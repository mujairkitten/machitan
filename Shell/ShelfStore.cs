using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Machitan.Core;

namespace Machitan.Shell;

/// <summary>What a drop on the shelf did: how many were parked, and how many the full shelf turned away.</summary>
internal readonly record struct ShelfAdded(int Added, int Full);

/// <summary>
/// Dynaland's file shelf: files and folders parked for later, newest first, in shelf.json
/// under %AppData%\Machitan (next to pins.json). Only where they are is kept (their paths):
/// nothing is copied, and a file that was moved or deleted since stays listed until it is
/// removed, so the shelf never pretends. At most <see cref="MaxItems"/>.
/// Safety rule (as the pins): if the file exists but cannot be read, this store refuses to
/// write for the rest of the run. An unreadable file is never replaced by an empty shelf.
/// </summary>
internal sealed class ShelfStore
{
    /// <summary>How many the shelf holds.</summary>
    internal const int MaxItems = 50;

    private const string Label = "Shelf";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly List<string> _paths = new();
    private readonly string _file;
    private bool _readFailed;

    internal ShelfStore(string file)
    {
        _file = file;
    }

    /// <summary>Raised when the shelf changed.</summary>
    internal event Action? Changed;

    /// <summary>The parked files and folders, newest first.</summary>
    internal IReadOnlyList<string> Paths => _paths;

    /// <summary>True when the file on disk could not be read and saving is blocked.</summary>
    internal bool IsReadOnlyBecauseOfBadFile => _readFailed;

    /// <summary>Reads the file. Never throws.</summary>
    internal void Load()
    {
        _paths.Clear();
        _readFailed = false;
        try
        {
            if (!File.Exists(_file))
            {
                Log.Info(Label + ": no file yet; the shelf is empty.");
                return;
            }

            string text = File.ReadAllText(_file);
            if (JsonNode.Parse(text, NodeOptions, DocumentOptions) is not JsonObject document
                || document["Shelf"] is not JsonArray entries)
            {
                throw new JsonException("The shelf file does not have the expected shape.");
            }

            foreach (JsonNode? entry in entries)
            {
                if (entry is not JsonObject item
                    || item["Path"] is not JsonValue value
                    || !value.TryGetValue(out string? path)
                    || string.IsNullOrWhiteSpace(path))
                {
                    throw new JsonException("A shelf entry has no path.");
                }

                if (IndexOf(path) < 0 && _paths.Count < MaxItems)
                {
                    _paths.Add(path);
                }
            }

            Log.Info(Label + ": " + _paths.Count.ToString(CultureInfo.InvariantCulture) + " item(s) loaded.");
        }
        catch (Exception ex)
        {
            _paths.Clear();
            _readFailed = true;
            Log.Error(Label + ": the file could not be read. It is left untouched and shelf changes will not be saved this run.", ex);
            KeepCopyOfUnreadableFile();
        }
    }

    /// <summary>
    /// Parks files and folders, the first of them on top. One already on the shelf moves to
    /// the top (it is not new). New ones past <see cref="MaxItems"/> are turned away.
    /// </summary>
    internal ShelfAdded Add(IReadOnlyList<string> paths)
    {
        int added = 0;
        int full = 0;
        bool changed = false;

        // Backwards, each to the top: the first dropped ends up first.
        for (int i = paths.Count - 1; i >= 0; i--)
        {
            string path = paths[i];
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            int at = IndexOf(path);
            if (at >= 0)
            {
                if (at > 0)
                {
                    _paths.RemoveAt(at);
                    _paths.Insert(0, path);
                    changed = true;
                }

                continue;
            }

            if (_paths.Count >= MaxItems)
            {
                full++;
                continue;
            }

            _paths.Insert(0, path);
            added++;
            changed = true;
        }

        if (changed)
        {
            Save();
            Changed?.Invoke();
        }

        return new ShelfAdded(added, full);
    }

    /// <summary>Takes one off the shelf (the file itself is untouched). Returns false when it was not there.</summary>
    internal bool Remove(string path)
    {
        int index = IndexOf(path);
        if (index < 0)
        {
            return false;
        }

        _paths.RemoveAt(index);
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Takes several off the shelf at once (the files are untouched). Returns how many were there.</summary>
    internal int RemoveMany(IEnumerable<string> paths)
    {
        int removed = 0;
        foreach (string path in paths)
        {
            int index = IndexOf(path);
            if (index >= 0)
            {
                _paths.RemoveAt(index);
                removed++;
            }
        }

        if (removed > 0)
        {
            Save();
            Changed?.Invoke();
        }

        return removed;
    }

    private int IndexOf(string path)
    {
        for (int i = 0; i < _paths.Count; i++)
        {
            if (string.Equals(_paths[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void Save()
    {
        if (_readFailed)
        {
            Log.Warn(Label + ": not saved, because the file on disk could not be read at start and must not be overwritten.");
            return;
        }

        try
        {
            var entries = new JsonArray();
            foreach (string path in _paths)
            {
                entries.Add(new JsonObject { ["Path"] = path });
            }

            var document = new JsonObject { ["Shelf"] = entries };
            AtomicFile.WriteAllText(_file, document.ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            Log.Error(Label + ": the file could not be saved.", ex);
        }
    }

    private void KeepCopyOfUnreadableFile()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string copy = Path.Combine(
                AppPaths.DataFolder,
                Path.GetFileNameWithoutExtension(_file) + ".unreadable-" + stamp + ".json");
            File.Copy(_file, copy, overwrite: true);
            Log.Info(Label + ": a copy of the unreadable file was kept as " + Path.GetFileName(copy) + ".");
        }
        catch (Exception ex)
        {
            Log.Warn(Label + ": could not keep a copy of the unreadable file.", ex);
        }
    }
}
