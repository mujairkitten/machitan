using System;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// The volume of Windows' default speakers (or headphones), through Core Audio: the same
/// control the system's own volume slider moves. Use on the UI thread.
/// <see cref="Connect"/> again each time the flyout opens: the default device can change.
/// </summary>
internal sealed class VolumeService
{
    private static Guid s_noContext = Guid.Empty;

    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private string _lastProblem = string.Empty;

    /// <summary>The device's name as Windows shows it, or empty when unknown.</summary>
    internal string DeviceName { get; private set; } = string.Empty;

    internal bool IsAvailable => _volume is not null;

    /// <summary>Finds the current default output device. False when there is none or Core Audio fails.</summary>
    internal bool Connect()
    {
        ReleaseVolume();
        try
        {
            _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorObject();

            int hr = _enumerator.GetDefaultAudioEndpoint(CoreAudio.RenderFlow, CoreAudio.ConsoleRole, out IMMDevice? device);
            if (hr != 0 || device is null)
            {
                Note(hr == CoreAudio.ENotFound
                    ? "Volume: no speakers or headphones are set up in Windows; the volume control is hidden."
                    : "Volume: Windows did not name a default output device (0x" + hr.ToString("X8") + "); the volume control is hidden.");
                return false;
            }

            try
            {
                Guid volumeId = typeof(IAudioEndpointVolume).GUID;
                hr = device.Activate(ref volumeId, CoreAudio.ClsctxAll, 0, out object? instance);
                if (hr != 0 || instance is not IAudioEndpointVolume volume)
                {
                    Note("Volume: the output device's volume control could not be reached (0x" + hr.ToString("X8") + "); it is hidden.");
                    return false;
                }

                _volume = volume;
                DeviceName = ReadName(device);
                Note(string.Empty);
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception ex)
        {
            Note("Volume: Core Audio is not available (" + ex.GetType().Name + "); the volume control is hidden.");
            ReleaseVolume();
            return false;
        }
    }

    /// <summary>Level 0..1 and mute, as Windows has them now.</summary>
    internal bool TryRead(out float level, out bool muted)
    {
        level = 0;
        muted = false;
        if (_volume is null)
        {
            return false;
        }

        return _volume.GetMasterVolumeLevelScalar(out level) == 0
            && _volume.GetMute(out muted) == 0;
    }

    internal void SetLevel(float level)
    {
        if (_volume is null)
        {
            return;
        }

        int hr = _volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref s_noContext);
        if (hr != 0)
        {
            Note("Volume: Windows did not take the new level (0x" + hr.ToString("X8") + ").");
        }
    }

    internal void SetMute(bool mute)
    {
        if (_volume is null)
        {
            return;
        }

        int hr = _volume.SetMute(mute, ref s_noContext);
        if (hr != 0)
        {
            Note("Volume: Windows did not take the mute change (0x" + hr.ToString("X8") + ").");
        }
    }

    private static string ReadName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(CoreAudio.StgmRead, out store) != 0 || store is null)
            {
                return string.Empty;
            }

            PROPERTYKEY key = CoreAudio.DeviceFriendlyName;
            if (store.GetValue(ref key, out PROPVARIANT value) != 0)
            {
                return string.Empty;
            }

            try
            {
                return value.vt == CoreAudio.VtLpwstr && value.pointerValue != 0
                    ? Marshal.PtrToStringUni(value.pointerValue) ?? string.Empty
                    : string.Empty;
            }
            finally
            {
                Ole32.PropVariantClear(ref value);
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }
        finally
        {
            if (store is not null)
            {
                Marshal.ReleaseComObject(store);
            }
        }
    }

    private void ReleaseVolume()
    {
        if (_volume is not null)
        {
            Marshal.ReleaseComObject(_volume);
            _volume = null;
        }

        DeviceName = string.Empty;
    }

    /// <summary>Logs a problem once, not on every open.</summary>
    private void Note(string problem)
    {
        if (problem == _lastProblem)
        {
            return;
        }

        _lastProblem = problem;
        if (problem.Length > 0)
        {
            Log.Warn(problem);
        }
    }
}
