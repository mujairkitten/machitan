using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Machitan's shortcuts while an app running as administrator is in front (regedit, Event
/// Viewer, Task Manager). Windows keeps the keys of such an app from every program that runs
/// with fewer rights, Machitan's keyboard hook included. So, when the user turns this on
/// (Settings > Shortcuts, off by default), a small part of Machitan runs as administrator:
/// Machitan.exe --adminkeys, with no window, only a keyboard hook. It acts only while an app
/// with more rights than Machitan is in front, catches the same keys Machitan's own hook
/// catches (a lone Win, Win+1..9 and the like), and hands them to Machitan, which does the rest.
///
/// It starts through a Windows scheduled task ("Machitan\AdminKeys", run with the highest
/// rights), made once when the option is turned on: Windows asks then (one prompt), and not
/// again at each start. Turning the option off ends the helper and removes the task (Windows
/// asks again). The task names this copy of Machitan.exe; another copy needs the option
/// turned on again.
///
/// This side lives in the shell (UI thread): a message-only window the helper finds, tells
/// itself to, and posts the caught keys to. The helper's side is <see cref="AdminKeysHelper"/>.
/// </summary>
internal sealed class AdminKeys : IDisposable
{
    internal const string Argument = "--adminkeys";
    internal const string HostClass = "MachitanAdminKeysHost";
    internal const string HelperClass = "MachitanAdminKeysHelper";
    internal const string TaskName = @"Machitan\AdminKeys";

    // Helper to shell (posted; a higher-rights process may post to a lower one).
    internal const uint MsgHello = User32.WM_APP + 0x31;
    internal const uint MsgLoneWin = User32.WM_APP + 0x32;
    internal const uint MsgShortcut = User32.WM_APP + 0x33;
    internal const uint MsgGoodbye = User32.WM_APP + 0x34;

    // Shell to helper (WM_COPYDATA, the one message the helper lets through from lower rights).
    internal const nuint DataShortcuts = 1;
    internal const nuint DataForeground = 2;
    internal const nuint DataQuit = 3;

    private static bool s_classRegistered;

    private readonly KeyboardService _keyboard;
    private readonly Action? _onLoneWin;
    private nint _hwnd;
    private nint _helper;

    private AdminKeys(KeyboardService keyboard, Action? onLoneWin)
    {
        _keyboard = keyboard;
        _onLoneWin = onLoneWin;
    }

    /// <summary>The running one, while the option is on.</summary>
    internal static AdminKeys? Current { get; private set; }

    /// <summary>True once the helper has said hello (and has not left).</summary>
    internal bool Connected => _helper != 0 && User32.IsWindow(_helper);

    /// <summary>Makes the window the helper looks for, then starts the helper through its task. UI thread.</summary>
    internal static AdminKeys? Start(KeyboardService keyboard, Action? onLoneWin)
    {
        Current?.Dispose();
        var keys = new AdminKeys(keyboard, onLoneWin);
        if (!keys.MakeWindow())
        {
            return null;
        }

        Current = keys;
        _ = RunTaskAsync();
        return keys;
    }

    public void Dispose()
    {
        if (_helper != 0 && User32.IsWindow(_helper))
        {
            SendToHelper(DataQuit, ReadOnlySpan<byte>.Empty, 500);
        }

        _helper = 0;
        if (_hwnd != 0)
        {
            User32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }

    /// <summary>
    /// Asks the helper (which runs as administrator, so Windows lets it) to let Machitan bring a
    /// window to the front over an app running as administrator. True when the helper said yes.
    /// </summary>
    internal bool AllowForeground()
    {
        return Connected && SendToHelper(DataForeground, ReadOnlySpan<byte>.Empty, 300) == 1;
    }

    // ---------------------------------------------------------------- the scheduled task

    /// <summary>Makes (or remakes) the task for this copy of Machitan.exe. Windows asks first. True when it was made.</summary>
    internal static async Task<bool> RegisterTaskAsync()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            Log.Warn("Admin keys: Machitan's own path is not known; the task was not made.");
            return false;
        }

