using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace Machitan.Shell;

/// <summary>A switch in Win+A's action center.</summary>
internal enum QuickToggle
{
    WiFi,
    Bluetooth,
    AirplaneMode,
    MobileHotspot,
    LiveCaptions,
}

/// <summary>A switch's state, as Windows reports it.</summary>
internal enum SwitchState
{
    Off,
    On,

    /// <summary>Windows has the switch, but it cannot be used now (the adapter is turned off, or it is changing).</summary>
    Unavailable,
}

/// <summary>
/// The switches in Win+A's action center, each read from and set in Windows itself: Wi-Fi and
/// Bluetooth through Windows' radio API (Windows.Devices.Radios), airplane mode through Windows'
/// radio manager (the one Windows' own quick settings use; not in the SDK, see
/// Interop/RadioManager.cs), the mobile hotspot through Windows' tethering API (shown only while
/// Windows says the PC's internet connection can be shared), and live captions by Windows' own
/// LiveCaptions.exe (on while it runs). A switch Windows does not offer on this PC is not shown.
/// Call everything on the UI thread.
/// </summary>
internal sealed class QuickToggles
{
    private const string CaptionsProcess = "LiveCaptions";
    private const string AirplaneRecordKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState";

    private readonly HashSet<string> _noted = new();
    private readonly string _captionsPath = Path.Combine(Environment.SystemDirectory, CaptionsProcess + ".exe");
    private Radio? _wifi;
    private Radio? _bluetooth;
    private IRadioManager? _radioManager;
    private bool _radioManagerTried;
    private NetworkOperatorTetheringManager? _hotspot;

    /// <summary>The switches this PC has, in the order Win+A shows them.</summary>
    internal IReadOnlyList<QuickToggle> Available { get; private set; } = Array.Empty<QuickToggle>();

    /// <summary>Finds what Windows offers on this PC now (the radios, the hotspot can come and go).</summary>
    internal async Task CheckAsync()
    {
        var found = new List<QuickToggle>();
        await FindRadiosAsync();
        if (_wifi is not null)
        {
            found.Add(QuickToggle.WiFi);
        }

        if (_bluetooth is not null)
        {
            found.Add(QuickToggle.Bluetooth);
        }

        if (FindRadioManager())
        {
            found.Add(QuickToggle.AirplaneMode);
        }

        _hotspot = await Task.Run(() => FindHotspot());
        if (_hotspot is not null)
        {
            found.Add(QuickToggle.MobileHotspot);
        }

        if (File.Exists(_captionsPath))
        {
            found.Add(QuickToggle.LiveCaptions);
        }

        Available = found;
    }

    /// <summary>What Windows says the switch is now.</summary>
    internal SwitchState StateOf(QuickToggle toggle)
    {
        try
        {
            switch (toggle)
            {
                case QuickToggle.WiFi:
                    return RadioStateOf(_wifi);

                case QuickToggle.Bluetooth:
                    return RadioStateOf(_bluetooth);

                case QuickToggle.AirplaneMode:
                    if (_radioManager is not null && _radioManager.GetSystemRadioState(out int radiosEnabled, out _, out _) >= 0)
                    {
                        return radiosEnabled == 0 ? SwitchState.On : SwitchState.Off;
                    }

                    return SwitchState.Unavailable;

                case QuickToggle.MobileHotspot:
                    return _hotspot?.TetheringOperationalState switch
                    {
                        TetheringOperationalState.On => SwitchState.On,
                        TetheringOperationalState.Off => SwitchState.Off,
                        _ => SwitchState.Unavailable,
                    };

                case QuickToggle.LiveCaptions:
                    return CaptionsRunning() ? SwitchState.On : SwitchState.Off;
            }
        }
        catch (Exception ex)
        {
            Note("state-" + toggle, "Quick settings: the state of " + toggle + " could not be read.", ex);
        }

        return SwitchState.Unavailable;
    }

    /// <summary>Asks Windows to switch it on or off. True when Windows took the request.</summary>
    internal async Task<bool> SetAsync(QuickToggle toggle, bool on)
    {
        string what = toggle + (on ? " on" : " off");
        try
        {
            switch (toggle)
            {
                case QuickToggle.WiFi:
                case QuickToggle.Bluetooth:
                {
                    Radio? radio = toggle == QuickToggle.WiFi ? _wifi : _bluetooth;
                    if (radio is null)
                    {
                        return false;
                    }

                    RadioAccessStatus result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
                    if (result != RadioAccessStatus.Allowed)
                    {
                        Log.Warn("Quick settings: Windows did not switch " + what + " (" + result + ").");
                        return false;
                    }

                    return true;
                }

                case QuickToggle.AirplaneMode:
                {
                    if (_radioManager is null)
                    {
                        return false;
                    }

                    // Airplane mode on: the radios are not allowed on.
                    int hr = _radioManager.SetSystemRadioState(on ? 0 : 1);
                    if (hr < 0)
                    {
                        Log.Warn("Quick settings: Windows did not switch " + what + " (0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + ").");
                        return false;
                    }

                    return true;
                }

                case QuickToggle.MobileHotspot:
                {
                    if (_hotspot is null)
                    {
                        return false;
                    }

                    NetworkOperatorTetheringOperationResult result = on
                        ? await _hotspot.StartTetheringAsync()
                        : await _hotspot.StopTetheringAsync();
                    if (result.Status != TetheringOperationStatus.Success)
                    {
                        Log.Warn("Quick settings: Windows did not switch " + what + " (" + result.Status + ": " + result.AdditionalErrorMessage + ").");
                        return false;
                    }

                    return true;
                }

                case QuickToggle.LiveCaptions:
                    if (on)
                    {
                        Process.Start(new ProcessStartInfo(_captionsPath) { UseShellExecute = true })?.Dispose();
                    }
                    else
                    {
                        // As its own close button does.
                        foreach (Process process in Process.GetProcessesByName(CaptionsProcess))
                        {
                            using (process)
                            {
                                process.CloseMainWindow();
                            }
                        }
                    }

                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Quick settings: " + what + " could not be done.", ex);
        }

        return false;
    }

    /// <summary>
    /// The Wi-Fi network the PC is connected to, by the name Windows lists it under, or null: the
    /// one it uses for the internet, else any Wi-Fi it is connected to (a network without internet).
    /// </summary>
    internal static string? WifiName()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is { IsWlanConnectionProfile: true } && !string.IsNullOrWhiteSpace(profile.ProfileName))
            {
                return profile.ProfileName;
            }

