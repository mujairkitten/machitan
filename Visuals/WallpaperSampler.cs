using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Machitan.Visuals;

/// <summary>How something drawn straight on the wallpaper should look there.</summary>
/// <param name="DarkInk">True when dark ink reads better than white on this part of the wallpaper.</param>
/// <param name="NeedsBacking">True when the area is too mixed for either ink alone (a backing helps).</param>
/// <param name="Luminance">The area's average relative luminance, 0 (black) to 1 (white).</param>
/// <param name="HardShare">The share of the area where the chosen ink has a contrast under 3:1.</param>
internal readonly record struct WallpaperJudgement(bool DarkInk, bool NeedsBacking, double Luminance, double HardShare);

/// <summary>A flat colour laid over the wallpaper (the top strip cover): its colour and how solid it is (0 to 1).</summary>
internal readonly record struct CoverTint(byte R, byte G, byte B, double Alpha);

/// <summary>Windows' wallpaper settings for one screen.</summary>
/// <param name="Monitor">The screen, physical pixels.</param>
/// <param name="Path">The picture's file, or empty for a solid colour.</param>
/// <param name="Position">How the picture is fitted (WallpaperPosition).</param>
/// <param name="BackColour">The background colour (COLORREF, 0x00BBGGRR) around a fitted or centred picture.</param>
/// <param name="Stamp">Write times and sizes of the picture file and of the file Windows shows, to notice a changed file.</param>
/// <param name="Source">Where the picture's path came from, for the log.</param>
internal sealed record WallpaperSettings(RECT Monitor, string Path, int Position, uint BackColour, string Stamp, string Source)
{
    internal string Signature => string.Join(
        "|",
        Monitor.Left.ToString(CultureInfo.InvariantCulture),
        Monitor.Top.ToString(CultureInfo.InvariantCulture),
        Monitor.Right.ToString(CultureInfo.InvariantCulture),
        Monitor.Bottom.ToString(CultureInfo.InvariantCulture),
        Path,
        Position.ToString(CultureInfo.InvariantCulture),
        BackColour.ToString(CultureInfo.InvariantCulture),
        Stamp);
}

/// <summary>
/// A small copy of the wallpaper of one screen, placed the way Windows shows it (fill, fit,
/// stretch, centre, tile; span is treated as fill), to judge what ink reads well on a part of
/// it. Settings from Windows' wallpaper record and the registry (see ReadSettings); the
/// picture decoded natively (Windows.Graphics.Imaging), at most 640 pixels on the long side.
/// Use on the UI thread.
/// </summary>
internal sealed class WallpaperSampler
{
    private const int MaxDecodeSide = 640;
    private const int Columns = 48;
    private const int Rows = 8;
    private const double HardContrast = 3.0;
    private const double BackingShare = 0.15;

    private static readonly double DarkInkLuminance = Luminance(0x1C, 0x1C, 0x1C);
    private static readonly HashSet<string> Noted = new();

    private readonly WallpaperSettings _settings;
    private readonly byte[]? _pixels;
    private readonly int _decodedWidth;
    private readonly int _decodedHeight;
    private readonly int _imageWidth;
    private readonly int _imageHeight;

    private WallpaperSampler(WallpaperSettings settings, byte[]? pixels, int decodedWidth, int decodedHeight, int imageWidth, int imageHeight)
    {
        _settings = settings;
        _pixels = pixels;
        _decodedWidth = decodedWidth;
        _decodedHeight = decodedHeight;
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
    }

    internal string Signature => _settings.Signature;

    /// <summary>Where the picture's path came from, for the log.</summary>
    internal string Source => _settings.Source;

    /// <summary>"a picture (fill)" or "a solid colour", for the log.</summary>
    internal string Kind => _pixels is null
        ? "a solid colour"
        : "a picture (" + (_settings.Position switch
        {
            WallpaperPosition.Center => "centre",
            WallpaperPosition.Tile => "tile",
            WallpaperPosition.Stretch => "stretch",
            WallpaperPosition.Fit => "fit",
            WallpaperPosition.Span => "span, judged as fill",
            _ => "fill",
        }) + ", " + System.IO.Path.GetFileName(_settings.Path) + ")";

