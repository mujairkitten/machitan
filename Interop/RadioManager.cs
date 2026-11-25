using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>
/// Windows' radio management object, RadioManagementAPI: the master switch for all of the PC's
/// radios, which Windows shows as airplane mode. Not in the Windows SDK; the interface below is
/// as it has been worked out from Windows itself (the method order matters). Machitan only
/// reads and sets the system radio state with it.
/// </summary>
[ComImport]
[Guid("581333F6-28DB-41BE-BC7A-FF201F12F3F6")]
internal class RadioManagementApi
{
}

/// <summary>
/// IRadioManager. GetSystemRadioState's first value is TRUE while the radios are allowed on
/// (airplane mode off) and FALSE in airplane mode; SetSystemRadioState takes the same.
/// GetUIRadioInstances hands back an interface pointer; Machitan never calls it.
/// </summary>
[ComImport]
[Guid("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRadioManager
{
    [PreserveSig]
    int IsRMSupported(out uint state);

    [PreserveSig]
    int GetUIRadioInstances(out nint instances);

    [PreserveSig]
    int GetSystemRadioState(out int radiosEnabled, out int switchState, out int changeReason);

    [PreserveSig]
    int SetSystemRadioState(int radiosEnabled);

    [PreserveSig]
    int Refresh();

    [PreserveSig]
    int OnHardwareSliderChange(int switchState, int changeReason);
}
