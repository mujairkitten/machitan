using System;
using System.IO;

namespace Machitan.Native;

internal static class DiagnosticsLog
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "Machitan.log");

    public static void Write(string message)
    {
        try
        {
            RotateIfNeeded();
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }

    private static void RotateIfNeeded()
    {
        // Single .old generation, ~256 KB cap — diagnostics must not become
        // an unbounded file in %TEMP% over weeks of uptime.
        var info = new FileInfo(LogPath);
        if (info.Exists && info.Length > 256 * 1024)
            File.Move(LogPath, LogPath + ".old", overwrite: true);
    }

    public static void WriteException(string context, Exception ex)
    {
        Write($"{context}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
    }
}
