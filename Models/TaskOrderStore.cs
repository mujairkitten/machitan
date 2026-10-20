using System.IO;
using System.Text.Json;

namespace Machitan;

internal static class TaskOrderStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Machitan", "task-order.json");

    public static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var order = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
                if (order != null)
                    return Normalize(order);
            }
        }
        catch
        {
        }

        return new List<string>();
    }

    public static void Save(IEnumerable<string> order)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            WriteAtomically(JsonSerializer.Serialize(Normalize(order), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static List<string> Normalize(IEnumerable<string?> order) => order
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Select(key => key!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(5000)
        .ToList();

    private static void WriteAtomically(string contents)
    {
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
