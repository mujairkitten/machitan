using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
using Windows.Storage.Streams;

namespace Machitan.Shell;

/// <summary>
/// Real app icons for the bar, as ready-to-show bitmaps. UI thread only.
///
/// Packaged apps (store apps): the logo from the app's package, as Windows' own taskbar shows it:
/// the "target size" logo file closest to the size needed, unplated, in the variant for the
/// bar's theme. Those fill the icon square. Packages without such files fall back to the
/// general logo Windows hands out, which has a wide built-in margin. Logos load in the
/// background; <see cref="Changed"/> says when one arrived.
/// Everything else, and packaged apps while their logo loads: the executable icon at the size
/// the bar needs (sharpest), then the icon the window reports, then its window class icon.
///
/// Successes are cached for the run. Failures are never kept: a miss is tried again, at most
/// once every few seconds.
/// </summary>
internal sealed class IconService
{
    private const long RetryAfterMs = 5000;

    // Sizes icons are commonly stored at. Asking for one of these avoids a blurry resize.
    private static readonly int[] CommonSizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _logos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastMiss = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingLogos = new(StringComparer.OrdinalIgnoreCase);

    private PackageManager? _packageManager;

    /// <summary>The bar's theme: picks the dark-theme or light-theme variant of store-app logos.</summary>
    internal bool DarkBar { get; set; } = true;

    /// <summary>Raised on the UI thread when a logo that was loading in the background arrived.</summary>
    internal event Action? Changed;

    /// <summary>
    /// The icon for a dock item, or null when Windows has none to give right now.
    /// <paramref name="neededPixels"/> is the size on screen in physical pixels.
    /// </summary>
    internal ImageSource? GetIcon(DockItem item, int neededPixels)
    {
        int size = PickSize(neededPixels);
        string sizeText = "|" + size.ToString(CultureInfo.InvariantCulture);

        // A running packaged app is known by its package; a pinned one that is not running,
        // only by its app ID (the package's version can change between runs).
        string? package = item.PackageFullName;
        string? family = package is null ? PackageLogo.FamilyFromAppId(item.AppId) : null;
        if (package is not null || family is not null)
        {
            string logoKey = (package ?? "family:" + family) + sizeText + (DarkBar ? "|d" : "|l");
            if (_logos.TryGetValue(logoKey, out ImageSource? logo))
            {
                return logo;
            }

            StartLogoLoad(package, family, item.AppId, size, logoKey);
        }

        string iconKey = item.Key + sizeText;
        if (_icons.TryGetValue(iconKey, out ImageSource? cached))
        {
            return cached;
        }

        long now = Environment.TickCount64;
        if (_lastMiss.TryGetValue(iconKey, out long missedAt) && now - missedAt < RetryAfterMs)
        {
            return null;
        }

        try
        {
            IconPixels? pixels = null;

            if (item.Path is string path)
            {
                pixels = IconReader.FromExecutable(path, size);
            }

            if (pixels is null && item.Group is TaskGroup group)
            {
                foreach (TaskWindow window in group.Windows)
                {
                    pixels = IconReader.FromWindow(window.Handle);
                    if (pixels is not null)
                    {
                        break;
                    }
                }
            }

            if (pixels is null)
            {
                _lastMiss[iconKey] = now;
                return null;
            }

            WriteableBitmap bitmap = ToBitmap(pixels);
            _icons[iconKey] = bitmap;
            _lastMiss.Remove(iconKey);
            return bitmap;
        }
        catch (Exception ex)
        {
            _lastMiss[iconKey] = now;
            Log.Warn("An app icon could not be read.", ex);
            return null;
        }
    }

    private void StartLogoLoad(string? packageFullName, string? family, string? appId, int size, string logoKey)
    {
        if (_loadingLogos.Contains(logoKey))
        {
            return;
        }

        if (_lastMiss.TryGetValue(logoKey, out long missedAt) && Environment.TickCount64 - missedAt < RetryAfterMs)
        {
            return;
        }

        _loadingLogos.Add(logoKey);
        _ = LoadLogoAsync(packageFullName, family, appId, size, DarkBar, logoKey);
    }

    /// <summary>Runs on the UI thread between awaits (the shell sets a UI synchronization context).</summary>
    private async Task LoadLogoAsync(string? packageFullName, string? family, string? knownAppId, int size, bool dark, string logoKey)
    {
        try
        {
            _packageManager ??= new PackageManager();
            PackageManager manager = _packageManager;

            // An empty user id means the current user, which needs no special rights.
            Package? package = await Task.Run(() => packageFullName is not null
                ? manager.FindPackageForUser(string.Empty, packageFullName)
                : manager.FindPackagesForUser(string.Empty, family!).FirstOrDefault());
            if (package is null)
            {
                _lastMiss[logoKey] = Environment.TickCount64;
                Log.Warn("Package logo: package not found (" + (packageFullName ?? family) + ").");
                return;
            }

            IReadOnlyList<AppListEntry> entries = await package.GetAppListEntriesAsync();
            if (entries.Count == 0)
            {
                _lastMiss[logoKey] = Environment.TickCount64;
                Log.Warn("Package logo: the package lists no apps (" + packageFullName + ").");
                return;
            }

            var bitmap = new BitmapImage
            {
                DecodePixelWidth = size,
                DecodePixelType = DecodePixelType.Physical,
            };

            string appId = knownAppId ?? entries[0].AppUserModelId;
            string? file = await Task.Run(() => PackageLogo.FindTargetSizeLogo(package, appId, size, dark));
            if (file is not null)
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                using var memory = new InMemoryRandomAccessStream();
                await memory.WriteAsync(bytes.AsBuffer());
                memory.Seek(0);
                await bitmap.SetSourceAsync(memory);
            }
            else
            {
                RandomAccessStreamReference reference = entries[0].DisplayInfo.GetLogo(new Windows.Foundation.Size(size, size));
                using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
                await bitmap.SetSourceAsync(stream);
            }

            _logos[logoKey] = bitmap;
            _lastMiss.Remove(logoKey);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _lastMiss[logoKey] = Environment.TickCount64;
            Log.Warn("Package logo could not be loaded (" + (packageFullName ?? family) + ").", ex);
        }
        finally
        {
            _loadingLogos.Remove(logoKey);
        }
    }

    internal static int PickSize(int neededPixels)
    {
        foreach (int size in CommonSizes)
        {
            if (size >= neededPixels)
            {
                return size;
            }
        }

        return CommonSizes[^1];
    }

    private static WriteableBitmap ToBitmap(IconPixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels.Bgra, 0, pixels.Bgra.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
