using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Core;

namespace Machitan.Shell;

/// <summary>One line of synced lyrics: when it starts, and its words (empty for a pause in the singing).</summary>
internal sealed record LyricLine(TimeSpan At, string Text);

/// <summary>A song's synced lyrics, in time order.</summary>
internal sealed class SyncedLyrics
{
    internal SyncedLyrics(List<LyricLine> lines)
    {
        Lines = lines;
    }

    internal IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>The line being sung at <paramref name="position"/>: the last one that started; -1 before the first.</summary>
    internal int IndexAt(TimeSpan position)
    {
        int low = 0;
        int high = Lines.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (Lines[middle].At <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }
}

/// <summary>
/// Synced lyrics from LRCLIB (lrclib.net: a free, open lyrics database; no account, no key),
/// for Dynaland and the Now playing widget, one for both (ShellHost.Lyrics; Sam, 2026-11-26:
/// "Synced lyrics in Dynaland", from LRCLIB; off until turned on in Settings > Dynaland). Asked
/// once per song as it starts (both asking at once share one search): its title and artist go to
/// lrclib.net, and the answer whose length is within 2 seconds of the song's, with synced
/// lyrics, is used. Found (and not found) songs are remembered while Machitan runs, so a song
/// is never asked twice. When LRCLIB has none (or cannot be asked), a .lrc file with the song
/// file's name next to it is used (Sam: "local .lrc is used as fallback"; the song's file is
/// known for WACUP and other Winamp-style players). The log never gets a song's title, artist
/// or file. Never throws.
/// </summary>
internal sealed partial class LyricsService
{
    private const string SearchUrl = "https://lrclib.net/api/search";
    private const int Remembered = 64;
    private const double LengthSlackSeconds = 2;

    private static readonly HttpClient Http = MakeClient();

    private readonly Dictionary<string, SyncedLyrics?> _known = new(StringComparer.Ordinal);

    /// <summary>Searches under way, by song: a second asker waits for the same one.</summary>
    private readonly Dictionary<string, Task<SyncedLyrics?>> _asking = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private bool _failedOnce;

