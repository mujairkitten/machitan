using System;
using System.IO;
using Microsoft.UI.Windowing;

namespace Machitan.Core;

/// <summary>Machitan's icon (machitan.ico, next to Machitan.exe) on a window, for the taskbar and Alt+Tab.</summary>
internal static class AppIcon
{
    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "machitan.ico");

    internal static void Apply(AppWindow window)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Log.Warn("Machitan's icon file is missing (" + FilePath + "); the window keeps Windows' default icon.");
                return;
            }

            window.SetIcon(FilePath);
        }
        catch (Exception ex)
        {
            Log.Warn("Machitan's icon could not be set on a window.", ex);
        }
    }
}
