using System.IO;
using System.Text;

namespace Machitan.Core;

/// <summary>Writes a file so that a crash mid-write never leaves a half-written file behind.</summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes to a temporary file first, then swaps it in. When the target already exists the
    /// previous version is kept beside it as ".bak".
    /// </summary>
    internal static void WriteAllText(string path, string contents)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, contents, Utf8NoBom);

        if (File.Exists(path))
        {
            File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, path);
        }
    }
}
