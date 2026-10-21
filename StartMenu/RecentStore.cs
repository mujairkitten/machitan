using System.IO;
using System.Text.Json;

namespace Machitan.StartMenu;

/// <summary>
/// Recently launched apps (via the start menu), most-recent first, kept in
/// %AppData%\Machitan\recent.json. Replaces the old "Recently used" section
/// that quietly showed the pinned list.
/// </summary>
internal static class RecentStore
{
    private const int MaxItems = 8;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Machitan", "recent.json");

    public static List<PinnedApp> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var items = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(FilePath));
                if (items != null)
                    return items.Where(p => File.Exists(p.Path)).Take(MaxItems).ToList();
            }
        }
        catch { }

        return new List<PinnedApp>();
    }

    public static void Save(List<PinnedApp> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(items.Take(MaxItems).ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch { }
    }

    public static void Record(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var items = Load();
            items.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, new PinnedApp { Name = name, Path = path });

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(items.Take(MaxItems).ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch { }
    }
}
