using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
using Windows.Storage.Streams;

namespace Machitan.Shell;

public enum AppKind
{
    /// <summary>A .lnk in a Start menu Programs folder.</summary>
    Shortcut,

    /// <summary>A store (packaged) app.</summary>
    Packaged,
}

/// <summary>One app in the Start menu's All apps list. All of it comes from Windows.</summary>
public sealed class AppEntry
{
    public AppEntry(string key, string name, AppKind kind)
    {
        Key = key;
        Name = name;
        Kind = kind;
    }

    /// <summary>Shortcut path, or app ID for store apps.</summary>
    public string Key { get; }

    public string Name { get; }

    public AppKind Kind { get; }

    public string? ShortcutPath { get; init; }

    /// <summary>What the shortcut points at, when it is a file.</summary>
    public string? TargetPath { get; init; }

    public string? AppUserModelId { get; init; }

    /// <summary>Icon pixels (shortcuts). Null when Windows had none.</summary>
    internal IconPixels? Icon { get; init; }

    /// <summary>Encoded logo image (store apps). Null when the package had none.</summary>
    public byte[]? LogoBytes { get; init; }

    /// <summary>What a click starts.</summary>
    public string LaunchTarget => Kind == AppKind.Packaged && AppUserModelId is not null
        ? Launcher.ForAppId(AppUserModelId)
        : ShortcutPath ?? Key;

    /// <summary>The pin this app becomes when pinned.</summary>
    public PinnedApp ToPin()
    {
        if (Kind == AppKind.Packaged && AppUserModelId is not null)
        {
            return new PinnedApp(Launcher.ForAppId(AppUserModelId), Name, AppUserModelId);
        }

        // Pinned by the exe it starts, so it lines up with the running app on the bar;
        // started through the shortcut, which keeps its arguments and working folder.
        bool exeTarget = TargetPath is not null
            && TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        return new PinnedApp(exeTarget ? TargetPath! : ShortcutPath ?? Key, Name, null, ShortcutPath);
    }
}

/// <summary>
/// The All apps list: Start menu shortcuts (all users and the current user) plus store apps
/// that show in Windows' own app list. Built on a worker thread; kept for a minute.
/// </summary>
internal sealed class AppCatalog
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private DateTime _builtAt = DateTime.MinValue;
    private bool _building;

    /// <summary>Raised on the UI thread when a new list is ready.</summary>
    internal event Action? Changed;

    internal IReadOnlyList<AppEntry> Apps { get; private set; } = Array.Empty<AppEntry>();

    internal bool IsReady => _builtAt != DateTime.MinValue;

    /// <summary>Starts a rebuild when the list is older than a minute. Call on the UI thread.</summary>
    internal void RefreshIfStale(int iconPixels, bool darkTheme)
    {
        if (_building || DateTime.UtcNow - _builtAt < MaxAge)
        {
            return;
        }

        _ = RefreshAsync(iconPixels, darkTheme);
    }

    private async Task RefreshAsync(int iconPixels, bool darkTheme)
    {
        _building = true;
        try
        {
            var timer = Stopwatch.StartNew();
            (List<AppEntry> apps, int shortcuts, int packaged) = await Task.Run(() => BuildAsync(iconPixels, darkTheme));

            // Back on the UI thread.
            Apps = apps;
            _builtAt = DateTime.UtcNow;
            Log.Info("App list: " + apps.Count + " apps (" + shortcuts + " shortcuts, " + packaged + " store apps) in "
                + timer.ElapsedMilliseconds + " ms.");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("The app list could not be built.", ex);
        }
        finally
        {
            _building = false;
        }
    }

    private static async Task<(List<AppEntry> Apps, int Shortcuts, int Packaged)> BuildAsync(int iconPixels, bool darkTheme)
    {
        var apps = new List<AppEntry>(256);
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        int shortcuts = 0;
        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("App list: a Start menu folder could not be read (" + root + ").", ex);
                continue;
            }

            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length == 0 || !names.Add(name))
                {
                    continue;
                }

                ShortcutInfo? info = ShortcutReader.Read(file);
                apps.Add(new AppEntry(file, name, AppKind.Shortcut)
                {
                    ShortcutPath = file,
                    TargetPath = info?.TargetPath,
                    Icon = ReadShortcutIcon(info, iconPixels),
                });
                shortcuts++;
            }
        }

        int packaged = 0;
        try
        {
            var manager = new PackageManager();
            foreach (Package package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage)
                    {
                        continue;
                    }

                    IReadOnlyList<AppListEntry> entries = await package.GetAppListEntriesAsync();
                    foreach (AppListEntry entry in entries)
                    {
                        string name = entry.DisplayInfo.DisplayName?.Trim() ?? string.Empty;
                        string appId = entry.AppUserModelId;
                        if (name.Length == 0 || string.IsNullOrEmpty(appId) || !names.Add(name))
                        {
                            continue;
                        }

                        apps.Add(new AppEntry(appId, name, AppKind.Packaged)
                        {
                            AppUserModelId = appId,
                            LogoBytes = await ReadLogoAsync(package, entry, iconPixels, darkTheme),
                        });
                        packaged++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("App list: a store app could not be read (" + SafeName(package) + ").", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("App list: store apps could not be listed.", ex);
        }

        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return (apps, shortcuts, packaged);
    }

    /// <summary>The shortcut's own icon location first, then the icon of the file it points at.</summary>
    private static IconPixels? ReadShortcutIcon(ShortcutInfo? info, int pixels)
    {
        if (info is null)
        {
            return null;
        }

        try
        {
            if (info.IconPath is string iconPath && File.Exists(iconPath))
            {
                IconPixels? icon = IconReader.FromExecutable(iconPath, pixels, info.IconIndex);
                if (icon is not null)
                {
                    return icon;
                }
            }

            if (info.TargetPath is string target && File.Exists(target))
            {
                return IconReader.FromExecutable(target, pixels);
            }
        }
        catch (Exception)
        {
            // No icon is a valid answer; the list shows a letter instead.
        }

        return null;
    }

    private static async Task<byte[]?> ReadLogoAsync(Package package, AppListEntry entry, int pixels, bool dark)
    {
        try
        {
            string? file = PackageLogo.FindTargetSizeLogo(package, entry.AppUserModelId, pixels, dark);
            if (file is not null)
            {
                return await File.ReadAllBytesAsync(file);
            }

            RandomAccessStreamReference reference = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(pixels, pixels));
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            using Stream managed = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await managed.CopyToAsync(memory);
            return memory.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SafeName(Package package)
    {
        try
        {
            return package.Id.FullName;
        }
        catch (Exception)
        {
            return "unknown package";
        }
    }
}
