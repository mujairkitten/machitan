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
/// Follows camera and microphone use the way Windows' own privacy indicator does: Windows
/// records each app's use under HKCU\Software\Microsoft\Windows\CurrentVersion\
/// CapabilityAccessManager\ConsentStore\webcam and \microphone (Store apps by package family
/// name, desktop apps under NonPackaged by exe path), with LastUsedTimeStart and
/// LastUsedTimeStop; while an app uses the device its stop time is 0. Machitan only reads
/// these values: it never opens the camera or the microphone. A desktop app counts only
/// while its exe is running, so a crashed app cannot leave a stale "in use". A background
/// thread is woken by Windows when the keys change, and checks every 3 seconds as well.
/// <see cref="Changed"/> is raised on the UI thread, only when the state changed.
/// </summary>
internal sealed class PrivacyMonitor : IDisposable
{
    private const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private static readonly string[] Devices = { "webcam", "microphone" };

    private readonly DispatcherQueue _queue;
    private ManualResetEvent? _stopSignal;
    private bool _running;
    private bool _readFailedNoted;

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
        try
        {
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
                Log.Warn("Privacy: Windows' camera and microphone records cannot be watched; checking every 3 seconds instead.");
            }

            var handles = new List<WaitHandle> { stop };
            handles.AddRange(signals);
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

                int which = WaitHandle.WaitAny(waitOn, TimeSpan.FromSeconds(3));
                if (which == 0)
                {
                    return;
                }

                if (which > 0 && which < waitOn.Length)
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
            foreach (nint key in keys)
            {
                Advapi32.RegCloseKey(key);
            }

            foreach (AutoResetEvent signal in signals)
            {
                signal.Dispose();
            }
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
        try
        {
            return new PrivacyState(ReadDevice("webcam"), ReadDevice("microphone"));
        }
        catch (Exception ex)
        {
            if (!_readFailedNoted)
            {
                _readFailedNoted = true;
                Log.Warn("Privacy: Windows' camera and microphone records could not be read.", ex);
            }

            return PrivacyState.None;
        }
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
            else if (IsInUse(root, name))
            {
                inUse.Add(new PrivacyUse(name, true));
            }
        }

        return inUse;
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
