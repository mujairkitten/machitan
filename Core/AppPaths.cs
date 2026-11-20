using System;
using System.IO;

namespace Machitan.Core;

/// <summary>Where Machitan keeps its files: %AppData%\Machitan.</summary>
internal static class AppPaths
{
    internal static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Machitan");

    internal static string SettingsFile { get; } = Path.Combine(DataFolder, "settings.json");

    /// <summary>Apps pinned to the bar ("Pin to taskbar").</summary>
    internal static string PinsFile { get; } = Path.Combine(DataFolder, "pins.json");

    /// <summary>Apps pinned to Start's Pinned box ("Pin to Start").</summary>
    internal static string StartPinsFile { get; } = Path.Combine(DataFolder, "start-pins.json");

    /// <summary>The bar's bubbles (Start, the apps, app groups) and which apps sit where.</summary>
    internal static string BarLayoutFile { get; } = Path.Combine(DataFolder, "bar-layout.json");

    internal static string LogFile { get; } = Path.Combine(DataFolder, "diagnostics.log");

    internal static string OldLogFile { get; } = Path.Combine(DataFolder, "diagnostics.old.log");

    /// <summary>Holds Explorer's taskbar state from before Machitan changed it.</summary>
    internal static string TaskbarStateFile { get; } = Path.Combine(DataFolder, "explorer-taskbar.state");

    internal static void EnsureDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
    }
}
