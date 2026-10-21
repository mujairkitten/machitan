using System.IO;

namespace Machitan.StartMenu;

/// <summary>
/// Finds installed programs by scanning the two Start Menu\Programs folders
/// for .lnk shortcuts — the same source the real Start menu reads.
/// Shortcuts launch directly via Process.Start, no link-resolving needed.
/// </summary>
internal static class AppScanner
{
    public sealed record ScannedApp(string Name, string LinkPath);

    // Session cache with a short TTL: apps installed or removed while
    // Machitan runs show up within a few minutes instead of needing a
    // shell restart, without rescanning on every menu open.
    private static List<ScannedApp>? _cache;
    private static DateTime _cacheTime;

    public static List<ScannedApp> GetAllApps()
    {
        if (_cache != null && DateTime.UtcNow - _cacheTime < TimeSpan.FromMinutes(5))
            return _cache;

        // Per-user folder FIRST: when a shortcut exists in both folders, the
        // user's own copy wins (that's how Windows resolves it too).
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ScannedApp>();

        foreach (var dir in dirs)
        {
            foreach (var lnk in EnumerateLinks(dir))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (name.Equals("desktop", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seen.Add(name))
                    continue; // first folder scanned (the user's) wins
                result.Add(new ScannedApp(name, lnk));
            }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _cache = result;
        _cacheTime = DateTime.UtcNow;
        return result;
    }

    // Recursive enumeration that tolerates unreadable subfolders: one bad
    // folder must not empty the whole apps list.
    private static IEnumerable<string> EnumerateLinks(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir, "*.lnk"); }
            catch { files = Array.Empty<string>(); }

            foreach (var file in files)
                yield return file;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { subdirs = Array.Empty<string>(); }

            foreach (var subdir in subdirs)
                pending.Push(subdir);
        }
    }
}
