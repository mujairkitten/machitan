using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>How Machitan reaches a screen's brightness.</summary>
internal enum BrightnessKind
{
    /// <summary>A laptop's (or all-in-one's) own screen, through Windows' WMI.</summary>
    BuiltIn,

    /// <summary>An external monitor, over DDC/CI.</summary>
    External,
}

/// <summary>One screen whose brightness Machitan can set.</summary>
internal sealed class BrightnessDisplay
{
    internal BrightnessDisplay(BrightnessKind kind, int number, uint minimum, uint maximum, uint current)
    {
        Kind = kind;
        Number = number;
        Minimum = minimum;
        Maximum = maximum;
        Current = current;
    }

    internal BrightnessKind Kind { get; }

    /// <summary>External monitors: 1-based, in Windows' order of displays. Built-in: 0.</summary>
    internal int Number { get; }

    internal uint Minimum { get; }

    internal uint Maximum { get; }

    /// <summary>The level read when the screens were last checked.</summary>
    internal uint Current { get; }

    /// <summary>External monitors: the DDC/CI handle.</summary>
    internal nint Handle { get; init; }

    /// <summary>Built-in screen: WMI's name for it.</summary>
    internal string InstanceName { get; init; } = string.Empty;

    /// <summary>Built-in screen: the WMI object whose method sets the level.</summary>
    internal ManagementObject? Methods { get; init; }

    /// <summary>The newest level asked for and not yet sent; -1 when none.</summary>
    internal int Pending = -1;

    /// <summary>0..100 for a slider.</summary>
    internal double CurrentPercent => ToPercent(Current);

    internal double ToPercent(uint level)
    {
        return Maximum > Minimum ? (Math.Clamp(level, Minimum, Maximum) - Minimum) * 100.0 / (Maximum - Minimum) : 0;
    }

    internal uint FromPercent(double percent)
    {
        double clamped = Math.Clamp(percent, 0, 100);
        return Minimum + (uint)Math.Round((Maximum - Minimum) * clamped / 100.0);
    }
}

/// <summary>
/// Screen brightness. A laptop's own screen goes through Windows' WMI (the same control as
/// Windows' brightness slider); external monitors go over DDC/CI. Screens answer slowly, so
/// all of it runs off the UI thread, one call at a time; while a slider is being dragged only
/// the newest level is sent. Screens that do not answer are left out, and when none answers
/// there is no brightness control at all.
/// </summary>
internal sealed class BrightnessService
{
    private const string WmiScope = @"root\WMI";

    /// <summary>WmiSetBrightness's time-out, in seconds.</summary>
    private const uint WmiTimeoutSeconds = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<BrightnessDisplay> _displays = Array.Empty<BrightnessDisplay>();
    private int _writing;
    private bool _wmiUnavailable;
    private readonly HashSet<string> _notes = new();

    /// <summary>The screens found by the last check.</summary>
    internal IReadOnlyList<BrightnessDisplay> Displays => _displays;

    /// <summary>Checks which screens can change brightness and reads their levels. Runs in the background.</summary>
    internal Task<IReadOnlyList<BrightnessDisplay>> CheckAsync()
    {
        return Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ReleaseAll(_displays);
                _displays = Array.Empty<BrightnessDisplay>();

                var found = new List<BrightnessDisplay>();
                found.AddRange(ProbeBuiltIn());
                found.AddRange(ProbeExternal());
                _displays = found;
                return (IReadOnlyList<BrightnessDisplay>)found;
            }
            catch (Exception ex)
            {
                Log.Error("Brightness: checking the screens failed.", ex);
                return Array.Empty<BrightnessDisplay>();
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>
    /// The built-in screen's level as Windows has it now (it changes with the brightness keys).
    /// Empty when there is no built-in screen. Runs in the background.
    /// </summary>
    internal Task<IReadOnlyList<(BrightnessDisplay Display, double Percent)>> ReadBuiltInAsync()
    {
        return Task.Run(ReadBuiltInNowAsync);
    }

    private async Task<IReadOnlyList<(BrightnessDisplay Display, double Percent)>> ReadBuiltInNowAsync()
    {
        var levels = new List<(BrightnessDisplay Display, double Percent)>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool any = false;
            foreach (BrightnessDisplay display in _displays)
            {
                any |= display.Kind == BrightnessKind.BuiltIn;
            }

            if (any)
            {
                Dictionary<string, byte> current = ReadWmiLevels();
                foreach (BrightnessDisplay display in _displays)
                {
                    if (display.Kind == BrightnessKind.BuiltIn && current.TryGetValue(display.InstanceName, out byte level))
                    {
                        levels.Add((display, display.ToPercent(level)));
                    }
                }
            }
        }
        catch (Exception)
        {
            // Following outside changes is a nicety; a failed read just leaves the slider.
        }
        finally
        {
            _gate.Release();
        }

        return levels;
    }

