using System.IO;
using System.Linq;
using System.Text.Json;

namespace Machitan.StartMenu;

/// <summary>
/// Loads/saves the pinned-apps list as JSON in %AppData%\Machitan\pinned.json.
/// </summary>
internal static class PinnedStore
{
    private static string DirPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Machitan");

    private static string FilePath => Path.Combine(DirPath, "pinned.json");

    public static List<PinnedApp> Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var apps = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(FilePath));
                if (apps != null)
                    return apps.Where(a => File.Exists(a.Path)).ToList();
            }
            catch { }

            // The file exists but cannot be read right now. Fall back to the
            // seed list for THIS session only — never save over the user's
            // pins because of one bad read.
            return SeedDefaults();
        }

        var seeded = SeedDefaults();
        Save(seeded);
        return seeded;
    }

    public static void Save(List<PinnedApp> apps)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            var json = JsonSerializer.Serialize(apps, new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch { }
    }

    private static List<PinnedApp> SeedDefaults()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new (string Name, string Path)[]
        {
            ("Notepad",  Path.Combine(sys, "notepad.exe")),
            ("Firefox",  Path.Combine(pf, @"Mozilla Firefox\firefox.exe")),
            ("Chrome",   Path.Combine(pf, @"Google\Chrome\Application\chrome.exe")),
            ("VS Code",  Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe")),
            ("Terminal", Path.Combine(local, @"Microsoft\WindowsApps\wt.exe")),
        };

        return candidates
            .Where(c => File.Exists(c.Path))
            .Select(c => new PinnedApp { Name = c.Name, Path = c.Path })
            .ToList();
    }
}
