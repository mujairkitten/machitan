using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Machitan.Core;

namespace Machitan.Shell;

/// <summary>
/// How the bar is arranged, beyond the settings: the order of its bubbles from the left
/// (Start, the apps, and app groups made with "Group apps"), and which apps sit in which
/// bubble, in what order. Kept in %AppData%\Machitan\bar-layout.json.
///
/// Apps are named by their pin (when pinned) or their running app's key (its executable
/// path); either one finds an app's place. An app with no place sits in the apps bubble,
/// after the placed ones: pinned apps in pin order, then running apps in the order they
/// opened. A group whose apps are all closed and unpinned keeps its place and shows again
/// when one of them opens.
/// Safety rule (as for pins): an unreadable file is never overwritten in that run.
/// </summary>
internal sealed class BarLayoutStore
{
    internal const string StartId = "start";
    internal const string AppsId = "apps";

    /// <summary>The tray's bubble (shown only with Settings' "tray on the bar").</summary>
    internal const string TrayId = "tray";
    private const string GroupPrefix = "group-";
    private const int MaxKeysPerBubble = 300;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly List<string> _order = new();
    private readonly Dictionary<string, List<string>> _bubbles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tray icons' order, by their lasting names (in Dynaland and on the bar alike).</summary>
    private readonly List<string> _tray = new();

    /// <summary>
    /// Tray icons taken out of the tray box onto the bar itself (their lasting names). The others
    /// sit in the box behind the bar's ^ button, as on Windows' taskbar; a new icon starts there.
    /// </summary>
    private readonly List<string> _trayOut = new();
    private bool _readFailed;

    internal BarLayoutStore()
    {
        ResetToDefault();
    }

    /// <summary>Raised when the order or a bubble changed.</summary>
    internal event Action? Changed;

    /// <summary>Bubble IDs from the left: always Start, the apps and the tray, and any groups.</summary>
    internal IReadOnlyList<string> Order => _order;

