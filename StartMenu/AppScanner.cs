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

    public static List<ScannedApp> GetAllApps()
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ScannedApp>();

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (name.Equals("desktop", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seen.Add(name))
                    continue; // user folder wins over the shared one
                result.Add(new ScannedApp(name, lnk));
            }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
