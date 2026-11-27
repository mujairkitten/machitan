using System;
using System.IO;

namespace Machitan.Shell;

/// <summary>
/// A name to show for the app behind a media session (Windows gives its app ID): the app's own
/// name from Start's list when it is there, else the ID made readable. For Dynaland and the
/// Now playing widget.
/// </summary>
internal static class MediaAppNames
{
    internal static string NameFor(AppCatalog catalog, string appId)
    {
        if (appId.Length == 0)
        {
            return Strings.DynAnApp;
        }

        try
        {
            foreach (AppEntry app in catalog.Apps)
            {
                if (app.AppUserModelId is string id && string.Equals(id, appId, StringComparison.OrdinalIgnoreCase))
                {
                    return app.Name;
                }
            }

            string name = appId;
            int bang = name.LastIndexOf('!');
            if (bang >= 0 && bang < name.Length - 1)
            {
                name = name[(bang + 1)..];
            }

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                foreach (AppEntry app in catalog.Apps)
                {
                    if (app.TargetPath is string target
                        && string.Equals(Path.GetFileName(target), name, StringComparison.OrdinalIgnoreCase))
                    {
                        return app.Name;
                    }
                }

                name = name[..^4];
            }

            // The browsers name their sessions with a short ID.
            return name switch
            {
                "MSEdge" => "Microsoft Edge",
                "Chrome" => "Google Chrome",
                _ => name,
            };
        }
        catch (Exception)
        {
            return appId;
        }
    }
}