    /// <summary>
    /// The song's synced lyrics, or null when LRCLIB has none (or could not be asked). Up to
    /// three searches, each only when the one before found nothing (Sam: "is i possible for
    /// LRCLIB to give lyrics just from the song title/file name?", then "yes"): the title and
    /// artist; the title alone, without bracketed parts like "(Remastered)"; and, when the
    /// song's file is known, the words of its name as keywords (without a track number, the
    /// extension and bracketed parts). Every answer must match the song's length within 2 s.
    /// </summary>
    /// <remarks>
    /// <paramref name="cancel"/> stops only this asker's wait (the song changed); the search
    /// itself goes on (each answer within 10 s) and is remembered, for the other asker and the
    /// next time. UI thread only.
    /// </remarks>
    internal async Task<SyncedLyrics?> FindAsync(string key, string title, string artist, TimeSpan length, string? songFile, CancellationToken cancel)
    {
        if (_known.TryGetValue(key, out SyncedLyrics? known))
        {
            return known;
        }

        if (!_asking.TryGetValue(key, out Task<SyncedLyrics?>? asking))
        {
            asking = AskAsync(key, title, artist, length, songFile);
            if (!asking.IsCompleted)
            {
                _asking[key] = asking;
            }
        }

        try
        {
            return await asking.WaitAsync(cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The searches for one song (see <see cref="FindAsync"/>), not cancelled by any asker.</summary>
    private async Task<SyncedLyrics?> AskAsync(string key, string title, string artist, TimeSpan length, string? songFile)
    {
        try
        {
            return await SearchAllAsync(key, title, artist, length, songFile);
        }
        finally
        {
            _asking.Remove(key);
        }
    }

    private async Task<SyncedLyrics?> SearchAllAsync(string key, string title, string artist, TimeSpan length, string? songFile)
    {
        CancellationToken cancel = CancellationToken.None;

        var searches = new List<(string By, string Query)>();
        if (artist.Length > 0)
        {
            searches.Add(("title and artist", "track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist)));
        }

        string bareTitle = Clean(title);
        if (bareTitle.Length > 0)
        {
            searches.Add(("title", "track_name=" + Uri.EscapeDataString(bareTitle)));
        }

        string fileWords = songFile is null ? string.Empty : FileWords(songFile);
        if (fileWords.Length > 1)
        {
            searches.Add(("file name", "q=" + Uri.EscapeDataString(fileWords)));
        }

        SyncedLyrics? found = null;
        try
        {
            foreach ((string by, string query) in searches)
            {
                found = await SearchAsync(query, length, cancel);
                if (found is not null)
                {
                    Log.Info("Lyrics: " + found.Lines.Count.ToString(CultureInfo.InvariantCulture) + " lines found (by " + by + ").");
                    break;
                }
            }

            if (found is null)
            {
                Log.Info("Lyrics: none with synced lines for this song (" + searches.Count.ToString(CultureInfo.InvariantCulture) + " searches).");
            }
        }
        catch (Exception ex)
        {
            // Not remembered (a timeout included): asked again the next time the song starts.
            Note("LRCLIB could not be asked (" + ex.GetType().Name + ": " + ex.Message + ").");
            return null;
        }

        Remember(key, found);
        return found;
    }

    /// <summary>The synced lyrics in the .lrc file next to <paramref name="songFile"/> (same name), or null.</summary>
    internal static async Task<SyncedLyrics?> ReadLocalAsync(string songFile)
    {
        try
        {
            string lrc = Path.ChangeExtension(songFile, ".lrc");
            if (!File.Exists(lrc))
            {
                return null;
            }

            // UTF-8 unless the file says otherwise with its byte order mark.
            string text = await File.ReadAllTextAsync(lrc);
            SyncedLyrics? found = Parse(text);
            Log.Info("Lyrics: " + (found is null ? "a local .lrc file without timed lines." : found.Lines.Count.ToString(CultureInfo.InvariantCulture) + " lines from a local .lrc file."));
            return found;
        }
        catch (Exception ex)
        {
            Log.Warn("Lyrics: a local .lrc file could not be read (" + ex.GetType().Name + ").");
            return null;
        }
    }

    /// <summary>Lines like "[01:02.34] words" (a line may carry several times); tags such as [ar:...] are left out.</summary>
    internal static SyncedLyrics? Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (string raw in lrc.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            var times = new List<TimeSpan>();
            int at = 0;
            while (true)
            {
                Match match = TimeTag().Match(line, at);
                if (!match.Success || match.Index != at)
                {
                    break;
                }

                // ':' as the decimal mark ("[01:02:34]") is read as a dot.
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                    && double.TryParse(match.Groups[2].Value.Replace(':', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds))
                {
                    times.Add(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds));
                }

                at = match.Index + match.Length;
            }

            string words = line[at..].Trim();
            foreach (TimeSpan time in times)
            {
                lines.Add(new LyricLine(time, words));
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        lines.Sort((a, b) => a.At.CompareTo(b.At));
        return new SyncedLyrics(lines);
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex TimeTag();

    /// <summary>Bracketed parts: "(Remastered 2011)", "[Official Video]", "{Live}".</summary>
    [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Brackets();

    /// <summary>A track number at the start of a file name: "01 - ", "01. ", "1-01 ", "07 " (not "7 Rings" or "1979 - ").</summary>
    [GeneratedRegex(@"^\s*(?:\d{1,2}[-.]\d{1,3}\s+|\d{1,3}\s*[-._)\]]\s*|\d{2,3}\s+)")]
    private static partial Regex TrackNumber();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    /// <summary>One LRCLIB search; null when no answer has synced lines and the song's length.</summary>
    private static async Task<SyncedLyrics?> SearchAsync(string query, TimeSpan length, CancellationToken cancel)
    {
        using HttpResponseMessage response = await Http.GetAsync(SearchUrl + "?" + query, cancel);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancel);
        return Pick(document.RootElement, length);
    }

    /// <summary>Without bracketed parts and underscores, spaces tidied.</summary>
    private static string Clean(string text)
    {
        string cleaned = Brackets().Replace(text, " ").Replace('_', ' ');
        return Spaces().Replace(cleaned, " ").Trim(' ', '-', '.', ',');
    }

    /// <summary>A song file's name as search words: no folder, extension, track number or bracketed parts; dashes between parts become spaces.</summary>
    private static string FileWords(string file)
    {
        string name = TrackNumber().Replace(Path.GetFileNameWithoutExtension(file), string.Empty, 1);
        return Spaces().Replace(Clean(name).Replace(" - ", " ", StringComparison.Ordinal), " ").Trim();
    }

    /// <summary>The answer whose length is closest to the song's (within 2 s), with synced lines.</summary>
    private static SyncedLyrics? Pick(JsonElement root, TimeSpan length)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        double bestGap = double.MaxValue;
        foreach (JsonElement item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("syncedLyrics", out JsonElement synced) || synced.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? text = synced.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            double gap = double.MaxValue;
            if (item.TryGetProperty("duration", out JsonElement seconds) && seconds.ValueKind == JsonValueKind.Number)
            {
                gap = Math.Abs(seconds.GetDouble() - length.TotalSeconds);
            }

            if (gap <= LengthSlackSeconds && gap < bestGap)
            {
                best = text;
                bestGap = gap;
            }
        }

        return best is null ? null : Parse(best);
    }

    private static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        Version? version = typeof(LyricsService).Assembly.GetName().Version;
        string shown = version is null ? "0" : version.ToString(3);

        // LRCLIB asks apps to name themselves.
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("Machitan/" + shown + " (https://github.com/mujairkitten/machitan)");
        return client;
    }

    private void Remember(string key, SyncedLyrics? lyrics)
    {
        if (_known.ContainsKey(key))
        {
            return;
        }

        _known[key] = lyrics;
        _order.Enqueue(key);
        while (_order.Count > Remembered)
        {
            _known.Remove(_order.Dequeue());
        }
    }

    private void Note(string message)
    {
        if (!_failedOnce)
        {
            _failedOnce = true;
            Log.Warn("Lyrics: " + message);
        }
    }
}
