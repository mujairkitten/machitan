using System;
using System.Diagnostics;
using System.IO;
using Machitan.Core;

namespace Machitan.Shell;

/// <summary>Starts apps, shortcuts and files the way Explorer would.</summary>
internal static class Launcher
{
    private const string AppsFolderPrefix = "shell:AppsFolder\\";

    /// <summary>An exe, a .lnk, a document, or "shell:AppsFolder\&lt;app ID&gt;" for store apps.</summary>
    internal static bool Start(string target)
    {
        try
        {
            ProcessStartInfo start;
            if (target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Explorer starts store apps by app ID.
                start = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true,
                };
                start.ArgumentList.Add(target);
            }
            else
            {
                start = new ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = File.Exists(target) ? Path.GetDirectoryName(target) ?? string.Empty : string.Empty,
                };
            }

            using Process? started = Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start: " + target, ex);
            return false;
        }
    }

    internal static string ForAppId(string appUserModelId)
    {
        return AppsFolderPrefix + appUserModelId;
    }
}
