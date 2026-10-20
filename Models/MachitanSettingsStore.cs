using System.IO;
using System.Text.Json;

namespace Machitan;

internal static class MachitanSettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Machitan", "settings.json");

    public static MachitanSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<MachitanSettings>(File.ReadAllText(FilePath));
                if (settings != null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch { }
        return new MachitanSettings();
    }

    public static void Save(MachitanSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var normalized = settings.Clone();
            normalized.Normalize();
            WriteAtomically(JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void WriteAtomically(string contents)
    {
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