    /// <summary>Asks for a new level (0..100). Sent in the background; only the newest counts.</summary>
    internal void Set(BrightnessDisplay display, double percent)
    {
        Interlocked.Exchange(ref display.Pending, (int)display.FromPercent(percent));
        if (Interlocked.CompareExchange(ref _writing, 1, 0) == 0)
        {
            _ = Task.Run(WriteLoopAsync);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (true)
            {
                bool sent = false;
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (BrightnessDisplay display in _displays)
                    {
                        int level = Interlocked.Exchange(ref display.Pending, -1);
                        if (level < 0)
                        {
                            continue;
                        }

                        sent = true;
                        if (display.Kind == BrightnessKind.External)
                        {
                            if (!Dxva2.SetMonitorBrightness(display.Handle, (uint)level))
                            {
                                NoteOnce("Brightness: display " + display.Number + " did not take the new level (error " + Marshal.GetLastPInvokeError() + ").");
                            }
                        }
                        else
                        {
                            SetBuiltIn(display, (uint)level);
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }

                if (!sent)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Brightness: setting the level failed.", ex);
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }

        // A level asked for just as the loop ended still goes out.
        foreach (BrightnessDisplay display in _displays)
        {
            if (Volatile.Read(ref display.Pending) >= 0 && Interlocked.CompareExchange(ref _writing, 1, 0) == 0)
            {
                _ = Task.Run(WriteLoopAsync);
                break;
            }
        }
    }

    private void SetBuiltIn(BrightnessDisplay display, uint level)
    {
        if (display.Methods is null)
        {
            return;
        }

        try
        {
            using ManagementBaseObject parameters = display.Methods.GetMethodParameters("WmiSetBrightness");
            parameters["Timeout"] = WmiTimeoutSeconds;
            parameters["Brightness"] = (byte)Math.Min(level, 100u);
            using ManagementBaseObject? result = display.Methods.InvokeMethod("WmiSetBrightness", parameters, null);
        }
        catch (Exception ex)
        {
            NoteOnce("Brightness: the built-in screen did not take the new level (" + ex.GetType().Name + ": " + ex.Message + ").");
        }
    }

    /// <summary>The laptop's own screen, through WMI. Once WMI says it has no such control, it is not asked again.</summary>
    private List<BrightnessDisplay> ProbeBuiltIn()
    {
        var found = new List<BrightnessDisplay>();
        if (_wmiUnavailable)
        {
            return found;
        }

        try
        {
            Dictionary<string, byte> current = ReadWmiLevels();
            if (current.Count == 0)
            {
                NoteOnce("Brightness: Windows lists no built-in screen with a brightness control.");
                return found;
            }

            using var methods = new ManagementClass(WmiScope, "WmiMonitorBrightnessMethods", null);
            using ManagementObjectCollection instances = methods.GetInstances();
            foreach (ManagementBaseObject instance in instances)
            {
                if (instance is ManagementObject method
                    && method["InstanceName"] is string name
                    && current.TryGetValue(name, out byte level))
                {
                    found.Add(new BrightnessDisplay(BrightnessKind.BuiltIn, 0, 0, 100, level)
                    {
                        InstanceName = name,
                        Methods = method,
                    });
                }
                else
                {
                    instance.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // Desktops (and some laptops) have no such control; WMI says "not supported".
            _wmiUnavailable = true;
            NoteOnce("Brightness: Windows offers no built-in screen brightness control here (" + ex.GetType().Name + ": " + ex.Message + ").");
        }

        return found;
    }

    /// <summary>Built-in screens' current levels (percent), by WMI instance name.</summary>
    private static Dictionary<string, byte> ReadWmiLevels()
    {
        var levels = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(WmiScope, "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                if (item["InstanceName"] is string name && item["CurrentBrightness"] is byte level)
                {
                    levels[name] = level;
                }
            }
        }

        return levels;
    }

    /// <summary>External monitors over DDC/CI. Uses raw pointers, so it is the only "unsafe" part.</summary>
    private unsafe List<BrightnessDisplay> ProbeExternal()
    {
        var monitors = new List<nint>();
        GCHandle handle = GCHandle.Alloc(monitors);
        try
        {
            User32.EnumDisplayMonitors(0, 0, &MonitorCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var found = new List<BrightnessDisplay>();
        for (int i = 0; i < monitors.Count; i++)
        {
            int number = i + 1;
            if (!Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(monitors[i], out uint count) || count == 0)
            {
                continue;
            }

            var physical = new PHYSICAL_MONITOR[count];
            fixed (PHYSICAL_MONITOR* first = physical)
            {
                if (!Dxva2.GetPhysicalMonitorsFromHMONITOR(monitors[i], count, first))
                {
                    continue;
                }
            }

            foreach (PHYSICAL_MONITOR monitor in physical)
            {
                // The DDC/CI question; a screen without it says no (a laptop's own screen does).
                if (Dxva2.GetMonitorBrightness(monitor.hPhysicalMonitor, out uint minimum, out uint current, out uint maximum)
                    && maximum > minimum)
                {
                    found.Add(new BrightnessDisplay(BrightnessKind.External, number, minimum, maximum, current)
                    {
                        Handle = monitor.hPhysicalMonitor,
                    });
                }
                else
                {
                    Dxva2.DestroyPhysicalMonitor(monitor.hPhysicalMonitor);
                }
            }
        }

        NoteOnce("Brightness: " + found.Count + " external screen(s) answer over DDC/CI (" + monitors.Count + " display(s) checked).");
        return found;
    }

    private static void ReleaseAll(IReadOnlyList<BrightnessDisplay> displays)
    {
        foreach (BrightnessDisplay display in displays)
        {
            if (display.Kind == BrightnessKind.External)
            {
                Dxva2.DestroyPhysicalMonitor(display.Handle);
            }
            else
            {
                display.Methods?.Dispose();
            }
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int MonitorCallback(nint monitor, nint hdc, RECT* area, nint lParam)
    {
        try
        {
            if (GCHandle.FromIntPtr(lParam).Target is List<nint> monitors)
            {
                monitors.Add(monitor);
                return 1;
            }

            return 0;
        }
        catch (Exception)
        {
            // An exception must never cross back into native code.
            return 0;
        }
    }

    /// <summary>Logs a finding once per run, not on every open.</summary>
    private void NoteOnce(string note)
    {
        lock (_notes)
        {
            if (!_notes.Add(note))
            {
                return;
            }
        }

        Log.Info(note);
    }
}
