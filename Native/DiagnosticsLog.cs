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
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }

    public static void WriteException(string context, Exception ex)
    {
        Write($"{context}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
    }
}