        string folder = Path.Combine(Path.GetTempPath(), "Machitan");
        string xmlPath = Path.Combine(folder, "adminkeys-task.xml");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(xmlPath, TaskXml(exe), Encoding.Unicode);
            int code = await RunSchtasksAsync("/create /tn \"" + TaskName + "\" /xml \"" + xmlPath + "\" /f", elevated: true);
            if (code == 0)
            {
                Log.Info("Admin keys: the task was made (it starts " + exe + " " + Argument + ").");
                return true;
            }

            Log.Warn(code == ErrorCancelled
                ? "Admin keys: not turned on (Windows' question was answered no)."
                : "Admin keys: Windows did not make the task (schtasks ended with " + code + ").");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Admin keys: the task could not be made.", ex);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (Exception ex)
            {
                Log.Warn("Admin keys: the task's temporary file stayed behind.", ex);
            }
        }
    }

    /// <summary>Removes the task (Windows asks first). True when it is gone.</summary>
    internal static async Task<bool> RemoveTaskAsync()
    {
        int code = await RunSchtasksAsync("/delete /tn \"" + TaskName + "\" /f", elevated: true);
        if (code == 0)
        {
            Log.Info("Admin keys: the task was removed.");
            return true;
        }

        Log.Warn("Admin keys: the task was not removed (schtasks ended with " + code + "); it stays unused.");
        return false;
    }

    /// <summary>Starts the helper through its task (no prompt: the task already has the rights).</summary>
    private static async Task RunTaskAsync()
    {
        int code = await RunSchtasksAsync("/run /tn \"" + TaskName + "\"", elevated: false);
        if (code != 0)
        {
            Log.Warn("Admin keys: the helper's task did not start (schtasks ended with " + code + "); turn the option off and on again in Settings > Shortcuts.");
        }
    }

    private const int ErrorCancelled = 1223;

    private static Task<int> RunSchtasksAsync(string arguments, bool elevated)
    {
        return Task.Run(() =>
        {
            try
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
                {
                    UseShellExecute = elevated,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                if (elevated)
                {
                    start.Verb = "runas";
                }

                using Process? process = Process.Start(start);
                if (process is null)
                {
                    return -1;
                }

                if (!process.WaitForExit(60000))
                {
                    return -2;
                }

                return process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return ErrorCancelled;
            }
            catch (Exception ex)
            {
                Log.Warn("Admin keys: schtasks could not be run.", ex);
                return -1;
            }
        });
    }

    /// <summary>The task: no trigger (Machitan starts it), this user, the highest rights, no time limit.</summary>
    private static string TaskXml(string exe)
    {
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
            + "  <RegistrationInfo>\r\n"
            + "    <Description>Machitan: its keyboard shortcuts while an app running as administrator is in front. Started by Machitan; removed when the option is turned off.</Description>\r\n"
            + "  </RegistrationInfo>\r\n"
            + "  <Principals>\r\n"
            + "    <Principal id=\"Author\">\r\n"
            + "      <UserId>" + SecurityElement.Escape(user) + "</UserId>\r\n"
            + "      <LogonType>InteractiveToken</LogonType>\r\n"
            + "      <RunLevel>HighestAvailable</RunLevel>\r\n"
            + "    </Principal>\r\n"
            + "  </Principals>\r\n"
            + "  <Settings>\r\n"
            + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n"
            + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n"
            + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n"
            + "    <AllowHardTerminate>true</AllowHardTerminate>\r\n"
            + "    <StartWhenAvailable>false</StartWhenAvailable>\r\n"
            + "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n"
            + "    <Enabled>true</Enabled>\r\n"
            + "    <Hidden>false</Hidden>\r\n"
            + "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n"
            + "    <Priority>4</Priority>\r\n"
            + "  </Settings>\r\n"
            + "  <Actions Context=\"Author\">\r\n"
            + "    <Exec>\r\n"
            + "      <Command>" + SecurityElement.Escape(exe) + "</Command>\r\n"
            + "      <Arguments>" + Argument + "</Arguments>\r\n"
            + "    </Exec>\r\n"
            + "  </Actions>\r\n"
            + "</Task>\r\n";
    }

    // ---------------------------------------------------------------- the window the helper talks to

    private unsafe bool MakeWindow()
    {
        nint instance = Kernel32.GetModuleHandleW(null);
        if (!s_classRegistered)
        {
            fixed (char* className = HostClass)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = instance,
                    lpszClassName = className,
                };
                if (User32.RegisterClassExW(&windowClass) == 0 && Marshal.GetLastPInvokeError() != User32.ERROR_CLASS_ALREADY_EXISTS)
                {
                    Log.Warn("Admin keys: Machitan's window for the helper could not be set up.");
                    return false;
                }
            }

            s_classRegistered = true;
        }

        _hwnd = User32.CreateWindowEx(0, HostClass, string.Empty, 0, 0, 0, 0, 0, MessageOnlyParent, 0, instance, 0);
        if (_hwnd == 0)
        {
            Log.Warn("Admin keys: Machitan's window for the helper could not be made (error " + Marshal.GetLastPInvokeError() + ").");
            return false;
        }

        return true;
    }

    /// <summary>HWND_MESSAGE: a window that only receives messages.</summary>
    internal const nint MessageOnlyParent = -3;

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (Current is AdminKeys keys && keys._hwnd == hwnd)
            {
                switch (message)
                {
                    case MsgHello:
                        keys.OnHello(wParam);
                        return 0;
                    case MsgLoneWin:
                        keys._onLoneWin?.Invoke();
                        return 0;
                    case MsgShortcut:
                        keys._keyboard.RunByIndex((int)wParam);
                        return 0;
                    case MsgGoodbye:
                        keys._helper = 0;
                        Log.Info("Admin keys: the helper ended.");
                        return 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Admin keys: a message from the helper could not be handled.", ex);
        }

        return User32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>The helper is up: give it the shortcuts to catch.</summary>
    private void OnHello(nint helper)
    {
        if (!User32.IsWindow(helper) || User32.GetWindowThreadProcessId(helper, out uint process) == 0)
        {
            return;
        }

        _helper = helper;
        IReadOnlyList<(int Index, HotkeyDefinition Definition)> caught = _keyboard.CaughtByHook;
        byte[] table = new byte[4 + (caught.Count * 12)];
        BitConverter.TryWriteBytes(table.AsSpan(0, 4), caught.Count);
        for (int i = 0; i < caught.Count; i++)
        {
            int at = 4 + (i * 12);
            BitConverter.TryWriteBytes(table.AsSpan(at, 4), caught[i].Index);
            BitConverter.TryWriteBytes(table.AsSpan(at + 4, 4), caught[i].Definition.Modifiers);
            BitConverter.TryWriteBytes(table.AsSpan(at + 8, 4), caught[i].Definition.Key);
        }

        bool sent = SendToHelper(DataShortcuts, table, 2000) == 1;
        Log.Info("Admin keys: the helper is running (process " + process + "); "
            + (sent ? caught.Count + " shortcut(s) handed over." : "it did not take the shortcuts."));
    }

    private unsafe nint SendToHelper(nuint kind, ReadOnlySpan<byte> data, uint timeoutMs)
    {
        nint helper = _helper;
        if (helper == 0)
        {
            return 0;
        }

        fixed (byte* bytes = data)
        {
            var copy = new COPYDATASTRUCT { dwData = kind, cbData = (uint)data.Length, lpData = (nint)bytes };
            nint sent = User32.SendMessageTimeoutW(helper, User32.WM_COPYDATA, _hwnd, (nint)(&copy), User32.SMTO_ABORTIFHUNG, timeoutMs, out nint result);
            return sent == 0 ? 0 : result;
        }
    }
}
