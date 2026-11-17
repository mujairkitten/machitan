using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Machitan.Core;

/// <summary>
/// Diagnostics log at %AppData%\Machitan\diagnostics.log.
/// Logging never throws: a failure to write is swallowed so it can never take the shell down.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1_000_000;

    private static readonly Lock Gate = new();

    internal static void Info(string message)
    {
        Write("INFO", message, null);
    }

    internal static void Warn(string message, Exception? exception = null)
    {
        Write("WARN", message, exception);
    }

    internal static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            string line =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                " [" + level + "] " +
                "[" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "] " +
                message;

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            line += Environment.NewLine;

            lock (Gate)
            {
                AppPaths.EnsureDataFolder();
                RotateIfLarge();

                // The guardian process writes to the same file, so allow one retry.
                try
                {
                    File.AppendAllText(AppPaths.LogFile, line);
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                    File.AppendAllText(AppPaths.LogFile, line);
                }
            }
        }
        catch (Exception)
        {
            // Deliberately empty: diagnostics must never be fatal.
        }
    }

    private static void RotateIfLarge()
    {
        var info = new FileInfo(AppPaths.LogFile);
        if (info.Exists && info.Length > MaxBytes)
        {
            File.Move(AppPaths.LogFile, AppPaths.OldLogFile, overwrite: true);
        }
    }
}
