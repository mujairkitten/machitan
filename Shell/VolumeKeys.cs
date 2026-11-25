using System;
using System.Collections.Concurrent;
using System.Threading;
using Machitan.Core;

namespace Machitan.Shell;

/// <summary>
/// Volume up, down and mute handled by Machitan instead of Windows (v0.9.3, Most wanted item 5;
/// the spike proved it on Sam's PC, 2026-11-25): the keyboard hook keeps the three keys from
/// Windows, so Windows' own volume flyout never shows, and Machitan changes the volume of
/// Windows' default speakers itself, as Windows would: 2 points a press (rounded to steps of 2),
/// up or down also unmutes, mute turns mute on or off. Dynaland shows the change at once, as it
/// shows any volume change.
///
/// Only while the option is on (Settings > Dynaland, on by default) and Dynaland is on screen;
/// otherwise (Dynaland off, or stepped aside for a full-screen app) the keys are Windows' own.
/// While an app running as administrator is in front, Windows hides the keys from Machitan's
/// hook; then the admin-keys helper (when that option is on) takes them and passes them on.
///
/// Core Audio runs on a thread of its own, so a slow audio device never holds up the keyboard
/// hook or the UI. Presses wait their turn there, in order.
/// </summary>
internal sealed class VolumeKeys : IDisposable
{
    private const uint VkVolumeMute = 0xAD;
    private const uint VkVolumeUp = 0xAF;
    private const float Step = 0.02f;

    private readonly Action _changed;
    private readonly BlockingCollection<uint> _presses = new();
    private VolumeService? _volume;
    private Thread? _thread;
    private bool _optionOn;
    private bool _dynalandShown;
    private bool _active;
    private bool _disposed;
    private bool _failNoted;

    /// <param name="changed">Called on the volume thread after each change (to show it at once).</param>
    internal VolumeKeys(Action changed)
    {
        _changed = changed;
    }

    /// <summary>Settings > Dynaland: the option. UI thread.</summary>
    internal void SetOption(bool on)
    {
        if (on == _optionOn)
        {
            return;
        }

        _optionOn = on;
        Log.Info(on
            ? "Volume keys: Machitan handles them while Dynaland is on screen (Windows' volume flyout stays away)."
            : "Volume keys: Windows' own (Settings > Dynaland).");
        Apply();
    }

    /// <summary>Dynaland on screen or not (it steps aside for full-screen apps). UI thread.</summary>
    internal void SetDynalandShown(bool shown)
    {
        if (shown == _dynalandShown)
        {
            return;
        }

        _dynalandShown = shown;
        Apply();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        KeyboardService.SetVolumeKeySink(null);
        AdminKeys.SetVolumeKeySink(null);
        _presses.CompleteAdding();
    }

    private void Apply()
    {
        bool active = !_disposed && _optionOn && _dynalandShown;
        if (active == _active)
        {
            return;
        }

        _active = active;
        if (active && _thread is null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Machitan volume keys" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        Action<uint>? sink = active ? Press : null;
        KeyboardService.SetVolumeKeySink(sink);
        AdminKeys.SetVolumeKeySink(sink);
    }

    /// <summary>A volume key Windows did not see (down only). Any thread: the hook's, or the UI's for the helper's.</summary>
    private void Press(uint key)
    {
        try
        {
            _presses.TryAdd(key);
        }
        catch (InvalidOperationException)
        {
            // Closing: the press is dropped.
        }
    }

    private void Run()
    {
        try
        {
            foreach (uint key in _presses.GetConsumingEnumerable())
            {
                StepVolume(key);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Volume keys: the volume thread stopped.", ex);
        }
    }

    /// <summary>What Windows would have done with the key. Volume thread.</summary>
    private void StepVolume(uint key)
    {
        try
        {
            // Again for every press: the default speakers can change at any time (headphones in).
            _volume ??= new VolumeService();
            if (!_volume.Connect() || !_volume.TryRead(out float level, out bool muted))
            {
                if (!_failNoted)
                {
                    _failNoted = true;
                    Log.Warn("Volume keys: a key could not change the volume (no speakers, or Windows did not answer).");
                }

                return;
            }

            _failNoted = false;
            if (key == VkVolumeMute)
            {
                _volume.SetMute(!muted);
            }
            else
            {
                float wanted = (float)(Math.Round((level + (key == VkVolumeUp ? Step : -Step)) * 50) / 50);
                _volume.SetLevel(wanted);
                if (muted)
                {
                    _volume.SetMute(false);
                }
            }

            _changed();
        }
        catch (Exception ex)
        {
            Log.Error("Volume keys: a key could not change the volume.", ex);
        }
    }
}
