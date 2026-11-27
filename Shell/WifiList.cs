using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace Machitan.Shell;

/// <summary>Whether the Wi-Fi list can be shown.</summary>
internal enum WifiListState
{
    /// <summary>Windows lists the networks.</summary>
    Ready,

    /// <summary>
    /// Windows does not let Machitan see the networks. Since Windows 11 24H2 an app may only
    /// list Wi-Fi networks while it may use the location ("Let desktop apps access your location").
    /// </summary>
    NoLocation,

    /// <summary>Windows reports no Wi-Fi adapter.</summary>
    NoAdapter,

    /// <summary>Windows' Wi-Fi API failed (the reason is in diagnostics.log).</summary>
    Failed,
}

/// <summary>How asking Windows to connect ended.</summary>
internal enum WifiConnectResult
{
    Connected,

    /// <summary>The key (or user name and password) is wrong, or Windows has none saved.</summary>
    WrongKey,

    /// <summary>The network is out of reach now.</summary>
    Gone,

    Timeout,

    /// <summary>Windows cannot sign in to this network this way (certificates and the like).</summary>
    Unsupported,

    /// <summary>Windows took back Machitan's Wi-Fi access.</summary>
    NoAccess,

    Failed,
}

/// <summary>
/// A network in the list, as Windows reports it: one per name (several access points of one
/// network are one, the strongest); all hidden networks are one "Hidden network".
/// </summary>
internal sealed class WifiEntry
{
    // NetworkAuthenticationType, compared by number (Owe came in SDK 19041).
    private const int AuthNone = 0;
    private const int AuthOpen = 2;
    private const int AuthWpa = 4;
    private const int AuthRsna = 7;
    private const int AuthWpa3Enterprise192 = 10;
    private const int AuthOwe = 12;
    private const int AuthWpa3Enterprise = 13;

    internal WifiEntry(WiFiAvailableNetwork network, string ssid, bool connected, bool internet)
    {
        Network = network;
        Ssid = ssid;
        Connected = connected;
        Internet = internet;
        Bars = Math.Clamp((int)network.SignalBars, 0, 4);

        // Unknown counts as secured: Windows then asks for the key only if it needs one.
        int auth = network.SecuritySettings is NetworkSecuritySettings security ? (int)security.NetworkAuthenticationType : 1;
        Secured = auth is not (AuthNone or AuthOpen or AuthOwe);
        Enterprise = auth is AuthWpa or AuthRsna or AuthWpa3Enterprise192 or AuthWpa3Enterprise;
    }

    internal WiFiAvailableNetwork Network { get; }

    /// <summary>The network's name; empty for a hidden network.</summary>
    internal string Ssid { get; }

    internal bool Hidden => Ssid.Length == 0;

    /// <summary>Windows' signal bars, 0 to 4.</summary>
    internal int Bars { get; }

    /// <summary>It needs a key (or a user name and password).</summary>
    internal bool Secured { get; }

    /// <summary>It signs in with a user name and password (a work or school network).</summary>
    internal bool Enterprise { get; }

    /// <summary>The PC is connected to it.</summary>
    internal bool Connected { get; }

    /// <summary>Connected, and Windows says it reaches the internet.</summary>
    internal bool Internet { get; }
}

/// <summary>
/// The Wi-Fi networks nearby, through Windows' own Wi-Fi API (Windows.Devices.WiFi): look for
/// networks, list them, connect (with Windows' saved key, or one typed in) and disconnect.
/// Windows keeps the networks it connects to in its own list of known networks, as when
/// connecting from its own quick settings; Machitan keeps nothing. Used only while Win+A's
/// Wi-Fi list is open: since Windows 11 24H2 listing networks counts as using the location,
/// and Windows shows its location icon meanwhile. Call everything on the UI thread.
/// </summary>
internal sealed class WifiList
{
    private readonly HashSet<string> _noted = new();
    private WiFiAdapter? _adapter;
    private bool _listening;

    /// <summary>Raised on a worker thread when Windows' list of networks changed.</summary>
    internal event Action? Changed;

