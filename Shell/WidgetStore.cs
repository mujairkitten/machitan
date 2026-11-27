using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Machitan.Core;

namespace Machitan.Shell;

/// <summary>The kinds of widget Machitan has. A kind this version does not know (from a later one) is kept in the file, untouched, and not shown.</summary>
internal static class WidgetKinds
{
    /// <summary>A text note (v0.9.3).</summary>
    internal const string Note = "Note";

    /// <summary>The time and today's date, with a month calendar (v0.9.3).</summary>
    internal const string ClockCalendar = "ClockCalendar";

    /// <summary>The time and today's date only (v0.9.3).</summary>
    internal const string Clock = "Clock";

    /// <summary>The weather now, as the info line has it (v0.9.3).</summary>
    internal const string Weather = "Weather";

    /// <summary>What is playing, with its controls (v0.9.3).</summary>
    internal const string NowPlaying = "NowPlaying";

    /// <summary>The kinds this version can show, in the Add menu's order (Sam's list's order).</summary>
    internal static readonly string[] Known = { ClockCalendar, Clock, Weather, NowPlaying, Note };

    internal static bool IsKnown(string kind) => Array.IndexOf(Known, kind) >= 0;
}

/// <summary>Where a widget is.</summary>
internal static class WidgetPlaces
{
    /// <summary>In the sidebar, in the list's order.</summary>
    internal const string Sidebar = "Sidebar";

    /// <summary>On the desktop, behind the windows, at its own spot.</summary>
    internal const string Desktop = "Desktop";
}

/// <summary>One widget the user added: its kind, where it is, and its own data (a note's text and the like).</summary>
internal sealed class WidgetItem
{
    internal WidgetItem(string id, string kind)
    {
        Id = id;
        Kind = kind;
    }

    /// <summary>Unique within widgets.json.</summary>
    internal string Id { get; }

    internal string Kind { get; }

    /// <summary>WidgetPlaces.Sidebar (the default) or WidgetPlaces.Desktop.</summary>
    internal string Place { get; set; } = WidgetPlaces.Sidebar;

    /// <summary>On the desktop: its window's top-left corner (physical pixels, the whole desktop's coordinates); null until placed.</summary>
    internal int? X { get; set; }

    internal int? Y { get; set; }

    /// <summary>On the desktop: its card's width (DIP); null: the standard width. Kept in the sidebar too, for when it goes back.</summary>
    internal int? Width { get; set; }

    /// <summary>Its content's height (DIP), on the desktop and in the sidebar; null: the standard height (in the sidebar, as tall as its content).</summary>
    internal int? Height { get; set; }

    internal bool OnDesktop => Place == WidgetPlaces.Desktop;

    /// <summary>Its own values by name (all text), e.g. a note's "Text".</summary>
    internal Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);

    /// <summary>A kind this version does not know: written back exactly as it was read.</summary>
    internal JsonObject? Unknown { get; init; }

    internal string Get(string key) => Data.TryGetValue(key, out string? value) ? value : string.Empty;
}

/// <summary>
/// The widgets added to the sidebar, in its order, in widgets.json under %AppData%\Machitan
/// (next to shelf.json), with each widget's own data. Nothing is added by itself: every widget
/// is one the user added. Safety rule (as the pins and the shelf): if the file exists but cannot
/// be read, this store refuses to write for the rest of the run and keeps a copy of the file, so
/// an unreadable file is never replaced. At most <see cref="MaxItems"/>. UI thread only.
/// </summary>
internal sealed class WidgetStore
{
    /// <summary>How many widgets there can be.</summary>
    internal const int MaxItems = 30;

    /// <summary>The sizes a widget can be dragged to (DIP): a desktop card's width, and a widget's content height.</summary>
    internal const int MinWidth = 200;
    internal const int MaxWidth = 1000;
    internal const int MinHeight = 60;
    internal const int MaxHeight = 1000;

    /// <summary>The longest text a widget keeps for one value (a note), in characters.</summary>
    internal const int MaxValueLength = 20000;

    private const string Label = "Widgets";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly List<WidgetItem> _items = new();
    private readonly string _file;
    private bool _readFailed;

    internal WidgetStore(string file)
    {
        _file = file;
    }

    /// <summary>Raised when widgets were added, removed or moved (not when a widget's own data changed).</summary>
    internal event Action? Changed;

    /// <summary>Every widget in the file, in the sidebar's order (unknown kinds included).</summary>
    internal IReadOnlyList<WidgetItem> Items => _items;

    /// <summary>True when the file on disk could not be read and saving is blocked.</summary>
    internal bool IsReadOnlyBecauseOfBadFile => _readFailed;

