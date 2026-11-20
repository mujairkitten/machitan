using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Machitan.Interop;

/// <summary>The Windows shortcut (.lnk) object.</summary>
[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkObject
{
}

/// <summary>
/// shobjidl.h IShellLinkW, methods in vtable order.
/// GetPath's third parameter is a WIN32_FIND_DATAW pointer: always pass IntPtr.Zero. A real
/// pointer lets Windows write about 592 bytes through it and corrupt the stack.
/// </summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);

    void GetIDList(out IntPtr idList);

    void SetIDList(IntPtr idList);

    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int capacity);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

    void GetHotkey(out short hotkey);

    void SetHotkey(short hotkey);

    void GetShowCmd(out int showCommand);

    void SetShowCmd(int showCommand);

    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int capacity, out int iconIndex);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

    void Resolve(IntPtr hwnd, uint flags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

/// <summary>objidl.h IPersistFile (with IPersist's GetClassID first).</summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal interface IPersistFile
{
    void GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

    void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}

/// <summary>objidl.h IPersistStream (with IPersist's GetClassID first): a shortcut read from bytes.</summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000109-0000-0000-C000-000000000046")]
internal interface IPersistStream
{
    [PreserveSig]
    int GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    /// <summary>Reads from an IStream (a raw pointer), leaving it just after the object's data.</summary>
    [PreserveSig]
    int Load(nint stream);
}

/// <summary>
/// What a shortcut points at, where its icon is, and the app ID it was given
/// (System.AppUserModel.ID, set by apps that name themselves, e.g. Electron apps).
/// </summary>
internal sealed record ShortcutInfo(string? TargetPath, string? IconPath, int IconIndex, string? AppUserModelId = null);

/// <summary>Reading text and yes/no values from a COM property store.</summary>
internal static class PropertyReader
{
    private const ushort VtBool = 11;

    /// <summary>PKEY_AppUserModel_ID.</summary>
    internal static PROPERTYKEY AppUserModelId => new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    /// <summary>PKEY_AppUserModel_IsDestListSeparator (a jump list separator line).</summary>
    internal static PROPERTYKEY IsSeparator => new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 6 };

    /// <summary>PKEY_Title.</summary>
    internal static PROPERTYKEY Title => new() { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };

    internal static string? Text(object item, PROPERTYKEY key)
    {
        if (item is not IPropertyStore store || store.GetValue(ref key, out PROPVARIANT value) < 0)
        {
            return null;
        }

        try
        {
            string? text = value.vt == CoreAudio.VtLpwstr && value.pointerValue != 0
                ? Marshal.PtrToStringUni(value.pointerValue)
                : null;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    internal static bool Flag(object item, PROPERTYKEY key)
    {
        if (item is not IPropertyStore store || store.GetValue(ref key, out PROPVARIANT value) < 0)
        {
            return false;
        }

        try
        {
            // VARIANT_BOOL: the first two bytes of the value, -1 for true.
            return value.vt == VtBool && (short)(value.pointerValue & 0xFFFF) != 0;
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }
}

/// <summary>Reads .lnk files. Safe on worker threads (the shortcut object supports both apartments).</summary>
internal static class ShortcutReader
{
    private const uint StgmRead = 0;
    private const int Capacity = 1024;

    /// <summary>Null when the shortcut cannot be read.</summary>
    internal static ShortcutInfo? Read(string shortcutPath)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((IPersistFile)link).Load(shortcutPath, StgmRead);
            var shellLink = (IShellLinkW)link;

            var target = new StringBuilder(Capacity);
            shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);

            var icon = new StringBuilder(Capacity);
            shellLink.GetIconLocation(icon, icon.Capacity, out int iconIndex);

            string? appId = null;
            try
            {
                appId = PropertyReader.Text(link, PropertyReader.AppUserModelId);
            }
            catch (Exception)
            {
                // A shortcut without a property store has no app ID.
            }

            return new ShortcutInfo(Clean(target.ToString()), Clean(icon.ToString()), iconIndex, appId);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }

    internal static string? Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return expanded.Length > 0 && Path.IsPathRooted(expanded) ? expanded : null;
    }
}
