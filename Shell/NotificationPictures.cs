using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Machitan.Core;
using Machitan.Interop;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Machitan.Shell;

/// <summary>A notification's pictures, as bytes read on a worker thread.</summary>
internal sealed record NotificationPictureBytes(
    byte[]? Hero,
    byte[]? Logo,
    bool LogoCircle,
    IReadOnlyList<byte[]> Inline,
    string Attribution);

/// <summary>
/// A notification's pictures (CHANGELOG Part 25). Windows' notification reader gives Machitan
/// a notification's text only (NotificationBinding offers text elements, nothing else); the
/// pictures are only in the notification's own description (its XML), which Windows keeps in
/// its notification database, %LocalAppData%\Microsoft\Windows\Notifications\wpndatabase.db
/// (SQLite; table Notification, column Payload). This reads that file with Windows' own SQLite
/// (read only), takes the XML of only the notifications Windows' list holds now, and reads the
/// pictures they name: files on this PC, an app's own package files (ms-appx, ms-appdata) and
/// web pictures. Microsoft does not document the file, so a Windows update may change it: then
/// the pictures are simply missing, logged once, and the text list goes on.
///
/// No notification history: nothing is written; the bytes go to NotificationService, which keeps
/// them only while Windows' list holds the notification. The log gets counts, never a
/// notification's text, app, file names or addresses.
/// </summary>
internal static class NotificationPictures
{
    private const int MaxPictureBytes = 8 * 1024 * 1024;
    private const int MaxInline = 2;
    private const int RecentRows = 500;
    private const long MatchWindowTicks = 10L * 60 * 10_000_000; // 10 minutes, in FILETIME units

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> PackageRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Web = MakeWebClient();
    private static bool _databaseFailureLogged;
    private static bool _matchLogged;