    /// <summary>Asks Windows for access and finds the adapter; while open, Windows' changes raise Changed.</summary>
    internal async Task<WifiListState> OpenAsync()
    {
        try
        {
            if (_adapter is null)
            {
                WiFiAccessStatus access = await WiFiAdapter.RequestAccessAsync();
                if (access != WiFiAccessStatus.Allowed)
                {
                    Log.Info("Wi-Fi list: Windows does not let Machitan see the Wi-Fi networks (" + access
                        + "); location access for desktop apps is off, or was refused for Machitan.");
                    return access == WiFiAccessStatus.Unspecified ? WifiListState.Failed : WifiListState.NoLocation;
                }

                IReadOnlyList<WiFiAdapter> adapters = await WiFiAdapter.FindAllAdaptersAsync();
                if (adapters.Count == 0)
                {
                    Log.Info("Wi-Fi list: Windows reports no Wi-Fi adapter.");
                    return WifiListState.NoAdapter;
                }

                // Two openings at once keep the first one's adapter.
                _adapter ??= adapters[0];
            }

            Listen(true);
            return WifiListState.Ready;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn("Wi-Fi list: Windows refused access (location access for desktop apps is off?).", ex);
            return WifiListState.NoLocation;
        }
        catch (Exception ex)
        {
            Log.Error("Wi-Fi list: Windows' Wi-Fi API could not be opened.", ex);
            return WifiListState.Failed;
        }
    }

    /// <summary>Stops following Windows' changes and lets the adapter go (found again at the next opening).</summary>
    internal void Close()
    {
        Listen(false);
        _adapter = null;
    }

    /// <summary>Asks Windows to look for networks now (a few seconds). False when it could not.</summary>
    internal async Task<bool> ScanAsync()
    {
        WiFiAdapter? adapter = _adapter;
        if (adapter is null)
        {
            return false;
        }

        try
        {
            await adapter.ScanAsync();
            return true;
        }
        catch (Exception ex)
        {
            Note("scan", "Wi-Fi list: Windows could not look for networks.", ex);
            return false;
        }
    }

    /// <summary>Windows' latest list: the connected network first, then the strongest; the hidden one last.</summary>
    internal async Task<List<WifiEntry>> ReadAsync()
    {
        var entries = new List<WifiEntry>();
        WiFiAdapter? adapter = _adapter;
        if (adapter is null)
        {
            return entries;
        }

        try
        {
            (string? connected, bool internet) = await ConnectedAsync(adapter);
            var best = new Dictionary<string, WiFiAvailableNetwork>(StringComparer.Ordinal);
            foreach (WiFiAvailableNetwork network in adapter.NetworkReport.AvailableNetworks)
            {
                // Not in Windows' own list either: Wi-Fi Direct devices and ad hoc networks.
                if (network.IsWiFiDirect || network.NetworkKind == WiFiNetworkKind.Adhoc)
                {
                    continue;
                }

                string ssid = SsidOf(network);
                if (!best.TryGetValue(ssid, out WiFiAvailableNetwork? kept)
                    || network.NetworkRssiInDecibelMilliwatts > kept.NetworkRssiInDecibelMilliwatts)
                {
                    best[ssid] = network;
                }
            }

            foreach (KeyValuePair<string, WiFiAvailableNetwork> pair in best)
            {
                bool isConnected = connected is not null && pair.Key.Length > 0 && string.Equals(pair.Key, connected, StringComparison.Ordinal);
                entries.Add(new WifiEntry(pair.Value, pair.Key, isConnected, isConnected && internet));
            }

            entries.Sort(Compare);
        }
        catch (Exception ex)
        {
            Note("read", "Wi-Fi list: Windows' list of networks could not be read.", ex);
        }

        return entries;
    }

