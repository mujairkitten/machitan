using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using Machitan.Core;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Machitan.Shell;

/// <summary>What Windows says about a store (packaged) app's package.</summary>
/// <param name="FullName">The package full name (what removing it needs).</param>
/// <param name="Removable">False for Windows' own system packages, which Windows does not let you uninstall.</param>
/// <param name="FullTrust">A packaged desktop app, which can run as administrator.</param>
internal sealed record PackageFacts(string FullName, bool Removable, bool FullTrust);

/// <summary>How an app is uninstalled.</summary>
internal enum UninstallWay
{
    /// <summary>Not offered (a Windows system app).</summary>
    None,

    /// <summary>A store app: removed by Machitan after one question, as Windows' Start does.</summary>
    Package,

    /// <summary>A desktop app: Windows' Settings > Apps > Installed apps opens, as Windows' Start does.</summary>
    Settings,
}

/// <summary>
/// What Start and search can do with an app beyond starting it: run as administrator, open
/// its file location, its settings page, uninstall. Every answer comes from Windows (the
/// shortcut, or the package and its manifest); nothing is offered that would not work.
/// </summary>
internal static class AppActions
{
    private const int ErrorCancelled = 1223;

    private static readonly Dictionary<string, PackageFacts?> s_packages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Family!App" -> "Family"; null for desktop apps.</summary>
    internal static string? PackageFamily(AppEntry app)
    {
        if (app.Kind != AppKind.Packaged || app.AppUserModelId is not string id)
        {
            return null;
        }

        int bang = id.IndexOf('!');
        return bang > 0 ? id[..bang] : null;
    }

    internal static bool CanRunAsAdmin(AppEntry app)
    {
        if (app.Kind == AppKind.Packaged)
        {
            return Facts(app)?.FullTrust == true;
        }

        return app.ShortcutPath is not null
            && app.TargetPath is string target
            && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts the app elevated (Windows asks first). Saying no to Windows' question is not an error.</summary>
    internal static void RunAsAdmin(AppEntry app)
    {
        string target = app.Kind == AppKind.Packaged && app.AppUserModelId is string id
            ? Launcher.ForAppId(id)
            : app.ShortcutPath ?? app.Key;
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Log.Info("Run as administrator: " + app.Name + ".");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Run as administrator: " + app.Name + " was not allowed in Windows' question.");
        }
        catch (Exception ex)
        {
            Log.Error("Run as administrator failed: " + app.Name, ex);
        }
    }

    /// <summary>The file to show in File Explorer: the app's Start menu shortcut, as Windows shows it. Null for store apps.</summary>
    internal static string? FileLocation(AppEntry app)
    {
        return app.Kind == AppKind.Shortcut && app.ShortcutPath is string path && File.Exists(path) ? path : null;
    }

    /// <summary>Opens File Explorer at the file's folder with the file selected.</summary>
    internal static void ShowInExplorer(string path)
    {
        try
        {
            using Process? explorer = Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("File Explorer could not show " + path, ex);
        }
    }

    /// <summary>Store apps have a page in Settings > Apps (Advanced options).</summary>
    internal static bool HasAppSettings(AppEntry app)
    {
        return PackageFamily(app) is not null;
    }

    internal static void OpenAppSettings(AppEntry app)
    {
        if (PackageFamily(app) is string family)
        {
            Launcher.Start("ms-settings:appsfeatures-app?" + family);
        }
    }

    internal static UninstallWay UninstallWayOf(AppEntry app)
    {
        if (app.Kind == AppKind.Packaged)
        {
            return Facts(app)?.Removable == true ? UninstallWay.Package : UninstallWay.None;
        }

        return UninstallWay.Settings;
    }

    /// <summary>Windows' Settings > Apps > Installed apps, where desktop apps are removed.</summary>
    internal static void OpenInstalledApps()
    {
        Launcher.Start("ms-settings:appsfeatures");
    }

    /// <summary>Removes a store app for this user. Null when it worked, else what went wrong.</summary>
    internal static async Task<string?> UninstallPackageAsync(AppEntry app)
    {
        if (Facts(app) is not PackageFacts facts || !facts.Removable)
        {
            return Strings.UninstallNotAllowed;
        }

        try
        {
            Log.Info("Uninstall: removing " + facts.FullName + ".");
            DeploymentResult result = await new PackageManager().RemovePackageAsync(facts.FullName);
            if (result.ExtendedErrorCode is not null)
            {
                Log.Error("Uninstall: Windows could not remove " + facts.FullName + ": " + result.ErrorText, result.ExtendedErrorCode);
                return Strings.UninstallFailed;
            }

            lock (s_packages)
            {
                s_packages.Remove(app.AppUserModelId ?? string.Empty);
            }

            Log.Info("Uninstall: " + app.Name + " removed.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall: " + app.Name + " failed.", ex);
            return Strings.UninstallFailed;
        }
    }

    /// <summary>The app's package, looked up once per run (null when Windows has no such package).</summary>
    internal static PackageFacts? Facts(AppEntry app)
    {
        if (app.AppUserModelId is not string id || PackageFamily(app) is not string family)
        {
            return null;
        }

        lock (s_packages)
        {
            if (s_packages.TryGetValue(id, out PackageFacts? known))
            {
                return known;
            }
        }

        PackageFacts? facts = null;
        try
        {
            foreach (Package package in new PackageManager().FindPackagesForUser(string.Empty, family))
            {
                if (package.IsFramework || package.IsResourcePackage)
                {
                    continue;
                }

                bool removable = package.SignatureKind != PackageSignatureKind.System;
                facts = new PackageFacts(package.Id.FullName, removable, IsFullTrust(package, id[(id.IndexOf('!') + 1)..]));
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("A store app's package could not be read (" + family + ").", ex);
        }

        lock (s_packages)
        {
            s_packages[id] = facts;
        }

        return facts;
    }

    /// <summary>
    /// A packaged desktop app, from the package's AppxManifest.xml: its Application entry runs
    /// as Windows.FullTrustApplication, or declares a desktop runtime behaviour.
    /// </summary>
    private static bool IsFullTrust(Package package, string appId)
    {
        try
        {
            string manifest = Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml");
            if (!File.Exists(manifest))
            {
                return false;
            }

            XDocument document = XDocument.Load(manifest);
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName != "Application"
                    || !string.Equals((string?)element.Attribute("Id"), appId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (XAttribute attribute in element.Attributes())
                {
                    string name = attribute.Name.LocalName;
                    string value = attribute.Value;
                    if ((name == "EntryPoint" && value == "Windows.FullTrustApplication")
                        || (name == "RuntimeBehavior" && (value == "packagedClassicApp" || value == "win32App"))
                        || (name == "TrustLevel" && value == "mediumIL"))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        catch (Exception)
        {
            // An unreadable manifest: do not offer it.
        }

        return false;
    }
}
