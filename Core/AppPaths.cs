using System;
using System.IO;

namespace Machitan.Core;

/// <summary>Where Machitan keeps its files: %AppData%\Machitan.</summary>
internal static class AppPaths
{
    internal static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Machitan");

    internal static string SettingsFile { get; } = Path.Combine(DataFolder, "settings.json");

    internal static string PinsFile { get; } = Path.Combine(DataFolder, "pins.json");

    internal static string LogFile { get; } = Path.Combine(DataFolder, "diagnostics.log");

    internal static string OldLogFile { get; } = Path.Combine(DataFolder, "diagnostics.old.log");

    /// <summary>Holds Explorer's taskbar state from before Machitan changed it.</summary>
    internal static string TaskbarStateFile { get; } = Path.Combine(DataFolder, "explorer-taskbar.state");

    internal static void EnsureDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
    }
}