    /// <summary>The file Windows makes from the chosen picture and actually shows.</summary>
    internal static string ShownFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Themes",
        "TranscodedWallpaper");

    /// <summary>
    /// Windows' wallpaper settings for the screen under the middle of <paramref name="near"/>;
    /// null when Windows does not say. The screens come from Windows' wallpaper record
    /// (IDesktopWallpaper). When every screen shows the same picture (always so with one
    /// screen), the picture, its fit and the background colour are read from the registry
    /// (HKCU\Control Panel\Desktop and \Colors), which Windows updates the moment the wallpaper
    /// changes; the wallpaper record is used for different pictures per screen and as a fallback.
    /// </summary>
    internal static WallpaperSettings? ReadSettings(RECT near)
    {
        IDesktopWallpaper? wallpaper = null;
        try
        {
            wallpaper = (IDesktopWallpaper)new DesktopWallpaperObject();
            if (wallpaper.GetMonitorDevicePathCount(out uint count) != 0 || count == 0)
            {
                return null;
            }

            int x = (near.Left + near.Right) / 2;
            int y = (near.Top + near.Bottom) / 2;
            string? chosen = null;
            RECT chosenRect = default;
            string chosenPath = string.Empty;
            bool samePicture = true;
            string? firstPath = null;
            for (uint i = 0; i < count; i++)
            {
                if (wallpaper.GetMonitorDevicePathAt(i, out nint idPointer) != 0 || idPointer == 0)
                {
                    continue;
                }

                string id = TakeString(idPointer);
                if (wallpaper.GetMonitorRECT(id, out RECT rect) != 0)
                {
                    continue;
                }

                string path = wallpaper.GetWallpaper(id, out nint pathPointer) == 0 && pathPointer != 0
                    ? TakeString(pathPointer)
                    : string.Empty;
                firstPath ??= path;
                if (!string.Equals(path, firstPath, StringComparison.OrdinalIgnoreCase))
                {
                    samePicture = false;
                }

                bool contains = x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
                if (chosen is null || (contains && !RectContains(chosenRect, x, y)))
                {
                    chosen = id;
                    chosenRect = rect;
                    chosenPath = path;
                }
            }

            if (chosen is null || chosenRect.Width <= 0 || chosenRect.Height <= 0)
            {
                return null;
            }

            int position = wallpaper.GetPosition(out int fitted) == 0 ? fitted : WallpaperPosition.Fill;
            uint back = wallpaper.GetBackgroundColor(out uint colour) == 0 ? colour : 0;
            string source = "Windows' wallpaper record";

            if (samePicture)
            {
                string desktopKey = @"Control Panel\Desktop";
                string registryPath = ReadRegistry(desktopKey, "WallPaper");
                if (registryPath.Length > 0 && File.Exists(registryPath))
                {
                    chosenPath = registryPath;
                    source = "the registry";
                }

                if (TryReadRegistryPosition(ReadRegistry(desktopKey, "WallpaperStyle"), ReadRegistry(desktopKey, "TileWallpaper"), out int registryPosition))
                {
                    position = registryPosition;
                }

                if (TryReadRegistryColour(ReadRegistry(@"Control Panel\Colors", "Background"), out uint registryBack))
                {
                    back = registryBack;
                }
            }

            if (chosenPath.Length > 0 && !File.Exists(chosenPath))
            {
                // Windows then shows the background colour.
                chosenPath = string.Empty;
            }

            string stamp = FileStamp(chosenPath) + ";" + FileStamp(ShownFile);
            return new WallpaperSettings(chosenRect, chosenPath, position, back, stamp, source);
        }
        catch (Exception ex)
        {
            Note("settings", "Wallpaper: Windows' wallpaper settings could not be read; the status ring keeps the theme's colours.", ex);
            return null;
        }
        finally
        {
            if (wallpaper is not null)
            {
                Marshal.ReleaseComObject(wallpaper);
            }
        }
    }

    /// <summary>Reads the settings and decodes the picture. Null when the wallpaper cannot be read.</summary>
    internal static async Task<WallpaperSampler?> LoadAsync(RECT near)
    {
        WallpaperSettings? settings = ReadSettings(near);
        if (settings is null)
        {
            return null;
        }

        if (settings.Path.Length == 0)
        {
            return new WallpaperSampler(settings, null, 0, 0, 0, 0);
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(settings.Path);
            using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;
            if (width == 0 || height == 0)
            {
                return null;
            }

            double shrink = Math.Min(1.0, MaxDecodeSide / (double)Math.Max(width, height));
            uint decodedWidth = Math.Max(1u, (uint)Math.Round(width * shrink));
            uint decodedHeight = Math.Max(1u, (uint)Math.Round(height * shrink));
            var transform = new BitmapTransform
            {
                ScaledWidth = decodedWidth,
                ScaledHeight = decodedHeight,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            PixelDataProvider data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = data.DetachPixelData();
            if (pixels.Length < (long)decodedWidth * decodedHeight * 4)
            {
                return null;
            }

            return new WallpaperSampler(settings, pixels, (int)decodedWidth, (int)decodedHeight, (int)width, (int)height);
        }
        catch (Exception ex)
        {
            Note("decode:" + settings.Path, "Wallpaper: the picture " + System.IO.Path.GetFileName(settings.Path)
                + " could not be read (" + ex.GetType().Name + "); the status ring keeps the theme's colours.", ex);
            return null;
        }
    }

    /// <summary>True when the middle of <paramref name="region"/> is on this wallpaper's screen.</summary>
    internal bool Covers(RECT region)
    {
        RECT monitor = _settings.Monitor;
        int x = (region.Left + region.Right) / 2;
        int y = (region.Top + region.Bottom) / 2;
        return x >= monitor.Left && x < monitor.Right && y >= monitor.Top && y < monitor.Bottom;
    }

    /// <summary>Judges a screen area (physical pixels) on a 48 x 8 grid of samples.</summary>
    /// <summary>
    /// What reads best on the wallpaper in <paramref name="region"/>: dark or white ink, and
    /// whether the ink needs a soft backing. With <paramref name="cover"/>, each sampled colour
    /// is first blended with the cover's colour, as the screen shows it.
    /// </summary>
    internal WallpaperJudgement Judge(RECT region, CoverTint? cover = null)
    {
        RECT monitor = _settings.Monitor;
        Placement(monitor.Width, monitor.Height, out double scaleX, out double scaleY, out double offsetX, out double offsetY, out bool tile);

        var samples = new double[Columns * Rows];
        double sum = 0;
        int n = 0;
        for (int row = 0; row < Rows; row++)
        {
            double y = region.Top + ((row + 0.5) * region.Height / Rows) - monitor.Top;
            for (int column = 0; column < Columns; column++)
            {
                double x = region.Left + ((column + 0.5) * region.Width / Columns) - monitor.Left;
                ColourAt(x, y, scaleX, scaleY, offsetX, offsetY, tile, out byte r, out byte g, out byte b);
                if (cover is CoverTint tint)
                {
                    r = Blend(r, tint.R, tint.Alpha);
                    g = Blend(g, tint.G, tint.Alpha);
                    b = Blend(b, tint.B, tint.Alpha);
                }

                double luminance = Luminance(r, g, b);
                samples[n++] = luminance;
                sum += luminance;
            }
        }

        double mean = sum / n;
        double whiteContrast = 1.05 / (mean + 0.05);
        double darkContrast = (mean + 0.05) / (DarkInkLuminance + 0.05);
        bool darkInk = darkContrast > whiteContrast;

        int hard = 0;
        for (int i = 0; i < n; i++)
        {
            double contrast = darkInk
                ? (samples[i] + 0.05) / (DarkInkLuminance + 0.05)
                : 1.05 / (samples[i] + 0.05);
            if (contrast < HardContrast)
            {
                hard++;
            }
        }

        double share = hard / (double)n;
        return new WallpaperJudgement(darkInk, share > BackingShare, mean, share);
    }

    /// <summary>
    /// The same judgement for a flat colour (a cover over a wallpaper that could not be read):
    /// one colour is never busy, so no backing.
    /// </summary>
    internal static WallpaperJudgement JudgeFlat(byte r, byte g, byte b)
    {
        double luminance = Luminance(r, g, b);
        double whiteContrast = 1.05 / (luminance + 0.05);
        double darkContrast = (luminance + 0.05) / (DarkInkLuminance + 0.05);
        return new WallpaperJudgement(darkContrast > whiteContrast, false, luminance, 0);
    }

    /// <summary>The cover over one colour channel, as the screen mixes them.</summary>
    private static byte Blend(byte under, byte over, double alpha)
    {
        return (byte)Math.Clamp(Math.Round((under * (1 - alpha)) + (over * alpha)), 0, 255);
    }

    private static bool RectContains(RECT rect, int x, int y)
    {
        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    private static string ReadRegistry(string keyPath, string name)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(name) as string ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The registry's WallpaperStyle / TileWallpaper pair, as Windows' Settings writes it.</summary>
    private static bool TryReadRegistryPosition(string style, string tile, out int position)
    {
        position = WallpaperPosition.Fill;
        if (tile.Trim() == "1")
        {
            position = WallpaperPosition.Tile;
            return true;
        }

        switch (style.Trim())
        {
            case "0":
                position = WallpaperPosition.Center;
                return true;
            case "2":
                position = WallpaperPosition.Stretch;
                return true;
            case "6":
                position = WallpaperPosition.Fit;
                return true;
            case "10":
                position = WallpaperPosition.Fill;
                return true;
            case "22":
                position = WallpaperPosition.Span;
                return true;
            default:
                return false;
        }
    }

    /// <summary>"R G B" (HKCU\Control Panel\Colors\Background) as a COLORREF.</summary>
    private static bool TryReadRegistryColour(string text, out uint colour)
    {
        colour = 0;
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        colour = r | ((uint)g << 8) | ((uint)b << 16);
        return true;
    }

    /// <summary>Write time and size of a file ("-" when there is none).</summary>
    private static string FileStamp(string path)
    {
        try
        {
            if (path.Length == 0)
            {
                return "-";
            }

            var info = new FileInfo(path);
            return info.Exists
                ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + info.Length.ToString(CultureInfo.InvariantCulture)
                : "-";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>WCAG relative luminance of an sRGB colour.</summary>
    internal static double Luminance(byte r, byte g, byte b)
    {
        return (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));
    }

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static string TakeString(nint pointer)
    {
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static void Note(string key, string message, Exception ex)
    {
        if (Noted.Add(key))
        {
            Log.Warn(message, ex);
        }
    }

    /// <summary>Where the picture sits on the screen: monitor pixel = offset + image pixel x scale.</summary>
    private void Placement(int screenWidth, int screenHeight, out double scaleX, out double scaleY, out double offsetX, out double offsetY, out bool tile)
    {
        tile = false;
        scaleX = 1;
        scaleY = 1;
        offsetX = 0;
        offsetY = 0;
        if (_pixels is null)
        {
            return;
        }

        double fitX = screenWidth / (double)_imageWidth;
        double fitY = screenHeight / (double)_imageHeight;
        switch (_settings.Position)
        {
            case WallpaperPosition.Stretch:
                scaleX = fitX;
                scaleY = fitY;
                return;
            case WallpaperPosition.Tile:
                tile = true;
                return;
            case WallpaperPosition.Center:
                offsetX = (screenWidth - _imageWidth) / 2.0;
                offsetY = (screenHeight - _imageHeight) / 2.0;
                return;
            case WallpaperPosition.Fit:
                scaleX = scaleY = Math.Min(fitX, fitY);
                break;
            default:
                scaleX = scaleY = Math.Max(fitX, fitY);
                break;
        }

        offsetX = (screenWidth - (_imageWidth * scaleX)) / 2.0;
        offsetY = (screenHeight - (_imageHeight * scaleY)) / 2.0;
    }

    private void ColourAt(double x, double y, double scaleX, double scaleY, double offsetX, double offsetY, bool tile, out byte r, out byte g, out byte b)
    {
        if (_pixels is not null)
        {
            double imageX = (x - offsetX) / scaleX;
            double imageY = (y - offsetY) / scaleY;
            if (tile)
            {
                imageX = ((imageX % _imageWidth) + _imageWidth) % _imageWidth;
                imageY = ((imageY % _imageHeight) + _imageHeight) % _imageHeight;
            }

            if (imageX >= 0 && imageY >= 0 && imageX < _imageWidth && imageY < _imageHeight)
            {
                int px = Math.Clamp((int)(imageX * _decodedWidth / _imageWidth), 0, _decodedWidth - 1);
                int py = Math.Clamp((int)(imageY * _decodedHeight / _imageHeight), 0, _decodedHeight - 1);
                int index = ((py * _decodedWidth) + px) * 4;
                b = _pixels[index];
                g = _pixels[index + 1];
                r = _pixels[index + 2];
                return;
            }
        }

        // Outside the picture (or no picture): the background colour, COLORREF 0x00BBGGRR.
        uint back = _settings.BackColour;
        r = (byte)(back & 0xFF);
        g = (byte)((back >> 8) & 0xFF);
        b = (byte)((back >> 16) & 0xFF);
    }
}