    private static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "Windows",
        "Notifications",
        "wpndatabase.db");

    /// <summary>
    /// The pictures of the given notifications (by Windows' id). Runs on a worker thread.
    /// A notification whose description is not found, or that has no pictures, is left out.
    /// </summary>
    internal static async Task<Dictionary<uint, NotificationPictureBytes>> FindAsync(IReadOnlyList<NotificationEntry> wanted)
    {
        var result = new Dictionary<uint, NotificationPictureBytes>();
        Dictionary<uint, ToastXml> described = ReadDescriptions(wanted);
        foreach (NotificationEntry entry in wanted)
        {
            if (!described.TryGetValue(entry.Id, out ToastXml? toast) || !toast.HasPictures)
            {
                continue;
            }

            byte[]? hero = toast.Hero is null ? null : await ReadAsync(toast.Hero, toast.BaseUri, entry.AppId);
            byte[]? logo = toast.Logo is null ? null : await ReadAsync(toast.Logo, toast.BaseUri, entry.AppId);
            var inline = new List<byte[]>();
            foreach (string source in toast.Inline)
            {
                if (await ReadAsync(source, toast.BaseUri, entry.AppId) is byte[] bytes)
                {
                    inline.Add(bytes);
                }
            }

            if (hero is not null || logo is not null || inline.Count > 0 || toast.Attribution.Length > 0)
            {
                result[entry.Id] = new NotificationPictureBytes(hero, logo, toast.LogoCircle, inline, toast.Attribution);
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- Windows' database

    /// <summary>
    /// Each wanted notification's description: by Windows' id first (checked against its
    /// title), else the recent notification with the same title that arrived closest to it.
    /// </summary>
    private static Dictionary<uint, ToastXml> ReadDescriptions(IReadOnlyList<NotificationEntry> wanted)
    {
        var found = new Dictionary<uint, ToastXml>();
        string path = DatabasePath;
        if (!File.Exists(path))
        {
            LogDatabaseFailure("Windows' notification database is not where it used to be; notifications show without pictures.", null);
            return found;
        }

        nint db = OpenDatabase(path);
        if (db == 0)
        {
            return found;
        }

        int byId = 0;
        int byText = 0;
        try
        {
            WinSqlite.BusyTimeout(db, 500);
            foreach (NotificationEntry entry in wanted)
            {
                if (ReadPayloadById(db, entry.Id) is string payload
                    && ToastXml.Parse(payload) is ToastXml toast
                    && toast.Matches(entry.Title))
                {
                    found[entry.Id] = toast;
                    byId++;
                }
            }

            if (found.Count < wanted.Count)
            {
                List<(ToastXml Toast, long Arrival)> recent = ReadRecent(db);
                foreach (NotificationEntry entry in wanted)
                {
                    if (found.ContainsKey(entry.Id) || entry.Title.Length == 0)
                    {
                        continue;
                    }

                    long created = entry.Time.ToFileTime();
                    ToastXml? best = null;
                    long bestGap = long.MaxValue;
                    foreach ((ToastXml toast, long arrival) in recent)
                    {
                        long gap = Math.Abs(arrival - created);
                        if (gap < bestGap && gap <= MatchWindowTicks && toast.Matches(entry.Title))
                        {
                            best = toast;
                            bestGap = gap;
                        }
                    }

                    if (best is not null)
                    {
                        found[entry.Id] = best;
                        byText++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogDatabaseFailure("Windows' notification database could not be read; notifications show without pictures.", ex);
        }
        finally
        {
            WinSqlite.Close(db);
        }

        if (!_matchLogged && wanted.Count > 0)
        {
            _matchLogged = true;
            Log.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Notifications: descriptions found in Windows' notification database: {0} by id, {1} by title and time, {2} not found.",
                byId,
                byText,
                wanted.Count - byId - byText));
        }

        return found;
    }

    /// <summary>Read only; failing that (Windows' write-ahead log can need it), read and write, used only to read.</summary>
    private static nint OpenDatabase(string path)
    {
        foreach (int flags in new[] { WinSqlite.OpenReadOnly, WinSqlite.OpenReadWrite })
        {
            nint db = 0;
            try
            {
                if (WinSqlite.Open(path, out db, flags, 0) == WinSqlite.Ok)
                {
                    return db;
                }

                string error = WinSqlite.LastError(db);
                WinSqlite.Close(db);
                if (flags == WinSqlite.OpenReadWrite)
                {
                    LogDatabaseFailure("Windows' notification database could not be opened (" + error + "); notifications show without pictures.", null);
                }
            }
            catch (Exception ex)
            {
                if (db != 0)
                {
                    WinSqlite.Close(db);
                }

                LogDatabaseFailure("Windows' own SQLite (winsqlite3.dll) could not be used; notifications show without pictures.", ex);
                return 0;
            }
        }

        return 0;
    }

    private static string? ReadPayloadById(nint db, uint id)
    {
        if (WinSqlite.Prepare(db, "SELECT Payload FROM Notification WHERE Id = ?1", -1, out nint statement, 0) != WinSqlite.Ok)
        {
            throw new InvalidOperationException("Notification table: " + WinSqlite.LastError(db));
        }

        try
        {
            WinSqlite.BindInt64(statement, 1, id);
            return WinSqlite.Step(statement) == WinSqlite.Row
                ? Decode(WinSqlite.ColumnBytesCopy(statement, 0))
                : null;
        }
        finally
        {
            WinSqlite.FinalizeStatement(statement);
        }
    }

    private static List<(ToastXml Toast, long Arrival)> ReadRecent(nint db)
    {
        var rows = new List<(ToastXml, long)>();
        string sql = "SELECT Payload, ArrivalTime FROM Notification WHERE Type = 'toast' ORDER BY ArrivalTime DESC LIMIT "
            + RecentRows.ToString(CultureInfo.InvariantCulture);
        if (WinSqlite.Prepare(db, sql, -1, out nint statement, 0) != WinSqlite.Ok)
        {
            throw new InvalidOperationException("Notification table: " + WinSqlite.LastError(db));
        }

        try
        {
            while (WinSqlite.Step(statement) == WinSqlite.Row)
            {
                if (Decode(WinSqlite.ColumnBytesCopy(statement, 0)) is string payload
                    && ToastXml.Parse(payload) is ToastXml toast)
                {
                    rows.Add((toast, WinSqlite.ColumnInt64(statement, 1)));
                }
            }
        }
        finally
        {
            WinSqlite.FinalizeStatement(statement);
        }

        return rows;
    }

    /// <summary>The XML is stored as UTF-8 (sometimes UTF-16).</summary>
    private static string? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start).TrimEnd('\0');
    }

    private static void LogDatabaseFailure(string message, Exception? ex)
    {
        if (_databaseFailureLogged)
        {
            return;
        }

        _databaseFailureLogged = true;
        Log.Warn("Notifications: " + message, ex);
    }

    // ---------------------------------------------------------------- the pictures' files

    /// <summary>A picture's bytes from where the notification says it is; null when it cannot be had.</summary>
    private static async Task<byte[]?> ReadAsync(string source, string? baseUri, string appId)
    {
        try
        {
            Uri? uri = ToUri(source, baseUri);
            if (uri is null)
            {
                return null;
            }

            switch (uri.Scheme.ToLowerInvariant())
            {
                case "file":
                    return ReadFile(uri.LocalPath);
                case "ms-appx":
                    return PackageRoot(appId) is string root ? ReadFile(PackageFile(root, uri)) : null;
                case "ms-appdata":
                    return ReadFile(AppDataFile(appId, uri));
                case "http":
                case "https":
                    return await DownloadAsync(uri);
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            // A picture that cannot be read is left out; the notification still shows.
            return null;
        }
    }

    private static Uri? ToUri(string source, string? baseUri)
    {
        source = source.Trim();
        if (source.Length == 0)
        {
            return null;
        }

        if (Path.IsPathRooted(source) && source.Length > 2 && source[1] == ':')
        {
            return new Uri(source);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? absolute))
        {
            return absolute;
        }

        // Relative: against the notification's baseUri, else the app's own package.
        string root = string.IsNullOrEmpty(baseUri) ? "ms-appx:///" : baseUri;
        return Uri.TryCreate(new Uri(root, UriKind.Absolute), source, out Uri? combined) ? combined : null;
    }

    private static byte[]? ReadFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        return info.Length is > 0 and <= MaxPictureBytes ? File.ReadAllBytes(path) : null;
    }

    private static async Task<byte[]?> DownloadAsync(Uri uri)
    {
        using HttpResponseMessage response = await Web.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPictureBytes)
        {
            return null;
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        return bytes.Length is > 0 and <= MaxPictureBytes ? bytes : null;
    }

    /// <summary>The app's installed package folder (packaged apps), cached per package family.</summary>
    private static string? PackageRoot(string appId)
    {
        string? family = PackageLogo.FamilyFromAppId(appId);
        if (family is null)
        {
            return null;
        }

        lock (Gate)
        {
            if (PackageRoots.TryGetValue(family, out string? known))
            {
                return known;
            }
        }

        string? root = null;
        try
        {
            Package? package = new PackageManager().FindPackagesForUser(string.Empty, family).FirstOrDefault();
            root = package?.InstalledPath;
        }
        catch (Exception ex)
        {
            Log.Warn("Notifications: an app's package folder could not be found.", ex);
        }

        lock (Gate)
        {
            PackageRoots[family] = root;
        }

        return root;
    }

    /// <summary>
    /// A file in the package; when only resource-qualified files exist ("name.scale-200.png",
    /// "name.targetsize-48.png"), the biggest of them (high-contrast ones left out).
    /// </summary>
    private static string? PackageFile(string root, Uri uri)
    {
        string relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', '\\');
        string path = Path.Combine(root, relative);
        if (File.Exists(path))
        {
            return path;
        }

        string? folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string? best = null;
        int bestNumber = -1;
        foreach (string candidate in Directory.EnumerateFiles(folder, stem + ".*" + extension))
        {
            string name = Path.GetFileName(candidate).ToLowerInvariant();
            if (name.Contains("contrast-", StringComparison.Ordinal))
            {
                continue;
            }

            int number = QualifierNumber(name);
            if (number > bestNumber)
            {
                best = candidate;
                bestNumber = number;
            }
        }

        return best;
    }

    /// <summary>The number in "scale-200" or "targetsize-48" (0 when none).</summary>
    private static int QualifierNumber(string name)
    {
        foreach (string key in new[] { "scale-", "targetsize-" })
        {
            int at = name.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            int start = at + key.Length;
            int end = start;
            while (end < name.Length && char.IsDigit(name[end]))
            {
                end++;
            }

            if (end > start && int.TryParse(name.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                return number;
            }
        }

        return 0;
    }

    /// <summary>"ms-appdata:///local/..." (or roaming, temp) in the app's own data folder.</summary>
    private static string? AppDataFile(string appId, Uri uri)
    {
        string? family = PackageLogo.FamilyFromAppId(appId);
        if (family is null)
        {
            return null;
        }

        string[] parts = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Split('/', 2);
        if (parts.Length < 2)
        {
            return null;
        }

        string? folder = parts[0].ToLowerInvariant() switch
        {
            "local" => "LocalState",
            "roaming" => "RoamingState",
            "temp" => "TempState",
            _ => null,
        };
        if (folder is null)
        {
            return null;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            family,
            folder,
            parts[1].Replace('/', '\\'));
    }

    private static HttpClient MakeWebClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Machitan");
        return client;
    }

    // ---------------------------------------------------------------- the description

    /// <summary>What Machitan uses from a notification's XML.</summary>
    private sealed class ToastXml
    {
        private ToastXml(string title, string? baseUri)
        {
            Title = title;
            BaseUri = baseUri;
        }

        internal string Title { get; }

        internal string? BaseUri { get; }

        internal string? Hero { get; private set; }

        internal string? Logo { get; private set; }

        internal bool LogoCircle { get; private set; }

        internal List<string> Inline { get; } = new();

        internal string Attribution { get; private set; } = string.Empty;

        internal bool HasPictures => Hero is not null || Logo is not null || Inline.Count > 0 || Attribution.Length > 0;

        internal bool Matches(string title)
        {
            return title.Length == 0 || string.Equals(Title, title.Trim(), StringComparison.Ordinal);
        }

        /// <summary>Null when it is not a notification's XML.</summary>
        internal static ToastXml? Parse(string payload)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(payload);
            }
            catch (Exception)
            {
                return null;
            }

            XElement? visual = document.Root?.Element("visual");
            if (visual is null)
            {
                return null;
            }

            XElement? binding = visual.Elements("binding")
                .FirstOrDefault(b => string.Equals((string?)b.Attribute("template"), "ToastGeneric", StringComparison.OrdinalIgnoreCase))
                ?? visual.Elements("binding").FirstOrDefault();
            if (binding is null)
            {
                return null;
            }

            bool generic = string.Equals((string?)binding.Attribute("template"), "ToastGeneric", StringComparison.OrdinalIgnoreCase);
            string title = string.Empty;
            string attribution = string.Empty;
            foreach (XElement text in binding.Descendants("text"))
            {
                string value = text.Value.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                if (string.Equals((string?)text.Attribute("placement"), "attribution", StringComparison.OrdinalIgnoreCase))
                {
                    attribution = value;
                }
                else if (title.Length == 0)
                {
                    title = value;
                }
            }

            string? baseUri = (string?)binding.Attribute("baseUri") ?? (string?)visual.Attribute("baseUri");
            var toast = new ToastXml(title, baseUri) { Attribution = attribution };
            foreach (XElement image in binding.Descendants("image"))
            {
                string? source = ((string?)image.Attribute("src"))?.Trim();
                if (string.IsNullOrEmpty(source))
                {
                    continue;
                }

                string placement = ((string?)image.Attribute("placement") ?? string.Empty).Trim();
                if (string.Equals(placement, "hero", StringComparison.OrdinalIgnoreCase))
                {
                    toast.Hero ??= source;
                }
                else if (string.Equals(placement, "appLogoOverride", StringComparison.OrdinalIgnoreCase) || !generic)
                {
                    // The older templates' image is shown where a logo override goes.
                    if (toast.Logo is null)
                    {
                        toast.Logo = source;
                        toast.LogoCircle = string.Equals((string?)image.Attribute("hint-crop"), "circle", StringComparison.OrdinalIgnoreCase);
                    }
                }
                else if (toast.Inline.Count < MaxInline)
                {
                    toast.Inline.Add(source);
                }
            }

            return toast;
        }
    }
}
