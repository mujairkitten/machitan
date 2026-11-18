using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;

namespace Machitan.Visuals;

/// <summary>
/// Tells when the wallpaper may have changed, from what always changes with it: the
/// registry keys Windows writes the wallpaper settings to (HKCU\Control Panel\Desktop, and
/// ...\Explorer\Wallpapers for slideshows and per-screen pictures), watched on a background
/// thread, and the file Windows actually shows (TranscodedWallpaper and its per-screen
/// siblings in the Themes folder). Notices are bundled: <see cref="Changed"/> is raised on the
/// UI thread about a second after the last one, and once more 4 seconds later (Windows can
/// finish writing the picture after the registry). Settings unrelated to the wallpaper also
/// touch the Desktop key; the listener compares the real settings, so that costs only a check.
/// </summary>
internal sealed class WallpaperWatcher : IDisposable
{
    private static readonly string[] Keys =
    {
        @"Control Panel\Desktop",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers",
    };

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _settle;
    private readonly DispatcherQueueTimer _again;
    private ManualResetEvent? _stopSignal;
    private FileSystemWatcher? _files;
    private string _reason = string.Empty;
    private bool _running;

    internal WallpaperWatcher(DispatcherQueue queue)
    {
        _queue = queue;

        // Made first: the settle timer's handler uses it.
        _again = queue.CreateTimer();
        _again.Interval = TimeSpan.FromSeconds(4);
        _again.IsRepeating = false;
        _again.Tick += (_, _) => Raise(_reason + ", checked again");

        _settle = queue.CreateTimer();
        _settle.Interval = TimeSpan.FromMilliseconds(900);
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) =>
        {
            Raise(_reason);
            _again.Stop();
            _again.Start();
        };
    }

    /// <summary>On the UI thread, with a short reason for the log.</summary>
    internal event Action<string>? Changed;

    internal void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        var stop = new ManualResetEvent(false);
        _stopSignal = stop;
        var thread = new Thread(() => WatchRegistry(stop))
        {
            IsBackground = true,
            Name = "Machitan wallpaper",
        };
        thread.Start();

        try
        {
            string folder = Path.GetDirectoryName(WallpaperSampler.ShownFile) ?? string.Empty;
            if (folder.Length > 0 && Directory.Exists(folder))
            {
                _files = new FileSystemWatcher(folder, "Transcoded*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                _files.Changed += OnFileEvent;
                _files.Created += OnFileEvent;
                _files.Renamed += OnFileEvent;
                _files.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Wallpaper: the Themes folder cannot be watched; changes are still noticed through the registry.", ex);
            _files = null;
        }
    }

    internal void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _settle.Stop();
        _again.Stop();
        try
        {
            _stopSignal?.Set();
        }
        catch (ObjectDisposedException)
        {
            // The thread already ended on its own.
        }

        _stopSignal = null;

        if (_files is not null)
        {
            _files.EnableRaisingEvents = false;
            _files.Changed -= OnFileEvent;
            _files.Created -= OnFileEvent;
            _files.Renamed -= OnFileEvent;
            _files.Dispose();
            _files = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        _queue.TryEnqueue(() => Notice("the wallpaper file changed"));
    }

    /// <summary>On the UI thread: (re)starts the short wait, so a burst of writes gives one check.</summary>
    private void Notice(string reason)
    {
        if (!_running)
        {
            return;
        }

        _reason = reason;
        _settle.Stop();
        _settle.Start();
    }

    private void Raise(string reason)
    {
        if (!_running)
        {
            return;
        }

        try
        {
            Changed?.Invoke(reason);
        }
        catch (Exception ex)
        {
            Log.Error("Wallpaper: following a change failed.", ex);
        }
    }

    /// <summary>Background thread: waits on the registry keys until stopped.</summary>
    private void WatchRegistry(ManualResetEvent stop)
    {
        var keys = new List<nint>();
        var signals = new List<AutoResetEvent>();
        try
        {
            foreach (string path in Keys)
            {
                if (Advapi32.RegOpenKeyEx(Advapi32.HKEY_CURRENT_USER, path, 0, Advapi32.KEY_NOTIFY, out nint key) == 0)
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
                Log.Warn("Wallpaper: the registry cannot be watched; the status ring checks the wallpaper every 30 seconds instead.");
                return;
            }

            var handles = new List<WaitHandle> { stop };
            handles.AddRange(signals);
            WaitHandle[] waitOn = handles.ToArray();
            while (true)
            {
                int which = WaitHandle.WaitAny(waitOn);
                if (which <= 0 || which >= waitOn.Length)
                {
                    return;
                }

                // A notice fires once; ask for the next one before passing this one on.
                Arm(keys[which - 1], signals[which - 1]);
                _queue.TryEnqueue(() => Notice("Windows' wallpaper settings changed"));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Wallpaper: watching the registry stopped; the status ring checks the wallpaper every 30 seconds instead.", ex);
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

            // The stop signal is left to .NET: Stop may still set it from the UI thread.
        }
    }

    private static bool Arm(nint key, AutoResetEvent signal)
    {
        return Advapi32.RegNotifyChangeKeyValue(
            key,
            false,
            Advapi32.REG_NOTIFY_CHANGE_LAST_SET,
            signal.SafeWaitHandle.DangerousGetHandle(),
            true) == 0;
    }
}
