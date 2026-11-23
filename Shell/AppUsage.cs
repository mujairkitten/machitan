using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Machitan.Core;

namespace Machitan.Shell;

/// <summary>
/// Start's Most used list (Settings > Start, off by default): how many times each app was
/// opened from Start, by its app list key (its shortcut's path, or a store app's ID), in
/// app-usage.json under %AppData%\Machitan. Counted only while the list is on; turning it off
/// deletes the file. Nothing else is kept: no times, no files the apps opened.
/// Safety rule, as for the pins: a file that exists but cannot be read is never overwritten
/// (counting pauses for the run), except that turning the list off deletes it.
/// </summary>
internal sealed class AppUsage
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private bool _on;
    private bool _readFailed;

    private static string FilePath => Path.Combine(AppPaths.DataFolder, "app-usage.json");

    /// <summary>Raised when the counts changed (an app was opened, or they were deleted).</summary>
    internal event Action? Changed;

    /// <summary>Reads the counts when the list is on; deletes them when it is off. Never throws.</summary>
    internal void Load(bool on)
    {
        _on = on;
        _counts.Clear();
        _readFailed = false;
        if (!on)
        {
            DeleteFile();
            return;
        }

        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            if (JsonNode.Parse(File.ReadAllText(FilePath), null, DocumentOptions) is not JsonObject document
                || document["Apps"] is not JsonArray apps)
            {
                throw new JsonException("The app usage file does not have the expected shape.");
            }

            foreach (JsonNode? entry in apps)
            {
                if (entry is JsonObject item
                    && item["Key"] is JsonValue keyValue && keyValue.TryGetValue(out string? key) && !string.IsNullOrWhiteSpace(key)
                    && item["Count"] is JsonValue countValue && countValue.TryGetValue(out int count) && count > 0)
                {
                    _counts[key] = count;
                }
            }

            Log.Info("Most used: " + _counts.Count.ToString(CultureInfo.InvariantCulture) + " app(s) counted so far.");
        }
        catch (Exception ex)
        {
            _counts.Clear();
            _readFailed = true;
            Log.Error("Most used: app-usage.json could not be read. It is left untouched and nothing is counted this run.", ex);
        }
    }

    /// <summary>The setting changed: off deletes the counts at once; on starts counting from nothing.</summary>
    internal void SetOn(bool on)
    {
        if (on == _on)
        {
            return;
        }

        _on = on;
        _counts.Clear();
        if (on)
        {
            Log.Info("Most used: on; counting apps opened from Start.");
            return;
        }

        _readFailed = false;
        DeleteFile();
        Log.Info("Most used: off; the counts were deleted.");
        Changed?.Invoke();
    }

    /// <summary>An app (by its app list key) was opened from Start.</summary>
    internal void Note(string key)
    {
        if (!_on || _readFailed || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _counts.TryGetValue(key, out int count);
        _counts[key] = count == int.MaxValue ? count : count + 1;
        Save();
        Changed?.Invoke();
    }

    /// <summary>The keys of the apps opened most, most first (equal counts: by key), at most <paramref name="count"/>.</summary>
    internal List<string> Top(int count)
    {
        var keys = new List<string>(_counts.Keys);
        keys.Sort((a, b) =>
        {
            int byCount = _counts[b].CompareTo(_counts[a]);
            return byCount != 0 ? byCount : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        if (keys.Count > count)
        {
            keys.RemoveRange(count, keys.Count - count);
        }

        return keys;
    }

    private void Save()
    {
        try
        {
            var apps = new JsonArray();
            foreach (KeyValuePair<string, int> pair in _counts)
            {
                apps.Add(new JsonObject { ["Key"] = pair.Key, ["Count"] = pair.Value });
            }

            AtomicFile.WriteAllText(FilePath, new JsonObject { ["Apps"] = apps }.ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Most used: app-usage.json could not be saved.", ex);
        }
    }

    private static void DeleteFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Most used: app-usage.json could not be deleted.", ex);
        }
    }
}
