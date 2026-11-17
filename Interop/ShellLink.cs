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

/// <summary>What a shortcut points at and where its icon is.</summary>
internal sealed record ShortcutInfo(string? TargetPath, string? IconPath, int IconIndex);

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

            return new ShortcutInfo(Clean(target.ToString()), Clean(icon.ToString()), iconIndex);
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

    private static string? Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return expanded.Length > 0 && Path.IsPathRooted(expanded) ? expanded : null;
    }
}
