using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;
using Windows.Networking.Connectivity;

namespace Machitan.Shell;

/// <summary>What the status ring shows for the network.</summary>
internal enum NetworkKind
{
    /// <summary>Not read yet.</summary>
    Unknown,

    WifiConnected,

    WifiConnecting,

    /// <summary>Wi-Fi is on but not connected to anything.</summary>
    NoSignal,

    /// <summary>The Wi-Fi radio is off (Windows' plane mode turns it off too).</summary>
    WifiOff,

    /// <summary>No Wi-Fi in use, but a cable (or a phone over USB) connects the PC.</summary>
    Wired,
}

/// <param name="Kind">The state.</param>
/// <param name="Name">The network's name as Windows lists it (Wi-Fi), or the connection's name (wired). Empty when unknown.</param>
/// <param name="Bars">Wi-Fi signal, 0..3 lit arcs. -1 when Windows gives no strength.</param>
internal readonly record struct NetworkState(NetworkKind Kind, string Name, int Bars);

/// <summary>
/// Follows the PC's network on a background thread: every 2 seconds (every second while a
/// Wi-Fi connection is being made), and at once when Windows reports a network change.
/// <see cref="Changed"/> is raised on the UI thread, only when something changed.
/// Sources: the Wi-Fi adapter's state and radio from Native Wifi (no location permission
/// needed), the network's name and signal bars from Windows' connection list.
/// </summary>
internal sealed class NetworkMonitor : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly AutoResetEvent _wake = new(false);
    private readonly HashSet<string> _noted = new();
    private RunState? _run;

    internal NetworkMonitor(DispatcherQueue queue)
    {
        _queue = queue;
    }

    /// <summary>On the UI thread.</summary>
    internal event Action<NetworkState>? Changed;

    internal NetworkState Current { get; private set; }

    internal void Start()
    {
        if (_run is not null)
        {
            return;
        }

        // Each start has its own stop flag, so a quick stop-and-start never leaves two
        // threads running (the old one ends at its next check).
        var run = new RunState();
        _run = run;
        try
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        catch (Exception ex)
        {
            Note("subscribe", "Network: Windows' network-change events are not available; checking every 2 seconds only.", ex);
        }

        var thread = new Thread(() => Run(run))
        {
            IsBackground = true,
            Name = "Machitan network",
        };
        thread.Start();
    }

    public void Dispose()
    {
        if (_run is not null)
        {
            _run.Stop = true;
            _run = null;
        }

        _wake.Set();
        try
        {
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        }
        catch (Exception)
        {
            // Never subscribed.
        }
    }

    private void OnNetworkStatusChanged(object sender)
    {
        _wake.Set();
    }

    private void Run(RunState run)
    {
        nint client = 0;
        NetworkState last = default;
        try
        {
            uint error = Wlanapi.WlanOpenHandle(Wlanapi.ClientVersion, 0, out _, out client);
            if (error != Wlanapi.ErrorSuccess)
            {
                client = 0;
                Note("open", "Network: the Wi-Fi service did not answer (error " + error + "); only wired connections are shown.", null);
            }
        }
        catch (Exception ex)
        {
            client = 0;
            Note("open", "Network: Native Wifi is not available on this PC; only wired connections are shown.", ex);
        }

        try
        {
            while (!run.Stop)
            {
                NetworkState state;
                try
                {
                    state = Read(client);
                }
                catch (Exception ex)
                {
                    Note("read", "Network: reading the network state failed.", ex);
                    state = last;
                }

                if (state != last)
                {
                    last = state;
                    _queue.TryEnqueue(() =>
                    {
                        if (run.Stop)
                        {
                            return;
                        }

                        Current = state;
                        Changed?.Invoke(state);
                    });
                }

                _wake.WaitOne(state.Kind == NetworkKind.WifiConnecting ? 1000 : 2000);
            }
        }
        finally
        {
            if (client != 0)
            {
                Wlanapi.WlanCloseHandle(client, 0);
            }
        }
    }

    private NetworkState Read(nint client)
    {
        NetworkKind wifi = client == 0 ? NetworkKind.Unknown : ReadWifiAdapter(client);

        ConnectionProfile? internet = null;
        try
        {
            internet = NetworkInformation.GetInternetConnectionProfile();
        }
        catch (Exception ex)
        {
            Note("profile", "Network: Windows' connection list could not be read.", ex);
        }

        if (wifi == NetworkKind.WifiConnected)
        {
            ConnectionProfile? wlan = internet is { IsWlanConnectionProfile: true } ? internet : FindWlanProfile();
            string name = wlan?.ProfileName ?? string.Empty;
            int bars = -1;
            try
            {
                byte? windowsBars = wlan?.GetSignalBars();
                if (windowsBars is byte b)
                {
                    // Windows counts 0..5; the ring has three arcs.
                    bars = b >= 4 ? 3 : b >= 2 ? 2 : b >= 1 ? 1 : 0;
                }
            }
            catch (Exception ex)
            {
                Note("bars", "Network: Windows gave no Wi-Fi signal strength; the arcs show as fully lit while connected.", ex);
            }

            if (bars < 0)
            {
                Note("bars", "Network: Windows gave no Wi-Fi signal strength; the arcs show as fully lit while connected.", null);
            }

            return new NetworkState(NetworkKind.WifiConnected, name, bars);
        }

        if (internet is not null && !internet.IsWlanConnectionProfile && IsUsable(internet))
        {
            string name = "Ethernet";
            try
            {
                if (internet.NetworkAdapter is { IanaInterfaceType: not 6 } && internet.ProfileName.Length > 0)
                {
                    // Not a network cable (a phone over USB, for example): its own name.
                    name = internet.ProfileName;
                }
            }
            catch (Exception)
            {
                // Keep "Ethernet".
            }

            return new NetworkState(NetworkKind.Wired, name, 0);
        }

        return wifi switch
        {
            NetworkKind.WifiConnecting => new NetworkState(NetworkKind.WifiConnecting, string.Empty, 0),
            NetworkKind.WifiOff => new NetworkState(NetworkKind.WifiOff, string.Empty, 0),
            _ => new NetworkState(NetworkKind.NoSignal, string.Empty, 0),
        };
    }

    /// <summary>The best state over all Wi-Fi adapters: connected, then connecting, then off or no signal.</summary>
    private NetworkKind ReadWifiAdapter(nint client)
    {
        uint error = Wlanapi.WlanEnumInterfaces(client, 0, out nint list);
        if (error != Wlanapi.ErrorSuccess || list == 0)
        {
            Note("enum", "Network: the Wi-Fi adapters could not be listed (error " + error + ").", null);
            return NetworkKind.Unknown;
        }

        try
        {
            int count = Marshal.ReadInt32(list);
            if (count <= 0)
            {
                return NetworkKind.Unknown;
            }

            bool connecting = false;
            Guid first = Guid.Empty;
            for (int i = 0; i < count; i++)
            {
                nint item = list + Wlanapi.InterfaceListHeader + (i * Wlanapi.InterfaceInfoSize);
                int state = Marshal.ReadInt32(item + Wlanapi.InterfaceStateOffset);
                if (i == 0)
                {
                    first = Marshal.PtrToStructure<Guid>(item);
                }

                switch (state)
                {
                    case Wlanapi.StateConnected:
                    case Wlanapi.StateAdHocFormed:
                        return NetworkKind.WifiConnected;
                    case Wlanapi.StateAssociating:
                    case Wlanapi.StateDiscovering:
                    case Wlanapi.StateAuthenticating:
                        connecting = true;
                        break;
                }
            }

            if (connecting)
            {
                return NetworkKind.WifiConnecting;
            }

            return IsRadioOff(client, first) ? NetworkKind.WifiOff : NetworkKind.NoSignal;
        }
        finally
        {
            Wlanapi.WlanFreeMemory(list);
        }
    }

    /// <summary>True when every radio of the adapter is switched off (in Windows or by a hardware switch).</summary>
    private static bool IsRadioOff(nint client, Guid adapter)
    {
        uint error = Wlanapi.WlanQueryInterface(client, ref adapter, Wlanapi.OpcodeRadioState, 0, out _, out nint data, out _);
        if (error != Wlanapi.ErrorSuccess || data == 0)
        {
            return false;
        }

        try
        {
            int phys = Math.Clamp(Marshal.ReadInt32(data), 0, 64);
            if (phys == 0)
            {
                return false;
            }

            for (int i = 0; i < phys; i++)
            {
                nint phy = data + Wlanapi.RadioPhyHeader + (i * Wlanapi.RadioPhySize);
                int software = Marshal.ReadInt32(phy + 4);
                int hardware = Marshal.ReadInt32(phy + 8);
                if (software != Wlanapi.RadioOff && hardware != Wlanapi.RadioOff)
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            Wlanapi.WlanFreeMemory(data);
        }
    }

    private ConnectionProfile? FindWlanProfile()
    {
        try
        {
            foreach (ConnectionProfile profile in NetworkInformation.GetConnectionProfiles())
            {
                if (profile.IsWlanConnectionProfile && IsUsable(profile))
                {
                    return profile;
                }
            }
        }
        catch (Exception ex)
        {
            Note("profiles", "Network: Windows' connection list could not be read.", ex);
        }

        return null;
    }

    private static bool IsUsable(ConnectionProfile profile)
    {
        try
        {
            return profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class RunState
    {
        public volatile bool Stop;
    }

    /// <summary>Logs each kind of problem once per run.</summary>
    private void Note(string key, string message, Exception? ex)
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
            Log.Warn(message);
        }
        else
        {
            Log.Warn(message, ex);
        }
    }
}