    /// <summary>
    /// Asks Windows to connect. Without a key Windows uses the one it has saved for the network
    /// (WrongKey when it has none); with one, Windows saves it. A hidden network needs its name.
    /// </summary>
    internal async Task<WifiConnectResult> ConnectAsync(WifiEntry entry, bool automatic, string? user, string? key, string? hiddenSsid)
    {
        WiFiAdapter? adapter = _adapter;
        if (adapter is null)
        {
            return WifiConnectResult.Failed;
        }

        WiFiReconnectionKind kind = automatic ? WiFiReconnectionKind.Automatic : WiFiReconnectionKind.Manual;
        try
        {
            WiFiConnectionResult result;
            if (entry.Hidden && !string.IsNullOrEmpty(hiddenSsid))
            {
                result = await adapter.ConnectAsync(entry.Network, kind, MakeCredential(user, key), hiddenSsid);
            }
            else if (user is null && key is null)
            {
                result = await adapter.ConnectAsync(entry.Network, kind);
            }
            else
            {
                result = await adapter.ConnectAsync(entry.Network, kind, MakeCredential(user, key));
            }

            WiFiConnectionStatus status = result.ConnectionStatus;
            Log.Info("Wi-Fi list: connecting (" + (entry.Secured ? "secured" : "open") + (entry.Hidden ? ", hidden" : string.Empty)
                + (key is null ? ", saved key" : ", typed key") + (automatic ? ", automatic" : ", manual") + ") ended with " + status + ".");
            return status switch
            {
                WiFiConnectionStatus.Success => WifiConnectResult.Connected,
                WiFiConnectionStatus.InvalidCredential => WifiConnectResult.WrongKey,
                WiFiConnectionStatus.NetworkNotAvailable => WifiConnectResult.Gone,
                WiFiConnectionStatus.Timeout => WifiConnectResult.Timeout,
                WiFiConnectionStatus.UnsupportedAuthenticationProtocol => WifiConnectResult.Unsupported,
                WiFiConnectionStatus.AccessRevoked => WifiConnectResult.NoAccess,
                _ => WifiConnectResult.Failed,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi list: connecting failed.", ex);
            return WifiConnectResult.Failed;
        }
    }

    /// <summary>Disconnects from the current network. False when Windows could not.</summary>
    internal bool Disconnect()
    {
        WiFiAdapter? adapter = _adapter;
        if (adapter is null)
        {
            return false;
        }

        try
        {
            adapter.Disconnect();
            Log.Info("Wi-Fi list: disconnected.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi list: disconnecting failed.", ex);
            return false;
        }
    }

    /// <summary>The name, or empty for a hidden network (Windows gives none, or only zero bytes).</summary>
    private static string SsidOf(WiFiAvailableNetwork network)
    {
        string ssid = network.Ssid ?? string.Empty;
        return ssid.Trim('\0').Length == 0 ? string.Empty : ssid;
    }

    private static int Compare(WifiEntry a, WifiEntry b)
    {
        if (a.Connected != b.Connected)
        {
            return a.Connected ? -1 : 1;
        }

        if (a.Hidden != b.Hidden)
        {
            return a.Hidden ? 1 : -1;
        }

        if (a.Bars != b.Bars)
        {
            return b.Bars.CompareTo(a.Bars);
        }

        return string.Compare(a.Ssid, b.Ssid, StringComparison.CurrentCultureIgnoreCase);
    }

    private static PasswordCredential MakeCredential(string? user, string? key)
    {
        // Windows refuses an empty user name or password here (an exception), so only what was typed is set.
        var credential = new PasswordCredential();
        if (!string.IsNullOrEmpty(user))
        {
            credential.UserName = user;
        }

        if (!string.IsNullOrEmpty(key))
        {
            credential.Password = key;
        }

        return credential;
    }

    /// <summary>The network the adapter is connected to (its name), and whether it reaches the internet.</summary>
    private async Task<(string? Ssid, bool Internet)> ConnectedAsync(WiFiAdapter adapter)
    {
        try
        {
            ConnectionProfile? profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
            if (profile is null || !profile.IsWlanConnectionProfile)
            {
                return (null, false);
            }

            string? ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
            if (string.IsNullOrEmpty(ssid))
            {
                ssid = profile.ProfileName;
            }

            bool internet = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            return (string.IsNullOrEmpty(ssid) ? null : ssid, internet);
        }
        catch (Exception ex)
        {
            Note("connected", "Wi-Fi list: the connected network could not be read.", ex);
            return (null, false);
        }
    }

    private void Listen(bool on)
    {
        WiFiAdapter? adapter = _adapter;
        if (adapter is null || on == _listening)
        {
            return;
        }

        try
        {
            if (on)
            {
                adapter.AvailableNetworksChanged += OnNetworksChanged;
            }
            else
            {
                adapter.AvailableNetworksChanged -= OnNetworksChanged;
            }

            _listening = on;
        }
        catch (Exception ex)
        {
            Note("listen-" + on.ToString(CultureInfo.InvariantCulture), "Wi-Fi list: Windows' list changes could not be followed.", ex);
            _listening = false;
        }
    }

    private void OnNetworksChanged(WiFiAdapter sender, object args)
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi list: a change could not be passed on.", ex);
        }
    }

    /// <summary>Logs a failure once per key while Machitan runs.</summary>
    private void Note(string key, string message, Exception ex)
    {
        if (_noted.Add(key))
        {
            Log.Warn(message, ex);
        }
    }
}

