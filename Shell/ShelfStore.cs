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

/// <summary>What a shelf item is: a file or folder (its path), a web link, or a piece of text.</summary>
internal enum ShelfItemKind
{
    File,
    Link,
    Note,
}

/// <summary>One thing on the shelf.</summary>
internal sealed class ShelfItem
{
    internal ShelfItem(ShelfItemKind kind, string key, string value, string title)
    {
        Kind = kind;
        Key = key;
        Value = value;
        Title = title;
    }

    internal ShelfItemKind Kind { get; }

    /// <summary>Unique on the shelf: a file's path itself, "link:" and the address, or "note:" and its id.</summary>
    internal string Key { get; }

    /// <summary>The path, the address (http or https), or the text.</summary>
    internal string Value { get; }

    /// <summary>A link's page title when the drop or the clipboard gave one; else empty.</summary>
    internal string Title { get; }
}

/// <summary>
/// Dynaland's shelf: files and folders, web links and pieces of text parked for later, newest
/// first, in shelf.json under %AppData%\Machitan (next to pins.json). For files and folders
/// only where they are is kept (their paths): nothing is copied, and one that was moved or
/// deleted since stays listed until it is removed, so the shelf never pretends. Links (http
/// and https only) and text are kept as they are (Sam, 2026-11-26: "Ability to save links and
/// notes in the Shelf"). At most <see cref="MaxItems"/> in all. The log never gets a link,
/// a text or a title, only how many.
/// Safety rule (as the pins): if the file exists but cannot be read, this store refuses to
/// write for the rest of the run. An unreadable file is never replaced by an empty shelf.
/// </summary>
internal sealed class ShelfStore
{
    /// <summary>How many the shelf holds.</summary>
    internal const int MaxItems = 50;

    /// <summary>The longest text kept (characters); longer text is cut there.</summary>
    internal const int MaxNoteLength = 20000;

    /// <summary>The longest address and page title kept (characters); a longer address is not taken.</summary>
    internal const int MaxLinkLength = 4096;
    internal const int MaxTitleLength = 300;

    private const string Label = "Shelf";
    private const string LinkPrefix = "link:";
    private const string NotePrefix = "note:";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly List<ShelfItem> _items = new();
    private readonly string _file;
    private bool _readFailed;

    internal ShelfStore(string file)
    {
        _file = file;
    }

    /// <summary>Raised when the shelf changed.</summary>
    internal event Action? Changed;

    /// <summary>What is parked, newest first.</summary>
    internal IReadOnlyList<ShelfItem> Items => _items;

    /// <summary>True when the file on disk could not be read and saving is blocked.</summary>
    internal bool IsReadOnlyBecauseOfBadFile => _readFailed;