    internal static bool IsGroup(string id)
    {
        return id.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name an app is kept under: its pin, else its running app's key.</summary>
    internal static string KeyOf(DockItem item)
    {
        return item.PinKey ?? item.Key;
    }

    /// <summary>Reads the file. Never throws.</summary>
    internal void Load()
    {
        ResetToDefault();
        _readFailed = false;

        try
        {
            if (!File.Exists(AppPaths.BarLayoutFile))
            {
                Log.Info("Bar layout: no file yet; Start, then the apps.");
                return;
            }

            ParseInto(File.ReadAllText(AppPaths.BarLayoutFile));
            Log.Info("Bar layout: " + _order.Count.ToString(CultureInfo.InvariantCulture) + " bubble(s): " + string.Join(", ", _order) + ".");
        }
        catch (Exception ex)
        {
            ResetToDefault();
            _readFailed = true;
            Log.Error("Bar layout: the file could not be read. It is left untouched; the bar uses the standard order and changes will not be saved this run.", ex);
        }
    }

    /// <summary>The whole layout as text, to put back later (custom mode's Back).</summary>
    internal string Snapshot()
    {
        return BuildDocument().ToJsonString(WriteOptions);
    }

    /// <summary>Puts back a layout taken with <see cref="Snapshot"/>.</summary>
    internal void Restore(string snapshot)
    {
        try
        {
            ParseInto(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("Bar layout: the earlier layout could not be put back.", ex);
            return;
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>Start first, then the apps, then the groups in their order (custom mode's Auto-arrange).</summary>
    internal void StandardOrder()
    {
        var order = new List<string> { StartId, AppsId };
        foreach (string id in _order)
        {
            if (IsGroup(id))
            {
                order.Add(id);
            }
        }

        order.Add(TrayId);

        if (SameOrder(order, _order))
        {
            return;
        }

        _order.Clear();
        _order.AddRange(order);
        Save();
        Changed?.Invoke();
    }

    /// <summary>No groups and no places: Start, then the apps in their standard order (custom mode's Reset).</summary>
    internal void ResetAll()
    {
        ResetToDefault();
        Save();
        Changed?.Invoke();
    }

    /// <summary>The tray icons in their kept order: placed ones first, then new ones as they came.</summary>
    internal List<TrayIcon> ArrangeTray(IReadOnlyList<TrayIcon> icons)
    {
        var ranked = new List<(int Rank, int Seen, TrayIcon Icon)>(icons.Count);
        for (int i = 0; i < icons.Count; i++)
        {
            int rank = IndexOf(_tray, icons[i].Stable);
            ranked.Add((rank < 0 ? int.MaxValue : rank, i, icons[i]));
        }

        ranked.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Seen.CompareTo(b.Seen));
        var result = new List<TrayIcon>(icons.Count);
        foreach ((int Rank, int Seen, TrayIcon Icon) entry in ranked)
        {
            result.Add(entry.Icon);
        }

        return result;
    }

    /// <summary>True when the icon sits on the bar itself, outside the tray box.</summary>
    internal bool IsTrayIconOut(TrayIcon icon)
    {
        return Contains(_trayOut, icon.Stable);
    }

    /// <summary>Puts a tray icon back into the tray box.</summary>
    internal void PutTrayIconInBox(TrayIcon icon)
    {
        if (RemoveKey(_trayOut, icon.Stable) == 0)
        {
            return;
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts a tray icon at <paramref name="index"/> among <paramref name="shown"/> (the icons as
    /// shown now, in order) without the icon itself; <paramref name="takeOut"/> also takes it
    /// out of the tray box onto the bar.
    /// </summary>
    internal void PlaceTrayIcon(TrayIcon icon, int index, IReadOnlyList<TrayIcon> shown, bool takeOut = false)
    {
        if (takeOut && !Contains(_trayOut, icon.Stable))
        {
            _trayOut.Add(icon.Stable);
            Trim(_trayOut);
        }

        var order = new List<string>();
        foreach (TrayIcon other in shown)
        {
            if (!string.Equals(other.Stable, icon.Stable, StringComparison.OrdinalIgnoreCase) && !Contains(order, other.Stable))
            {
                order.Add(other.Stable);
            }
        }

        order.Insert(Math.Clamp(index, 0, order.Count), icon.Stable);
        foreach (string key in _tray)
        {
            if (!Contains(order, key))
            {
                order.Add(key);
            }
        }

        Trim(order);
        _tray.Clear();
        _tray.AddRange(order);
        Save();
        Changed?.Invoke();
    }

    /// <summary>The apps of one bubble, in its order.</summary>
    internal IReadOnlyList<string> KeysOf(string bubbleId)
    {
        return _bubbles.TryGetValue(bubbleId, out List<string>? keys) ? keys : (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>Which bubble an app sits in (the apps bubble when it has no place yet).</summary>
    internal string BubbleOf(DockItem item)
    {
        foreach (string id in _order)
        {
            if (id != StartId && id != TrayId && _bubbles.TryGetValue(id, out List<string>? keys) && Find(keys, item) >= 0)
            {
                return id;
            }
        }

        return AppsId;
    }

    /// <summary>
    /// Sorts the dock's apps into bubbles: each bubble's placed apps in its order, then the
    /// ones with no place yet in the order they came (pins first, then running apps).
    /// Every bubble in <see cref="Order"/> except Start is in the result, empty or not.
    /// </summary>
    internal Dictionary<string, List<DockItem>> Arrange(IReadOnlyList<DockItem> items)
    {
        var result = new Dictionary<string, List<DockItem>>(StringComparer.OrdinalIgnoreCase);
        var placed = new Dictionary<string, List<(int Rank, int Seen, DockItem Item)>>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in _order)
        {
            if (id != StartId && id != TrayId)
            {
                result[id] = new List<DockItem>();
                placed[id] = new List<(int, int, DockItem)>();
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            DockItem item = items[i];
            string bubble = BubbleOf(item);
            int rank = Find(KeysOf(bubble), item);
            placed[bubble].Add((rank < 0 ? int.MaxValue : rank, i, item));
        }

        foreach ((string id, List<(int Rank, int Seen, DockItem Item)> list) in placed)
        {
            list.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Seen.CompareTo(b.Seen));
            foreach ((int Rank, int Seen, DockItem Item) entry in list)
            {
                result[id].Add(entry.Item);
            }
        }

        return result;
    }

    /// <summary>
    /// Puts an app into <paramref name="bubbleId"/> at <paramref name="index"/>, counted among
    /// <paramref name="shownThere"/> (the bubble's apps as shown now) without the app itself.
    /// </summary>
    internal void PlaceApp(DockItem item, string bubbleId, int index, IReadOnlyList<DockItem> shownThere)
    {
        if (!_bubbles.ContainsKey(bubbleId))
        {
            return;
        }

        var shown = new List<DockItem>();
        foreach (DockItem other in shownThere)
        {
            if (!SameApp(other, item))
            {
                shown.Add(other);
            }
        }

        shown.Insert(Math.Clamp(index, 0, shown.Count), item);
        RemoveEverywhere(item);
        foreach (DockItem other in shown)
        {
            RemoveEverywhere(other);
        }

        SetShownFirst(bubbleId, shown);
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// A new group holding <paramref name="items"/> (in that order), placed just after the
    /// bubble <paramref name="after"/>. Returns its ID.
    /// </summary>
    internal string MakeGroup(IReadOnlyList<DockItem> items, string after)
    {
        int number = 1;
        while (_bubbles.ContainsKey(GroupPrefix + number.ToString(CultureInfo.InvariantCulture)))
        {
            number++;
        }

        string id = GroupPrefix + number.ToString(CultureInfo.InvariantCulture);
        foreach (DockItem item in items)
        {
            RemoveEverywhere(item);
        }

        var keys = new List<string>();
        foreach (DockItem item in items)
        {
            string key = KeyOf(item);
            if (!Contains(keys, key))
            {
                keys.Add(key);
            }
        }

        _bubbles[id] = keys;
        int at = IndexOf(_order, after);
        _order.Insert(at < 0 ? _order.Count : at + 1, id);
        Save();
        Changed?.Invoke();
        return id;
    }

    /// <summary>Ends a group: its apps go back to the end of the apps bubble, in their order.</summary>
    internal void Ungroup(string groupId)
    {
        if (!IsGroup(groupId) || !_bubbles.TryGetValue(groupId, out List<string>? keys))
        {
            return;
        }

        List<string> apps = _bubbles[AppsId];
        foreach (string key in keys)
        {
            if (!Contains(apps, key))
            {
                apps.Add(key);
            }
        }

        _bubbles.Remove(groupId);
        int at = IndexOf(_order, groupId);
        if (at >= 0)
        {
            _order.RemoveAt(at);
        }

        Trim(apps);
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Moves a bubble: just before <paramref name="beforeId"/>, or (when that is null) just
    /// after <paramref name="afterId"/>; with neither, to the end.
    /// </summary>
    internal void MoveBubble(string id, string? beforeId, string? afterId)
    {
        int from = IndexOf(_order, id);
        if (from < 0)
        {
            return;
        }

        var order = new List<string>(_order);
        order.RemoveAt(from);
        int before = beforeId is null ? -1 : IndexOf(order, beforeId);
        int after = afterId is null ? -1 : IndexOf(order, afterId);
        int to = before >= 0 ? before : after >= 0 ? after + 1 : order.Count;

        order.Insert(to, id);
        if (SameOrder(order, _order))
        {
            return;
        }

        _order.Clear();
        _order.AddRange(order);
        Save();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- inside

    /// <summary>Reads a layout document; on a wrong shape it throws and changes nothing.</summary>
    private void ParseInto(string text)
    {
        if (JsonNode.Parse(text, NodeOptions, DocumentOptions) is not JsonObject document
            || document["Order"] is not JsonArray order
            || document["Bubbles"] is not JsonObject bubbles)
        {
            throw new JsonException("The bar layout does not have the expected shape.");
        }

        var newBubbles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, JsonNode? keys) in bubbles)
        {
            if ((id.Equals(AppsId, StringComparison.OrdinalIgnoreCase) || IsGroup(id)) && keys is JsonArray list)
            {
                var names = new List<string>();
                foreach (JsonNode? key in list)
                {
                    if (key is JsonValue value && value.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name) && !Contains(names, name))
                    {
                        names.Add(name);
                    }
                }

                newBubbles[id] = names;
            }
        }

        var newOrder = new List<string>();
        foreach (JsonNode? entry in order)
        {
            if (entry is JsonValue value && value.TryGetValue(out string? id)
                && (id == StartId || id == TrayId || newBubbles.ContainsKey(id))
                && !Contains(newOrder, id))
            {
                newOrder.Add(id);
            }
        }

        List<string> newTray = ReadNames(document["Tray"]);
        List<string> newTrayOut = ReadNames(document["TrayOut"]);

        _order.Clear();
        _order.AddRange(newOrder);
        _bubbles.Clear();
        foreach ((string id, List<string> keys) in newBubbles)
        {
            _bubbles[id] = keys;
        }

        _tray.Clear();
        _tray.AddRange(newTray);
        Trim(_tray);
        _trayOut.Clear();
        _trayOut.AddRange(newTrayOut);
        Trim(_trayOut);
        Repair();
    }

    /// <summary>The distinct, non-empty names in a JSON list (an absent or odd value gives none).</summary>
    private static List<string> ReadNames(JsonNode? node)
    {
        var names = new List<string>();
        if (node is JsonArray list)
        {
            foreach (JsonNode? key in list)
            {
                if (key is JsonValue value && value.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name) && !Contains(names, name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>Removes every copy of the name (any case); how many went.</summary>
    private static int RemoveKey(List<string> names, string key)
    {
        return names.RemoveAll(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));
    }

    private JsonObject BuildDocument()
    {
        var order = new JsonArray();
        foreach (string id in _order)
        {
            order.Add(id);
        }

        var bubbles = new JsonObject();
        foreach (string id in _order)
        {
            if (id == StartId || id == TrayId)
            {
                continue;
            }

            var keys = new JsonArray();
            foreach (string key in _bubbles[id])
            {
                keys.Add(key);
            }

            bubbles[id] = keys;
        }

        var tray = new JsonArray();
        foreach (string key in _tray)
        {
            tray.Add(key);
        }

        var trayOut = new JsonArray();
        foreach (string key in _trayOut)
        {
            trayOut.Add(key);
        }

        return new JsonObject { ["Order"] = order, ["Bubbles"] = bubbles, ["Tray"] = tray, ["TrayOut"] = trayOut };
    }

    private void ResetToDefault()
    {
        _order.Clear();
        _order.Add(StartId);
        _order.Add(AppsId);
        _order.Add(TrayId);
        _bubbles.Clear();
        _bubbles[AppsId] = new List<string>();
        _tray.Clear();
        _trayOut.Clear();
    }

    /// <summary>Start and the apps are always there; every group in the order, every ordered bubble kept.</summary>
    private void Repair()
    {
        if (!_bubbles.ContainsKey(AppsId))
        {
            _bubbles[AppsId] = new List<string>();
        }

        if (IndexOf(_order, StartId) < 0)
        {
            _order.Insert(0, StartId);
        }

        if (IndexOf(_order, TrayId) < 0)
        {
            _order.Add(TrayId);
        }

        foreach (string id in new List<string>(_bubbles.Keys))
        {
            if (IndexOf(_order, id) < 0)
            {
                _order.Add(id);
            }
        }

        // An app sits in one bubble only: the first one in the order keeps it.
        var seen = new List<string>();
        foreach (string id in _order)
        {
            if (id == StartId || id == TrayId)
            {
                continue;
            }

            List<string> keys = _bubbles[id];
            keys.RemoveAll(key => Contains(seen, key));
            seen.AddRange(keys);
            Trim(keys);
        }
    }

    /// <summary>The bubble's shown apps in their new order, then its apps not shown now (closed ones), as they were.</summary>
    private void SetShownFirst(string bubbleId, List<DockItem> shown)
    {
        List<string> old = _bubbles[bubbleId];
        var keys = new List<string>();
        foreach (DockItem item in shown)
        {
            string key = KeyOf(item);
            if (!Contains(keys, key))
            {
                keys.Add(key);
            }
        }

        foreach (string key in old)
        {
            if (!Contains(keys, key))
            {
                keys.Add(key);
            }
        }

        Trim(keys);
        _bubbles[bubbleId] = keys;
    }

    private void RemoveEverywhere(DockItem item)
    {
        foreach (List<string> keys in _bubbles.Values)
        {
            keys.RemoveAll(key => Matches(key, item));
        }
    }

    private static void Trim(List<string> keys)
    {
        if (keys.Count > MaxKeysPerBubble)
        {
            keys.RemoveRange(MaxKeysPerBubble, keys.Count - MaxKeysPerBubble);
        }
    }

    private static bool Matches(string key, DockItem item)
    {
        return string.Equals(key, item.Key, StringComparison.OrdinalIgnoreCase)
            || (item.PinKey is string pin && string.Equals(key, pin, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameApp(DockItem a, DockItem b)
    {
        return Matches(KeyOf(a), b) || Matches(a.Key, b);
    }

    private static int Find(IReadOnlyList<string> keys, DockItem item)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (Matches(keys[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOf(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Contains(List<string> list, string value)
    {
        return IndexOf(list, value) >= 0;
    }

    private static bool SameOrder(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void Save()
    {
        if (_readFailed)
        {
            Log.Warn("Bar layout: not saved, because the file on disk could not be read at start and must not be overwritten.");
            return;
        }

        try
        {
            AtomicFile.WriteAllText(AppPaths.BarLayoutFile, BuildDocument().ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Bar layout: the file could not be saved.", ex);
        }
    }
}