/// <summary>
/// Windows' saved Wi-Fi networks (its profiles) on the first Wi-Fi adapter, through Native Wifi
/// (wlanapi.dll): list them, connect to one, disconnect. None of these needs location access
/// (they are not on Microsoft's list of calls that do, unlike looking for networks), so Win+A's
/// Wi-Fi list offers them when location access is off (Sam, 2026-11-26: "i hope there's a way to
/// bypass location requirement"; this is no way around it: nothing nearby is looked at, and a
/// saved network connects only when it is in reach). UI thread; each call opens and closes its
/// own handle. Never throws.
/// </summary>
internal static class WifiSaved
{
    private static readonly HashSet<string> Noted = new();

    /// <summary>The saved networks' names, in Windows' order (its preference); empty when there are none or Windows refused.</summary>
    internal static List<string> Read()
    {
        var names = new List<string>();
        Use("read", (handle, adapter) =>
        {
            uint error = Wlanapi.WlanGetProfileList(handle, ref adapter, 0, out nint list);
            if (error != Wlanapi.ErrorSuccess || list == 0)
            {
                NoteOnce("list", "Wi-Fi list: Windows' saved networks could not be read (error " + error.ToString(CultureInfo.InvariantCulture) + ").");
                return false;
            }

            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    string? name = Marshal.PtrToStringUni(list + Wlanapi.ProfileListHeader + (i * Wlanapi.ProfileInfoSize));
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }
            finally
            {
                Wlanapi.WlanFreeMemory(list);
            }

            return true;
        });
        return names;
    }

    /// <summary>Asks Windows to connect to a saved network. True when Windows took the request (it connects in the background).</summary>
    internal static bool Connect(string name)
    {
        return Use("connect", (handle, adapter) =>
        {
            nint profile = Marshal.StringToHGlobalUni(name);
            try
            {
                var parameters = new Wlanapi.ConnectionParameters
                {
                    ConnectionMode = Wlanapi.ConnectionModeProfile,
                    Profile = profile,
                    BssType = Wlanapi.BssTypeAny,
                };
                uint error = Wlanapi.WlanConnect(handle, ref adapter, ref parameters, 0);
                Log.Info("Wi-Fi list: asked Windows to connect to a saved network: " + (error == Wlanapi.ErrorSuccess ? "taken." : "refused (error " + error.ToString(CultureInfo.InvariantCulture) + ")."));
                return error == Wlanapi.ErrorSuccess;
            }
            finally
            {
                Marshal.FreeHGlobal(profile);
            }
        });
    }

    /// <summary>Disconnects the Wi-Fi adapter. True when Windows took it.</summary>
    internal static bool Disconnect()
    {
        return Use("disconnect", (handle, adapter) =>
        {
            uint error = Wlanapi.WlanDisconnect(handle, ref adapter, 0);
            Log.Info("Wi-Fi list: asked Windows to disconnect: " + (error == Wlanapi.ErrorSuccess ? "taken." : "refused (error " + error.ToString(CultureInfo.InvariantCulture) + ")."));
            return error == Wlanapi.ErrorSuccess;
        });
    }

    /// <summary>Opens a handle, finds the first Wi-Fi adapter, runs <paramref name="work"/>, closes the handle.</summary>
    private static bool Use(string what, Func<nint, Guid, bool> work)
    {
        nint handle = 0;
        try
        {
            uint error = Wlanapi.WlanOpenHandle(Wlanapi.ClientVersion, 0, out _, out handle);
            if (error != Wlanapi.ErrorSuccess)
            {
                NoteOnce("open", "Wi-Fi list: Native Wifi could not be opened (error " + error.ToString(CultureInfo.InvariantCulture) + ").");
                return false;
            }

            error = Wlanapi.WlanEnumInterfaces(handle, 0, out nint interfaces);
            if (error != Wlanapi.ErrorSuccess || interfaces == 0)
            {
                NoteOnce("interfaces", "Wi-Fi list: Windows' Wi-Fi adapters could not be read (error " + error.ToString(CultureInfo.InvariantCulture) + ").");
                return false;
            }

            Guid adapter;
            try
            {
                if (Marshal.ReadInt32(interfaces) == 0)
                {
                    return false;
                }

                adapter = Marshal.PtrToStructure<Guid>(interfaces + Wlanapi.InterfaceListHeader);
            }
            finally
            {
                Wlanapi.WlanFreeMemory(interfaces);
            }

            return work(handle, adapter);
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi list: Windows' saved networks (" + what + ") failed.", ex);
            return false;
        }
        finally
        {
            if (handle != 0)
            {
                Wlanapi.WlanCloseHandle(handle, 0);
            }
        }
    }

    private static void NoteOnce(string key, string message)
    {
        if (Noted.Add(key))
        {
            Log.Warn(message);
        }
    }
}