    /// <summary>
    /// True when <paramref name="text"/> is one web address and nothing else (spaces around it
    /// aside): an absolute http or https address, at most <see cref="MaxLinkLength"/> long.
    /// </summary>
    internal static bool TryLink(string? text, out string address)
    {
        address = string.Empty;
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLinkLength)
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        address = trimmed;
        return true;
    }

    /// <summary>Reads the file. Never throws.</summary>
    internal void Load()
    {
        _items.Clear();
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
                ShelfItem item = ReadEntry(entry);
                if (IndexOf(item.Key) < 0 && _items.Count < MaxItems)
                {
                    _items.Add(item);
                }
            }

            Log.Info(Label + ": " + _items.Count.ToString(CultureInfo.InvariantCulture) + " item(s) loaded ("
                + Count(ShelfItemKind.Link).ToString(CultureInfo.InvariantCulture) + " link(s), "
                + Count(ShelfItemKind.Note).ToString(CultureInfo.InvariantCulture) + " note(s)).");
        }
        catch (Exception ex)
        {
            _items.Clear();
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
        var items = new List<ShelfItem>();
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                items.Add(new ShelfItem(ShelfItemKind.File, path, path, string.Empty));
            }
        }

        return AddItems(items);
    }

    /// <summary>Parks a web link (see <see cref="TryLink"/>), with its page title when there is one. The same address again moves to the top.</summary>
    internal ShelfAdded AddLink(string address, string title)
    {
        if (!TryLink(address, out string kept))
        {
            return new ShelfAdded(0, 0);
        }

        string cleanTitle = OneLine(title);
        if (cleanTitle.Length > MaxTitleLength)
        {
            cleanTitle = cleanTitle[..MaxTitleLength];
        }

        return AddItems(new[] { new ShelfItem(ShelfItemKind.Link, LinkPrefix + kept, kept, cleanTitle) });
    }

    /// <summary>
    /// Parks a piece of text (cut at <see cref="MaxNoteLength"/>). The same text again moves to
    /// the top. Text that is only spaces is not taken.
    /// </summary>
    internal ShelfAdded AddNote(string text)
    {
        string kept = (text ?? string.Empty).TrimEnd();
        if (kept.Trim().Length == 0)
        {
            return new ShelfAdded(0, 0);
        }

        if (kept.Length > MaxNoteLength)
        {
            kept = kept[..MaxNoteLength];
        }

        foreach (ShelfItem existing in _items)
        {
            if (existing.Kind == ShelfItemKind.Note && string.Equals(existing.Value, kept, StringComparison.Ordinal))
            {
                return AddItems(new[] { existing });
            }
        }

        string id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return AddItems(new[] { new ShelfItem(ShelfItemKind.Note, NotePrefix + id, kept, string.Empty) });
    }

    /// <summary>The item with this key, or null.</summary>
    internal ShelfItem? Get(string key)
    {
        int index = IndexOf(key);
        return index >= 0 ? _items[index] : null;
    }

    /// <summary>Takes one off the shelf (a file itself is untouched). Returns false when it was not there.</summary>
    internal bool Remove(string key)
    {
        int index = IndexOf(key);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Takes several off the shelf at once (files themselves are untouched). Returns how many were there.</summary>
    internal int RemoveMany(IEnumerable<string> keys)
    {
        int removed = 0;
        foreach (string key in keys)
        {
            int index = IndexOf(key);
            if (index >= 0)
            {
                _items.RemoveAt(index);
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

    private static ShelfItem ReadEntry(JsonNode? entry)
    {
        if (entry is not JsonObject item)
        {
            throw new JsonException("A shelf entry is not an object.");
        }

        if (TryText(item["Path"], out string path))
        {
            return new ShelfItem(ShelfItemKind.File, path, path, string.Empty);
        }

        if (TryText(item["Link"], out string link))
        {
            if (!TryLink(link, out string address))
            {
                throw new JsonException("A shelf link is not an http or https address.");
            }

            TryText(item["Title"], out string title);
            return new ShelfItem(ShelfItemKind.Link, LinkPrefix + address, address, OneLine(title));
        }

        if (TryText(item["Note"], out string note))
        {
            if (!TryText(item["Id"], out string id))
            {
                id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            }

            return new ShelfItem(ShelfItemKind.Note, NotePrefix + id, note.Length > MaxNoteLength ? note[..MaxNoteLength] : note, string.Empty);
        }

        throw new JsonException("A shelf entry is not a file, a link or a note.");
    }

    private static bool TryText(JsonNode? node, out string text)
    {
        text = string.Empty;
        if (node is JsonValue value && value.TryGetValue(out string? found) && !string.IsNullOrWhiteSpace(found))
        {
            text = found;
            return true;
        }

        return false;
    }

    /// <summary>A title on one line: line breaks and runs of spaces become one space.</summary>
    private static string OneLine(string? text)
    {
        var line = new System.Text.StringBuilder();
        bool space = false;
        foreach (char c in text ?? string.Empty)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = line.Length > 0;
                continue;
            }

            if (space)
            {
                line.Append(' ');
                space = false;
            }

            line.Append(c);
        }

        return line.ToString();
    }

    /// <summary>Puts items on top, the first of them first; ones already there move up, new ones past the limit are turned away.</summary>
    private ShelfAdded AddItems(IReadOnlyList<ShelfItem> items)
    {
        int added = 0;
        int full = 0;
        bool changed = false;

        // Backwards, each to the top: the first given ends up first.
        for (int i = items.Count - 1; i >= 0; i--)
        {
            ShelfItem item = items[i];
            int at = IndexOf(item.Key);
            if (at >= 0)
            {
                if (at > 0)
                {
                    ShelfItem kept = _items[at];
                    _items.RemoveAt(at);
                    _items.Insert(0, kept);
                    changed = true;
                }

                continue;
            }

            if (_items.Count >= MaxItems)
            {
                full++;
                continue;
            }

            _items.Insert(0, item);
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

    private int Count(ShelfItemKind kind)
    {
        int count = 0;
        foreach (ShelfItem item in _items)
        {
            if (item.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    private int IndexOf(string key)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Key, key, StringComparison.OrdinalIgnoreCase))
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
            foreach (ShelfItem item in _items)
            {
                switch (item.Kind)
                {
                    case ShelfItemKind.Link:
                        var link = new JsonObject { ["Link"] = item.Value };
                        if (item.Title.Length > 0)
                        {
                            link["Title"] = item.Title;
                        }

                        entries.Add(link);
                        break;
                    case ShelfItemKind.Note:
                        entries.Add(new JsonObject { ["Note"] = item.Value, ["Id"] = item.Key[NotePrefix.Length..] });
                        break;
                    default:
                        entries.Add(new JsonObject { ["Path"] = item.Value });
                        break;
                }
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