            foreach (ConnectionProfile other in NetworkInformation.GetConnectionProfiles())
            {
                if (other.IsWlanConnectionProfile && !string.IsNullOrWhiteSpace(other.ProfileName)
                    && other.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
                {
                    return other.ProfileName;
                }
            }
        }
        catch (Exception)
        {
            // No name is a valid answer: the tile then says Not connected.
        }

        return null;
    }

    private async Task FindRadiosAsync()
    {
        _wifi = null;
        _bluetooth = null;
        try
        {
            RadioAccessStatus access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Note("radio-access", "Quick settings: Windows does not let Machitan switch the radios (" + access + "); no Wi-Fi or Bluetooth switch.");
                return;
            }

            IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
            foreach (Radio radio in radios)
            {
                if (radio.Kind == RadioKind.WiFi && _wifi is null)
                {
                    _wifi = radio;
                }
                else if (radio.Kind == RadioKind.Bluetooth && _bluetooth is null)
                {
                    _bluetooth = radio;
                }
            }
        }
        catch (Exception ex)
        {
            Note("radios", "Quick settings: Windows' radios could not be listed.", ex);
        }
    }

    private bool FindRadioManager()
    {
        if (_radioManagerTried)
        {
            return _radioManager is not null;
        }

        _radioManagerTried = true;
        try
        {
            var manager = (IRadioManager)new RadioManagementApi();
            int hr = manager.GetSystemRadioState(out int radiosEnabled, out int switchState, out int changeReason);
            if (hr >= 0)
            {
                _radioManager = manager;
                Log.Info("Quick settings: airplane mode found (radios allowed " + radiosEnabled + ", switch " + switchState
                    + ", reason " + changeReason + "; Windows' record " + ReadAirplaneRecord() + ").");
            }
            else
            {
                Log.Warn("Quick settings: Windows' radio manager gave no airplane mode state (0x" + hr.ToString("X8", CultureInfo.InvariantCulture)
                    + "); no airplane mode switch.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Quick settings: Windows' radio manager is not available; no airplane mode switch.", ex);
        }

        return _radioManager is not null;
    }

    /// <summary>On a worker thread: the hotspot of the PC's internet connection, when Windows lets it be shared.</summary>
    private NetworkOperatorTetheringManager? FindHotspot()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                // No internet connection to share.
                return null;
            }

            TetheringCapability capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
            if (capability != TetheringCapability.Enabled)
            {
                Note("hotspot-" + capability, "Quick settings: Windows reports the mobile hotspot as " + capability + "; no hotspot switch.");
                return null;
            }

            return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        }
        catch (Exception ex)
        {
            Note("hotspot", "Quick settings: the mobile hotspot could not be checked.", ex);
            return null;
        }
    }

    private static SwitchState RadioStateOf(Radio? radio)
    {
        return radio?.State switch
        {
            RadioState.On => SwitchState.On,
            RadioState.Off => SwitchState.Off,
            _ => SwitchState.Unavailable,
        };
    }

    private static bool CaptionsRunning()
    {
        Process[] found = Process.GetProcessesByName(CaptionsProcess);
        foreach (Process process in found)
        {
            process.Dispose();
        }

        return found.Length > 0;
    }

    /// <summary>Windows' own record of airplane mode, for the log (1 while it is on), or "none".</summary>
    private static string ReadAirplaneRecord()
    {
        try
        {
            return Registry.GetValue(AirplaneRecordKey, string.Empty, null)?.ToString() ?? "none";
        }
        catch (Exception)
        {
            return "none";
        }
    }

    /// <summary>Logs a message once per key (a missing switch would otherwise be logged at every Win+A).</summary>
    private void Note(string key, string message, Exception? ex = null)
    {
        lock (_noted)
        {
            if (!_noted.Add(key))
            {
                return;
            }
        }

        if (ex is null)
        {
            Log.Info(message);
        }
        else
        {
            Log.Warn(message, ex);
        }
    }
}