    /// <summary>True when no more widgets can be added.</summary>
    internal bool IsFull => _items.Count >= MaxItems;

    /// <summary>Reads the file. Never throws.</summary>
    internal void Load()
    {
        _items.Clear();
        _readFailed = false;
        try
        {
            if (!File.Exists(_file))
            {
                Log.Info(Label + ": no file yet; no widgets.");
                return;
            }

            string text = File.ReadAllText(_file);
            if (JsonNode.Parse(text, NodeOptions, DocumentOptions) is not JsonObject document
                || document["Widgets"] is not JsonArray entries)
            {
                throw new JsonException("The widgets file does not have the expected shape.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? entry in entries)
            {
                if (entry is not JsonObject item
                    || !TryText(item["Id"], out string id)
                    || !TryText(item["Kind"], out string kind))
                {
                    throw new JsonException("A widget has no id or kind.");
                }

                if (!ids.Add(id) || _items.Count >= MaxItems)
                {
                    continue;
                }

                if (!WidgetKinds.IsKnown(kind))
                {
                    _items.Add(new WidgetItem(id, kind) { Unknown = (JsonObject)item.DeepClone() });
                    continue;
                }

                var widget = new WidgetItem(id, kind);
                if (TryText(item["Place"], out string place) && place == WidgetPlaces.Desktop)
                {
                    widget.Place = WidgetPlaces.Desktop;
                    widget.X = TryNumber(item["X"]);
                    widget.Y = TryNumber(item["Y"]);
                }

                widget.Width = TryNumber(item["Width"]) is int width ? Math.Clamp(width, MinWidth, MaxWidth) : null;
                widget.Height = TryNumber(item["Height"]) is int height ? Math.Clamp(height, MinHeight, MaxHeight) : null;

                if (item["Data"] is JsonObject data)
                {
                    foreach (KeyValuePair<string, JsonNode?> pair in data)
                    {
                        if (TryText(pair.Value, out string value, allowEmpty: true))
                        {
                            widget.Data[pair.Key] = Cut(value);
                        }
                    }
                }

                _items.Add(widget);
            }

            Log.Info(Label + ": " + _items.Count.ToString(CultureInfo.InvariantCulture) + " widget(s) loaded.");
        }
        catch (Exception ex)
        {
            _items.Clear();
            _readFailed = true;
            Log.Error(Label + ": the file could not be read. It is left untouched and widget changes will not be saved this run.", ex);
            KeepCopyOfUnreadableFile();
        }
    }

    /// <summary>Adds a widget at the end of the sidebar. Null when full or the kind is unknown.</summary>
    internal WidgetItem? Add(string kind)
    {
        if (IsFull || !WidgetKinds.IsKnown(kind))
        {
            return null;
        }

        var item = new WidgetItem(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), kind);
        _items.Add(item);
        Save();
        Log.Info(Label + ": a widget (" + kind + ") was added.");
        Changed?.Invoke();
        return item;
    }

    /// <summary>Removes a widget and its data. False when it was not there.</summary>
    internal bool Remove(string id)
    {
        int index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        string kind = _items[index].Kind;
        _items.RemoveAt(index);
        Save();
        Log.Info(Label + ": a widget (" + kind + ") was removed.");
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Puts a widget on the desktop or in the sidebar. On the desktop: at <paramref name="x"/>,
    /// <paramref name="y"/> (physical pixels; where it was dropped), or with neither its spot is
    /// found when it is shown. In the sidebar: just before <paramref name="before"/> (where it
    /// was dropped), at the end with <paramref name="atEnd"/>, or else at its old place in the list.
    /// </summary>
    internal void SetPlace(string id, bool desktop, int? x = null, int? y = null, string? before = null, bool atEnd = false)
    {
        int index = IndexOf(id);
        if (index < 0)
        {
            return;
        }

        WidgetItem item = _items[index];
        string place = desktop ? WidgetPlaces.Desktop : WidgetPlaces.Sidebar;
        if (item.Place == place)
        {
            return;
        }

        item.Place = place;
        item.X = desktop ? x : null;
        item.Y = desktop ? y : null;
        if (!desktop)
        {
            int at = before is null ? (atEnd ? _items.Count : -1) : IndexOf(before);
            if (at >= 0 && at != index)
            {
                _items.RemoveAt(index);
                _items.Insert(at > index ? at - 1 : at, item);
            }
        }

        Save();
        Log.Info(Label + ": a widget (" + item.Kind + ") was moved to the " + (desktop ? "desktop." : "sidebar."));
        Changed?.Invoke();
    }

    /// <summary>Two sidebar widgets change places (one dragged onto the other). False when either is not there.</summary>
    internal bool Swap(string a, string b)
    {
        int first = IndexOf(a);
        int second = IndexOf(b);
        if (first < 0 || second < 0 || first == second)
        {
            return false;
        }

        (_items[first], _items[second]) = (_items[second], _items[first]);
        Save();
        Log.Info(Label + ": two widgets changed places.");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Keeps where a desktop widget is (Changed is not raised: nothing else moved).</summary>
    internal void SetPosition(string id, int x, int y)
    {
        int index = IndexOf(id);
        if (index < 0)
        {
            return;
        }

        WidgetItem item = _items[index];
        if (item.X == x && item.Y == y)
        {
            return;
        }

        item.X = x;
        item.Y = y;
        Save();
    }

    /// <summary>
    /// Keeps a widget's size (DIP; null: the standard one), clamped to the limits. Changed is not
    /// raised: the widget was resized where it is.
    /// </summary>
    internal void SetSize(string id, int? width, int? height)
    {
        int index = IndexOf(id);
        if (index < 0)
        {
            return;
        }

        WidgetItem item = _items[index];
        int? w = width is int wide ? Math.Clamp(wide, MinWidth, MaxWidth) : null;
        int? h = height is int tall ? Math.Clamp(tall, MinHeight, MaxHeight) : null;
        if (item.Width == w && item.Height == h)
        {
            return;
        }

        item.Width = w;
        item.Height = h;
        Save();
    }

    /// <summary>Moves a widget up (negative) or down (positive) among the sidebar's; false when it cannot move that way.</summary>
    internal bool Move(string id, int step)
    {
        int index = IndexOf(id);
        if (index < 0 || step == 0)
        {
            return false;
        }

        // The next known widget in that direction (unknown kinds are not shown, so they are stepped over).
        int other = index;
        do
        {
            other += Math.Sign(step);
        }
        while (other >= 0 && other < _items.Count && !InSidebar(_items[other]));

        if (other < 0 || other >= _items.Count)
        {
            return false;
        }

        (_items[index], _items[other]) = (_items[other], _items[index]);
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>True when the widget can move one place that way among the known ones.</summary>
    internal bool CanMove(string id, int step)
    {
        int index = IndexOf(id);
        if (index < 0 || step == 0)
        {
            return false;
        }

        for (int other = index + Math.Sign(step); other >= 0 && other < _items.Count; other += Math.Sign(step))
        {
            if (InSidebar(_items[other]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Keeps one value of a widget's own data and saves (Changed is not raised: nothing moved).</summary>
    internal void SetData(string id, string key, string value)
    {
        int index = IndexOf(id);
        if (index < 0)
        {
            return;
        }

        string kept = Cut(value);
        WidgetItem item = _items[index];
        if (item.Data.TryGetValue(key, out string? old) && old == kept)
        {
            return;
        }

        item.Data[key] = kept;
        Save();
    }

    /// <summary>Shown in the sidebar: a kind this version has, placed in the sidebar.</summary>
    private static bool InSidebar(WidgetItem item)
    {
        return WidgetKinds.IsKnown(item.Kind) && !item.OnDesktop;
    }

    private static int? TryNumber(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out int number) ? number : (int?)null;
    }

    private static string Cut(string value)
    {
        return value.Length > MaxValueLength ? value[..MaxValueLength] : value;
    }

    private static bool TryText(JsonNode? node, out string text, bool allowEmpty = false)
    {
        text = string.Empty;
        if (node is JsonValue value && value.TryGetValue(out string? found) && found is not null && (allowEmpty || found.Length > 0))
        {
            text = found;
            return true;
        }

        return false;
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Id, id, StringComparison.Ordinal))
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
            foreach (WidgetItem item in _items)
            {
                if (item.Unknown is JsonObject unknown)
                {
                    entries.Add(unknown.DeepClone());
                    continue;
                }

                var data = new JsonObject();
                foreach (KeyValuePair<string, string> pair in item.Data)
                {
                    data[pair.Key] = pair.Value;
                }

                var entry = new JsonObject
                {
                    ["Id"] = item.Id,
                    ["Kind"] = item.Kind,
                    ["Place"] = item.Place,
                };
                if (item.OnDesktop && item.X is int x && item.Y is int y)
                {
                    entry["X"] = x;
                    entry["Y"] = y;
                }

                if (item.Width is int width)
                {
                    entry["Width"] = width;
                }

                if (item.Height is int height)
                {
                    entry["Height"] = height;
                }

                entry["Data"] = data;
                entries.Add(entry);
            }

            var document = new JsonObject { ["Widgets"] = entries };
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
