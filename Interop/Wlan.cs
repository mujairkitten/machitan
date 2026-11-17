using System;
using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>
/// wlanapi.dll (Native Wifi): the Wi-Fi adapter's state and whether its radio is on.
/// Deliberately NOT used: the current-connection query (network name, signal). Since
/// Windows 11 24H2 that call needs location permission and shows up as location use in the
/// tray each time; the name and signal come from Windows' network list instead.
/// </summary>
internal static partial class Wlanapi
{
    internal const uint ClientVersion = 2;
    internal const uint ErrorSuccess = 0;

    /// <summary>WLAN_INTF_OPCODE wlan_intf_opcode_radio_state.</summary>
    internal const int OpcodeRadioState = 4;

    /// <summary>WLAN_INTERFACE_STATE values.</summary>
    internal const int StateNotReady = 0;
    internal const int StateConnected = 1;
    internal const int StateAdHocFormed = 2;
    internal const int StateDisconnecting = 3;
    internal const int StateDisconnected = 4;
    internal const int StateAssociating = 5;
    internal const int StateDiscovering = 6;
    internal const int StateAuthenticating = 7;

    /// <summary>DOT11_RADIO_STATE dot11_radio_state_off.</summary>
    internal const int RadioOff = 2;

    /// <summary>WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] (512) + state (4).</summary>
    internal const int InterfaceInfoSize = 532;
    internal const int InterfaceStateOffset = 528;

    /// <summary>WLAN_INTERFACE_INFO_LIST: two DWORDs, then the items.</summary>
    internal const int InterfaceListHeader = 8;

    /// <summary>WLAN_RADIO_STATE: a DWORD count, then 64 x (index, software state, hardware state).</summary>
    internal const int RadioPhyHeader = 4;
    internal const int RadioPhySize = 12;

    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint clientHandle);

    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanCloseHandle(nint clientHandle, nint reserved);

    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint interfaceList);

    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanQueryInterface(
        nint clientHandle,
        ref Guid interfaceGuid,
        int opCode,
        nint reserved,
        out uint dataSize,
        out nint data,
        out int opcodeValueType);

    [LibraryImport("wlanapi.dll")]
    internal static partial void WlanFreeMemory(nint memory);
}
