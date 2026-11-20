using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace Machitan.Shell;

/// <summary>One app using the camera or the microphone right now.</summary>
/// <param name="Key">Windows' name for the app: a package family name, or an exe path for a desktop app.</param>
/// <param name="Packaged">True for a Store (packaged) app.</param>
public sealed record PrivacyUse(string Key, bool Packaged)
{
    /// <summary>The exe path of a desktop app (Windows writes it with # for \).</summary>
    public string ExePath => Packaged ? string.Empty : Key.Replace('#', '\\');
}

/// <summary>Who uses the camera and the microphone right now.</summary>
public sealed record PrivacyState(IReadOnlyList<PrivacyUse> Camera, IReadOnlyList<PrivacyUse> Microphone)
{
    public static readonly PrivacyState None = new(Array.Empty<PrivacyUse>(), Array.Empty<PrivacyUse>());

    public bool Any => Camera.Count > 0 || Microphone.Count > 0;

    /// <summary>A text that changes exactly when the state does.</summary>
    public string Signature
    {
        get
        {
            var text = new StringBuilder();
            foreach (PrivacyUse use in Camera)
            {
                text.Append("c:").Append(use.Key).Append('|');
            }

            foreach (PrivacyUse use in Microphone)
            {
                text.Append("m:").Append(use.Key).Append('|');
            }

            return text.ToString();
        }
    }
}

/// <summary>
/// Follows camera and microphone use. Machitan only reads what Windows reports: it never
/// opens the camera or the microphone. Three sources, joined:
/// - Microphone: Core Audio's recording sessions (MicActivity), checked every 1.5 seconds.
/// - Camera: Media Foundation's sensor activity monitor (CameraActivity), which Windows
///   calls whenever a camera's users change.
/// - Windows' older records under HKCU\Software\Microsoft\Windows\CurrentVersion\
///   CapabilityAccessManager\ConsentStore\webcam and \microphone (LastUsedTimeStop 0 while
///   in use). Newer Windows builds stopped writing these (it keeps them in a database instead),
///   which is why the dots never lit with them alone; they still count where Windows writes
///   them. An app from the records counts only while it runs, so a crashed app cannot leave a
///   stale "in use".
/// A background thread wakes on Windows' notices and every 1.5 seconds.
/// <see cref="Changed"/> is raised on the UI thread, only when the state changed.
/// </summary>
internal sealed class PrivacyMonitor : IDisposable
{
    private const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private static readonly string[] Devices = { "webcam", "microphone" };
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(1500);

    private readonly DispatcherQueue _queue;
    private ManualResetEvent? _stopSignal;
    private bool _running;
    private bool _readFailedNoted;
    private bool _micFailedNoted;
    private CameraActivity? _camera;

    internal PrivacyMonitor(DispatcherQueue queue)
    {
        _queue = queue;
    }

    /// <summary>On the UI thread.</summary>
    internal event Action<PrivacyState>? Changed;

    internal PrivacyState Current { get; private set; } = PrivacyState.None;

    internal void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        var stop = new ManualResetEvent(false);
        _stopSignal = stop;
        var thread = new Thread(() => Run(stop))
        {
            IsBackground = true,
            Name = "Machitan privacy",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _stopSignal?.Set();
        }
        catch (ObjectDisposedException)
        {
            // Ended on its own.
        }

