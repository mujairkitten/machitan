using System;
using System.Globalization;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;
using Color = Windows.UI.Color;

namespace Machitan.Visuals;

/// <summary>
/// Machitan's accent colour (spec, Batch 7). By default it follows the wallpaper: the picture
/// Windows shows on the main screen is read natively (WallpaperSampler, the same file and
/// settings the status ring uses) and its strongest colour is taken, lightened enough to read
/// on dark surfaces (the same pick as Dynaland's track colour). When the wallpaper has no real
/// colour (black and white, grey), cannot be read, or the user chose so in Settings, it is
/// Windows' own accent colour. Follows wallpaper changes and Windows' accent changes.
/// <see cref="Changed"/> is raised on the UI thread.
/// </summary>
internal sealed class AccentService : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly UISettings _ui = new();
    private readonly WallpaperWatcher _watcher;
    private AccentSource _source;
    private Color? _wallpaperAccent;
    private string _wallpaperNote = "not read yet";
    private string _lastLogged = string.Empty;
    private int _generation;
    private bool _started;
    private bool _disposed;

    internal AccentService(DispatcherQueue queue)
    {
        _queue = queue;
        _watcher = new WallpaperWatcher(queue);
        _watcher.Changed += _ => ReadWallpaper();
        _ui.ColorValuesChanged += OnWindowsColours;
        Current = WindowsAccent();
    }

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    internal event Action? Changed;

    /// <summary>The accent colour now.</summary>
    internal Color Current { get; private set; }

    /// <summary>Starts following the wallpaper (UI thread).</summary>
    internal void Start(AccentSource source)
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _source = source;
        _watcher.Start();
        ReadWallpaper();
    }

    /// <summary>The setting changed (UI thread).</summary>
    internal void SetSource(AccentSource source)
    {
        if (!_started || _disposed || source == _source)
        {
            return;
        }

        _source = source;
        Recompute();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ui.ColorValuesChanged -= OnWindowsColours;
        _watcher.Dispose();
    }

    private void OnWindowsColours(UISettings sender, object args)
    {
        // Raised on a background thread.
        _queue.TryEnqueue(Recompute);
    }

    private async void ReadWallpaper()
    {
        int generation = ++_generation;
        Color? accent = null;
        string note;
        try
        {
            // The main screen (no window: Windows answers with the primary monitor).
            User32.TryGetMonitorRect(0, out RECT primary);
            WallpaperSampler? sampler = await WallpaperSampler.LoadAsync(primary);
            if (sampler is null)
            {
                note = "the wallpaper could not be read";
            }
            else
            {
                accent = sampler.Accent();
                note = accent is null ? "the wallpaper (" + sampler.Kind + ") has no real colour" : sampler.Kind;
            }
        }
        catch (Exception ex)
        {
            note = "reading the wallpaper failed (" + ex.GetType().Name + ")";
        }

        if (_disposed || generation != _generation)
        {
            return;
        }

        _wallpaperAccent = accent;
        _wallpaperNote = note;
        Recompute();
    }

    private void Recompute()
    {
        if (_disposed)
        {
            return;
        }

        Color colour;
        string origin;
        if (_source == AccentSource.Wallpaper && _wallpaperAccent is Color wallpaper)
        {
            colour = wallpaper;
            origin = "the wallpaper, " + _wallpaperNote;
        }
        else
        {
            colour = WindowsAccent();
            origin = _source == AccentSource.Wallpaper
                ? "Windows' accent colour, as " + _wallpaperNote
                : "Windows' accent colour, as chosen in Settings";
        }

        string line = "Accent colour: #" + colour.R.ToString("X2", CultureInfo.InvariantCulture)
            + colour.G.ToString("X2", CultureInfo.InvariantCulture)
            + colour.B.ToString("X2", CultureInfo.InvariantCulture) + " from " + origin + ".";
        if (!string.Equals(line, _lastLogged, StringComparison.Ordinal))
        {
            _lastLogged = line;
            Log.Info(line);
        }

        if (!colour.Equals(Current))
        {
            Current = colour;
            Changed?.Invoke();
        }
    }

    private Color WindowsAccent()
    {
        try
        {
            return _ui.GetColorValue(UIColorType.Accent);
        }
        catch (Exception)
        {
            return Microsoft.UI.ColorHelper.FromArgb(255, 76, 194, 255);
        }
    }
}
