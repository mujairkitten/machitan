using System;
using System.Globalization;
using System.Management;
using System.Threading.Tasks;
using Machitan.Core;
using Microsoft.UI.Dispatching;

namespace Machitan.Shell;

/// <summary>
/// Brightness changes of a laptop's own screen as Windows reports them: WMI's
/// WmiMonitorBrightnessEvent, which carries the new level in percent. The v0.9.3 tests saw it
/// arrive within a quarter second of Windows' brightness flyout on Sam's PC, at every step.
/// For Dynaland's brightness level. Started off the UI thread (WMI can be slow to answer);
/// each change is passed to the UI thread. A screen without WMI brightness (most desktop
/// monitors) gives no events: logged once, not asked again this run, and Dynaland then has no
/// brightness level (Windows' own flyout is then shown again for it: WindowsFlyouts gets no
/// brightness change to confirm its hide).
/// </summary>
internal sealed class BrightnessWatch : IDisposable
{
    private readonly DispatcherQueue _queue;
    private ManagementEventWatcher? _watcher;
    private bool _starting;
    private bool _wanted;
    private bool _disposed;

    /// <summary>Windows gave no brightness events here: not asked again this run.</summary>
    private bool _unavailable;

    internal BrightnessWatch(DispatcherQueue queue)
    {
        _queue = queue;
    }

    /// <summary>A new brightness level, 0 to 100. UI thread.</summary>
    internal event Action<int>? Changed;

    /// <summary>Listening while wanted (Dynaland on screen with the option on). UI thread.</summary>
    internal void SetWanted(bool wanted)
    {
        if (_disposed)
        {
            return;
        }

        _wanted = wanted;
        if (wanted)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _wanted = false;
        Changed = null;
        Stop();
    }

    private void Start()
    {
        if (_watcher is not null || _starting || _unavailable)
        {
            return;
        }

        _starting = true;
        DispatcherQueue queue = _queue;
        _ = Task.Run(() =>
        {
            ManagementEventWatcher? watcher = null;
            try
            {
                watcher = new ManagementEventWatcher(
                    new ManagementScope(@"root\WMI"),
                    new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                watcher.EventArrived += (_, e) =>
                {
                    int level;
                    try
                    {
                        level = Convert.ToInt32(e.NewEvent["Brightness"], CultureInfo.InvariantCulture);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    queue.TryEnqueue(() =>
                    {
                        if (!_disposed && _wanted)
                        {
                            Changed?.Invoke(Math.Clamp(level, 0, 100));
                        }
                    });
                };
                watcher.Start();
                ManagementEventWatcher started = watcher;
                queue.TryEnqueue(() => OnStarted(started));
            }
            catch (Exception ex)
            {
                watcher?.Dispose();
                queue.TryEnqueue(() =>
                {
                    _starting = false;
                    _unavailable = true;
                    Log.Info("Brightness: Windows does not report brightness changes for this screen (" + ex.GetType().Name + "); Dynaland shows no brightness level, and Windows' own flyout shows for it.");
                });
            }
        });
    }

    private void OnStarted(ManagementEventWatcher watcher)
    {
        _starting = false;
        _watcher = watcher;
        if (!_wanted || _disposed)
        {
            Stop();
        }
    }

    private void Stop()
    {
        ManagementEventWatcher? watcher = _watcher;
        if (watcher is null)
        {
            return;
        }

        _watcher = null;
        _ = Task.Run(() =>
        {
            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch (Exception)
            {
                // Stopping only.
            }
        });
    }
}
