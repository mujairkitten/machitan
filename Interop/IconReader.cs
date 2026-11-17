using System;

namespace Machitan.Interop;

/// <summary>Icon pixels: 32-bit BGRA, premultiplied alpha, top row first.</summary>
internal sealed class IconPixels
{
    internal IconPixels(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal byte[] Bgra { get; }
}

/// <summary>
/// Reads real icons into pixels using GDI only.
/// SHGetFileInfo is deliberately not used: it can overflow the stack on .NET 9/10.
/// </summary>
internal static unsafe class IconReader
{
    private const int MaxIconSide = 512;

    /// <summary>
    /// An icon inside an executable, DLL or .ico file, at about the requested pixel size.
    /// A negative index means a resource ID, as in shortcut icon locations.
    /// </summary>
    internal static IconPixels? FromExecutable(string path, int pixelSize, int iconIndex = 0)
    {
        uint count = User32.PrivateExtractIconsW(path, iconIndex, pixelSize, pixelSize, out nint icon, out _, 1, 0);
        if (count == 0 || count == uint.MaxValue || icon == 0)
        {
            return null;
        }

        try
        {
            return Read(icon);
        }
        finally
        {
            // Extracted for us, so ours to destroy.
            User32.DestroyIcon(icon);
        }
    }

    /// <summary>The icon a window reports for itself, else its window class icon.</summary>
    internal static IconPixels? FromWindow(nint hwnd)
    {
        nint icon = AskWindow(hwnd, User32.ICON_BIG);
        if (icon == 0)
        {
            icon = (nint)User32.GetClassLongPtrW(hwnd, User32.GCLP_HICON);
        }

        if (icon == 0)
        {
            icon = AskWindow(hwnd, User32.ICON_SMALL2);
        }

        if (icon == 0)
        {
            icon = (nint)User32.GetClassLongPtrW(hwnd, User32.GCLP_HICONSM);
        }

        // These icons belong to the other app: read, never destroy.
        return icon == 0 ? null : Read(icon);
    }

    private static nint AskWindow(nint hwnd, nint which)
    {
        // A hung window must not hang the shell: give up after a short wait.
        nint sent = User32.SendMessageTimeoutW(hwnd, User32.WM_GETICON, which, 0, User32.SMTO_ABORTIFHUNG, 100, out nint icon);
        return sent == 0 ? 0 : icon;
    }

    private static IconPixels? Read(nint icon)
    {
        if (!User32.GetIconInfo(icon, out ICONINFO info))
        {
            return null;
        }

        try
        {
            if (info.hbmColor == 0)
            {
                // Black-and-white icon without a colour bitmap: not worth supporting.
                return null;
            }

            BITMAP description;
            if (Gdi32.GetObjectW(info.hbmColor, sizeof(BITMAP), &description) == 0)
            {
                return null;
            }

            int width = description.bmWidth;
            int height = description.bmHeight;
            if (width <= 0 || height <= 0 || width > MaxIconSide || height > MaxIconSide)
            {
                return null;
            }

            byte[]? pixels = ReadBits(info.hbmColor, width, height);
            if (pixels is null)
            {
                return null;
            }

            bool hasAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (!hasAlpha)
            {
                // Old-style icon: transparency comes from the separate mask (white = see-through).
                byte[]? mask = info.hbmMask != 0 ? ReadBits(info.hbmMask, width, height) : null;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    bool seeThrough = mask is not null && mask[i] != 0;
                    pixels[i + 3] = seeThrough ? (byte)0 : (byte)255;
                }
            }

            // XAML bitmaps want premultiplied alpha.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                if (alpha == 255)
                {
                    continue;
                }

                pixels[i] = (byte)(pixels[i] * alpha / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
            }

            return new IconPixels(width, height, pixels);
        }
        finally
        {
            if (info.hbmColor != 0)
            {
                Gdi32.DeleteObject(info.hbmColor);
            }

            if (info.hbmMask != 0)
            {
                Gdi32.DeleteObject(info.hbmMask);
            }
        }
    }

    /// <summary>Copies a bitmap out as 32-bit BGRA, top row first.</summary>
    private static byte[]? ReadBits(nint bitmap, int width, int height)
    {
        BITMAPINFO info = default;
        info.Header.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.Header.biWidth = width;
        info.Header.biHeight = -height;
        info.Header.biPlanes = 1;
        info.Header.biBitCount = 32;
        info.Header.biCompression = 0;

        byte[] bits = new byte[width * height * 4];

        nint screen = User32.GetDC(0);
        if (screen == 0)
        {
            return null;
        }

        try
        {
            fixed (byte* target = bits)
            {
                int lines = Gdi32.GetDIBits(screen, bitmap, 0, (uint)height, target, &info, 0);
                return lines == height ? bits : null;
            }
        }
        finally
        {
            User32.ReleaseDC(0, screen);
        }
    }
}
