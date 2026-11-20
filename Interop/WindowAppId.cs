using System;
using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>
/// What a window tells Windows' taskbar about itself through its property store: its own app
/// ID (System.AppUserModel.ID), and for that ID the name and icon its button should show
/// (RelaunchDisplayNameResource, RelaunchIconResource). Windows' taskbar groups windows by this
/// app ID: Firefox's private windows, for one, carry their own and get their own button.
/// </summary>
internal sealed record WindowAppInfo(string? AppId, string? DisplayName, string? IconResource);

internal static unsafe partial class WindowAppId
{
    private const int GetValueSlot = 5;

    private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AppUserModelSet = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    /// <summary>Reads the window's values; all null when it set none (most windows).</summary>
    internal static WindowAppInfo Read(nint hwnd)
    {
        Guid iid = IidPropertyStore;
        nint store = 0;
        if (SHGetPropertyStoreForWindow(hwnd, &iid, &store) < 0 || store == 0)
        {
            return new WindowAppInfo(null, null, null);
        }

        try
        {
            string? appId = Text(store, 5);
            if (appId is null)
            {
                return new WindowAppInfo(null, null, null);
            }

            string? name = Text(store, 4);
            if (name is not null && name.StartsWith('@'))
            {
                name = Indirect(name);
            }

            return new WindowAppInfo(appId, name, Text(store, 3));
        }
        finally
        {
            ComCall.Release(store);
        }
    }

    /// <summary>"path,index" (index may be negative: a resource id) as a file and an index; false when it is not that.</summary>
    internal static bool TrySplitIcon(string resource, out string file, out int index)
    {
        file = string.Empty;
        index = 0;
        int comma = resource.LastIndexOf(',');
        string path = comma > 0 ? resource[..comma] : resource;
        if (comma > 0 && !int.TryParse(resource[(comma + 1)..].Trim(), out index))
        {
            return false;
        }

        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.Length == 0 || !System.IO.File.Exists(path))
        {
            return false;
        }

        file = path;
        return true;
    }

    private static string? Text(nint store, uint pid)
    {
        var key = new PROPERTYKEY { fmtid = AppUserModelSet, pid = pid };
        PROPVARIANT value = default;
        int hr = ((delegate* unmanaged<nint, PROPERTYKEY*, PROPVARIANT*, int>)ComCall.Slot(store, GetValueSlot))(store, &key, &value);
        if (hr < 0)
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

    /// <summary>An "@file,-id" name as text, or null.</summary>
    private static string? Indirect(string source)
    {
        char* buffer = stackalloc char[260];
        if (SHLoadIndirectString(source, buffer, 260, 0) < 0)
        {
            return null;
        }

        string text = new(buffer);
        return text.Length == 0 ? null : text;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint hwnd, Guid* riid, nint* store);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string source, char* buffer, uint bufferSize, nint reserved);
}
