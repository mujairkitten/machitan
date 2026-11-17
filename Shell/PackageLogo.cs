using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Machitan.Core;
using Windows.ApplicationModel;

namespace Machitan.Shell;

/// <summary>Package helpers shared by the bar and the Start menu.</summary>
internal static class PackageLogo
{
    /// <summary>"Name_Version_Arch_ResourceId_PublisherId" to "Name_PublisherId".</summary>
    internal static string? FamilyFromFullName(string? packageFullName)
    {
        if (string.IsNullOrEmpty(packageFullName))
        {
            return null;
        }

        string[] parts = packageFullName.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[^1] : null;
    }

    /// <summary>"Family!AppId" to "Family".</summary>
    internal static string? FamilyFromAppId(string? appUserModelId)
    {
        if (string.IsNullOrEmpty(appUserModelId))
        {
            return null;
        }

        int bang = appUserModelId.IndexOf('!');
        return bang > 0 ? appUserModelId.Substring(0, bang) : null;
    }

    /// <summary>
    /// Finds the app's small logo file ("Square44x44Logo" in its manifest) in the "target size"
    /// form: "Name.targetsize-24_altform-unplated.png" and similar. Preference: unplated in the
    /// bar's theme variant, then the other unplated variant, then plated; within that, the
    /// smallest size at least as big as needed, else the biggest. Null when the package has none.
    /// Runs on a worker thread.
    /// </summary>
    internal static string? FindTargetSizeLogo(Package package, string appUserModelId, int size, bool dark)
    {
        try
        {
            string root = package.InstalledPath;
            XDocument manifest = XDocument.Load(Path.Combine(root, "AppxManifest.xml"));

            // "Family!AppId": the part after the "!" is the Application Id in the manifest.
            int bang = appUserModelId.IndexOf('!');
            string appId = bang >= 0 ? appUserModelId.Substring(bang + 1) : string.Empty;

            XElement? application = manifest.Descendants()
                .Where(e => e.Name.LocalName == "Application")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Id"), appId, StringComparison.OrdinalIgnoreCase))
                ?? manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application");

            string? logo = application?.Descendants()
                .Where(e => e.Name.LocalName == "VisualElements")
                .Select(e => (string?)e.Attribute("Square44x44Logo"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (logo is null)
            {
                return null;
            }

            string logoPath = Path.Combine(root, logo.Replace('/', '\\'));
            string? folder = Path.GetDirectoryName(logoPath);
            string baseName = Path.GetFileNameWithoutExtension(logoPath);
            string extension = Path.GetExtension(logoPath);
            if (folder is null || !Directory.Exists(folder))
            {
                return null;
            }

            string preferred = dark ? "altform-unplated" : "altform-lightunplated";
            string other = dark ? "altform-lightunplated" : "altform-unplated";

            string? best = null;
            int bestTier = int.MaxValue;
            int bestSize = 0;

            foreach (string candidate in Directory.EnumerateFiles(folder, baseName + ".*" + extension))
            {
                string name = Path.GetFileName(candidate);
                int start = baseName.Length + 1;
                int length = name.Length - start - extension.Length;
                if (length <= 0)
                {
                    continue;
                }

                string[] qualifiers = name.Substring(start, length).ToLowerInvariant().Split('_');
                if (qualifiers.Any(q => q.StartsWith("contrast-", StringComparison.Ordinal)))
                {
                    continue;
                }

                string? target = qualifiers.FirstOrDefault(q => q.StartsWith("targetsize-", StringComparison.Ordinal));
                if (target is null || !int.TryParse(target.AsSpan("targetsize-".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetSize))
                {
                    continue;
                }

                int tier = qualifiers.Contains(preferred) ? 0
                    : qualifiers.Contains(other) ? 1
                    : qualifiers.Any(q => q.StartsWith("altform-", StringComparison.Ordinal)) ? 3
                    : 2;

                if (tier < bestTier || (tier == bestTier && IsBetterSize(targetSize, bestSize, size)))
                {
                    best = candidate;
                    bestTier = tier;
                    bestSize = targetSize;
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            Log.Warn("Package logo: the target-size logo files could not be read; using the general logo.", ex);
            return null;
        }
    }

    /// <summary>Smallest size at least as big as needed wins; below that, bigger is better.</summary>
    private static bool IsBetterSize(int candidate, int current, int needed)
    {
        if (current == 0)
        {
            return true;
        }

        bool candidateBigEnough = candidate >= needed;
        bool currentBigEnough = current >= needed;
        if (candidateBigEnough != currentBigEnough)
        {
            return candidateBigEnough;
        }

        return candidateBigEnough ? candidate < current : candidate > current;
    }
}
