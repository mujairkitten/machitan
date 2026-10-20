using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Machitan.Native;

/// <summary>
/// Loads the shell icon for an executable or shortcut and converts it to a
/// WinUI <see cref="ImageSource"/> (BitmapImage cannot read icons from .exe files).
/// </summary>
internal static class IconLoader
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (_cache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(path, 0, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                _cache[path] = null;
                return null;
            }

            using var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);

            using var bitmap = icon.ToBitmap();
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);

            var image = new BitmapImage();
            _ = SetSourceAsync(image, memory.ToArray());
            _cache[path] = image;
            return image;
        }
        catch
        {
            _cache[path] = null;
            return null;
        }
    }

    private static async System.Threading.Tasks.Task SetSourceAsync(BitmapImage image, byte[] png)
    {
        try
        {
            using var memory = new MemoryStream(png);
            using var stream = memory.AsRandomAccessStream();
            await image.SetSourceAsync(stream);
        }
        catch
        {
            // Icon simply stays empty.
        }
    }
}