        _stopSignal = null;
    }

    private void Run(ManualResetEvent stop)
    {
        var keys = new List<nint>();
        var signals = new List<AutoResetEvent>();
        var cameraSignal = new AutoResetEvent(false);
        int com = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED);
        try
        {
            _camera = CameraActivity.TryStart(() => SetQuietly(cameraSignal), out string cameraProblem);
            Log.Info(_camera is not null
                ? "Privacy: camera use comes from Windows' camera monitor; microphone use from Windows' audio sessions."
                : "Privacy: camera use comes from Windows' records only: " + cameraProblem);

            foreach (string device in Devices)
            {
                if (Advapi32.RegOpenKeyEx(Advapi32.HKEY_CURRENT_USER, StoreKey + device, 0, Advapi32.KEY_NOTIFY, out nint key) == 0)
                {
                    var signal = new AutoResetEvent(false);
                    if (Arm(key, signal))
                    {
                        keys.Add(key);
                        signals.Add(signal);
                        continue;
                    }

                    signal.Dispose();
                    Advapi32.RegCloseKey(key);
                }
            }

            if (keys.Count == 0)
            {
                Log.Info("Privacy: Windows' older camera and microphone records cannot be watched; they are read every 1.5 seconds.");
            }

            // stop, then the record keys' signals, then the camera's.
            var handles = new List<WaitHandle> { stop };
            handles.AddRange(signals);
            handles.Add(cameraSignal);
            WaitHandle[] waitOn = handles.ToArray();
            string last = "\0";
            while (true)
            {
                PrivacyState state = Read();
                string signature = state.Signature;
                if (signature != last)
                {
                    last = signature;
                    _queue.TryEnqueue(() =>
                    {
                        if (!_running)
                        {
                            return;
                        }

                        Current = state;
                        Changed?.Invoke(state);
                    });
                }

                int which = WaitHandle.WaitAny(waitOn, CheckEvery);
                if (which == 0)
                {
                    return;
                }

                if (which > 0 && which <= keys.Count)
                {
                    // A notice fires once; ask for the next one. Give Windows a moment to finish writing.
                    Arm(keys[which - 1], signals[which - 1]);
                    if (stop.WaitOne(150))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Privacy: following camera and microphone use stopped.", ex);
        }
        finally
        {
            try
            {
                _camera?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Privacy: the camera monitor did not stop cleanly.", ex);
            }

            _camera = null;
            foreach (nint key in keys)
            {
                Advapi32.RegCloseKey(key);
            }

            foreach (AutoResetEvent signal in signals)
            {
                signal.Dispose();
            }

            cameraSignal.Dispose();
            if (com >= 0)
            {
                Ole32.CoUninitialize();
            }
        }
    }

    /// <summary>Windows' camera thread: wake the privacy thread (which may be ending).</summary>
    private static void SetQuietly(AutoResetEvent signal)
    {
        try
        {
            signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // The thread already ended.
        }
    }

    private static bool Arm(nint key, AutoResetEvent signal)
    {
        return Advapi32.RegNotifyChangeKeyValue(
            key,
            true,
            Advapi32.REG_NOTIFY_CHANGE_LAST_SET | Advapi32.REG_NOTIFY_CHANGE_NAME,
            signal.SafeWaitHandle.DangerousGetHandle(),
            true) == 0;
    }

    private PrivacyState Read()
    {
        var camera = new List<PrivacyUse>();
        var microphone = new List<PrivacyUse>();

        if (_camera is CameraActivity watch)
        {
            AddProcesses(camera, watch.Streaming);
        }

        try
        {
            List<uint>? recording = MicActivity.RecordingProcesses();
            if (recording is not null)
            {
                AddProcesses(microphone, recording);
            }
            else if (!_micFailedNoted)
            {
                _micFailedNoted = true;
                Log.Warn("Privacy: Windows' audio sessions could not be read; microphone use comes from Windows' records only.");
            }
        }
        catch (Exception ex)
        {
            if (!_micFailedNoted)
            {
                _micFailedNoted = true;
                Log.Warn("Privacy: Windows' audio sessions could not be read; microphone use comes from Windows' records only.", ex);
            }
        }

        try
        {
            AddAll(camera, ReadDevice("webcam"));
            AddAll(microphone, ReadDevice("microphone"));
        }
        catch (Exception ex)
        {
            if (!_readFailedNoted)
            {
                _readFailedNoted = true;
                Log.Warn("Privacy: Windows' camera and microphone records could not be read.", ex);
            }
        }

        return camera.Count == 0 && microphone.Count == 0 ? PrivacyState.None : new PrivacyState(camera, microphone);
    }

    /// <summary>Each process as Windows names apps in its records: package family, or exe path with # for \.</summary>
    private static void AddProcesses(List<PrivacyUse> uses, IEnumerable<uint> processIds)
    {
        foreach (uint processId in processIds)
        {
            if (processId == (uint)Environment.ProcessId)
            {
                continue;
            }

            ProcessIdentity identity = WindowInfo.GetProcessIdentity(processId);
            if (identity.PackageFullName is string fullName && FamilyOf(fullName) is string family)
            {
                Add(uses, new PrivacyUse(family, true));
            }
            else if (identity.Path is string path && path.Length > 0)
            {
                Add(uses, new PrivacyUse(path.Replace('\\', '#'), false));
            }
        }
    }

    private static void AddAll(List<PrivacyUse> uses, List<PrivacyUse> more)
    {
        foreach (PrivacyUse use in more)
        {
            Add(uses, use);
        }
    }

    private static void Add(List<PrivacyUse> uses, PrivacyUse use)
    {
        foreach (PrivacyUse known in uses)
        {
            if (known.Packaged == use.Packaged && string.Equals(known.Key, use.Key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        uses.Add(use);
    }

    /// <summary>"Name_Version_Arch_Resource_Publisher" -> "Name_Publisher" (package names hold no underscore).</summary>
    private static string? FamilyOf(string fullName)
    {
        string[] parts = fullName.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[^1] : null;
    }

    private static List<PrivacyUse> ReadDevice(string device)
    {
        var inUse = new List<PrivacyUse>();
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(StoreKey + device);
        if (root is null)
        {
            return inUse;
        }

        foreach (string name in root.GetSubKeyNames())
        {
            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                using RegistryKey? desktop = root.OpenSubKey(name);
                if (desktop is null)
                {
                    continue;
                }

                foreach (string exe in desktop.GetSubKeyNames())
                {
                    if (IsInUse(desktop, exe))
                    {
                        var use = new PrivacyUse(exe, false);
                        if (IsRunning(use.ExePath))
                        {
                            inUse.Add(use);
                        }
                    }
                }
            }
            else if (IsInUse(root, name) && PackageRuns(name))
            {
                inUse.Add(new PrivacyUse(name, true));
            }
        }

        return inUse;
    }

    /// <summary>True when a process of this package family runs (rarely asked: only for a record that says "in use").</summary>
    private static bool PackageRuns(string family)
    {
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                ProcessIdentity identity = WindowInfo.GetProcessIdentity((uint)process.Id);
                if (identity.PackageFullName is string fullName
                    && string.Equals(FamilyOf(fullName), family, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // A process that ended while being looked at.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    /// <summary>Started and not stopped: Windows' own "in use" rule.</summary>
    private static bool IsInUse(RegistryKey parent, string name)
    {
        using RegistryKey? app = parent.OpenSubKey(name);
        if (app is null)
        {
            return false;
        }

        long start = AsLong(app.GetValue("LastUsedTimeStart"));
        long stop = AsLong(app.GetValue("LastUsedTimeStop"));
        return start > 0 && stop == 0;
    }

    private static long AsLong(object? value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            byte[] { Length: 8 } bytes => BitConverter.ToInt64(bytes, 0),
            _ => 0,
        };
    }

    private static bool IsRunning(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        if (name.Length == 0)
        {
            return false;
        }

        Process[] processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}
