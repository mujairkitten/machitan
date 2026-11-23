using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Machitan.Core;

namespace Machitan.Shell;

/// <summary>
/// One pinned app.
/// Path: the identity (an exe path, or "shell:AppsFolder\&lt;app ID&gt;" for store apps).
/// AppId: the store app's ID, when it is one. Launch: what to start, when that is not Path
/// (a Start-menu shortcut keeps its arguments and working folder). Group: the category it
/// sits in (Start's pins only; see <see cref="PinGroup"/>).
/// </summary>
public sealed record PinnedApp(string Path, string Name, string? AppId = null, string? Launch = null, string? Group = null)
{
    public string LaunchTarget => Launch ?? Path;
}

/// <summary>
/// A category of Start's pins, in the order shown. Name: as the user typed it; null for the
/// first one before it is renamed (shown as "Pinned").
/// </summary>
public sealed record PinGroup(string Id, string? Name);

/// <summary>
/// A list of pinned apps in a file under %AppData%\Machitan: pins.json for the bar ("Pin to
/// taskbar"), start-pins.json for Start's Pinned box ("Pin to Start"). The two were one list
/// before; the Start list starts as a copy of the bar's the first time it is read.
/// Start's list keeps its pins in categories ("Groups" in the file, each pin's "Group"); a
/// list without them (the bar's, or Start's before categories) gets one unnamed category.
/// Safety rule: if the file exists but cannot be read, this store refuses to write for the
/// rest of the run. An unreadable file is never replaced by an empty list.
/// </summary>
internal sealed class PinStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly List<PinnedApp> _pins = new();
    private readonly List<PinGroup> _groups = new();
    private readonly string _file;
    private readonly string _label;
    private readonly string? _seedFile;
    private readonly bool _grouped;
    private bool _readFailed;

    /// <param name="file">The list's own file.</param>
    /// <param name="label">How the log names it ("Pins", "Start pins").</param>
    /// <param name="seedFile">Read once when <paramref name="file"/> does not exist yet, then saved as it.</param>
    /// <param name="grouped">Keeps the pins in categories (Start's list).</param>
    internal PinStore(string file, string label, string? seedFile = null, bool grouped = false)
    {
        _file = file;
        _label = label;
        _seedFile = seedFile;
        _grouped = grouped;
    }

    /// <summary>Raised when the list changed.</summary>
    internal event Action? Changed;

    internal IReadOnlyList<PinnedApp> Pins => _pins;

    /// <summary>The categories, in the order shown (always at least one for a grouped list; none otherwise).</summary>
    internal IReadOnlyList<PinGroup> Groups => _groups;

    /// <summary>True when the file on disk could not be read and saving is blocked.</summary>
    internal bool IsReadOnlyBecauseOfBadFile => _readFailed;

    /// <summary>Reads the file. Never throws.</summary>
    internal void Load()
    {
        _pins.Clear();
        _groups.Clear();
        _readFailed = false;

        if (!File.Exists(_file) && _seedFile is not null && File.Exists(_seedFile))
        {
            // First run with a separate list: start from the old shared one, then keep our own.
            try
            {
                ReadFrom(_seedFile);
                FixGroups();
                Log.Info(_label + ": " + _pins.Count.ToString(CultureInfo.InvariantCulture) + " pinned app(s) copied from "
                    + System.IO.Path.GetFileName(_seedFile) + " (one list until now).");
                Save();
            }
            catch (Exception ex)
            {
                _pins.Clear();
                _groups.Clear();
                FixGroups();
                Log.Warn(_label + ": " + System.IO.Path.GetFileName(_seedFile) + " could not be copied; starting with no pinned apps.", ex);
            }

            return;
        }

        try
        {
            if (!File.Exists(_file))
            {
                FixGroups();
                Log.Info(_label + ": no file yet; starting with no pinned apps.");
                return;
            }

            ReadFrom(_file);
            FixGroups();
            Log.Info(_label + ": " + _pins.Count.ToString(CultureInfo.InvariantCulture) + " pinned app(s) loaded.");
        }
        catch (Exception ex)
        {
            _pins.Clear();
            _groups.Clear();
            FixGroups();
            _readFailed = true;
            Log.Error(_label + ": the file could not be read. It is left untouched and pin changes will not be saved this run.", ex);
            KeepCopyOfUnreadableFile();
        }
    }

    private void ReadFrom(string file)
    {
        string text = File.ReadAllText(file);
        if (JsonNode.Parse(text, NodeOptions, DocumentOptions) is not JsonObject document
            || document["Pins"] is not JsonArray entries)
        {
            throw new JsonException("The pins file does not have the expected shape.");
        }

        foreach (JsonNode? entry in entries)
        {
            if (entry is not JsonObject item || ReadText(item, "Path") is not string path)
            {
                throw new JsonException("A pin entry has no path.");
            }

            string name = ReadText(item, "Name") ?? System.IO.Path.GetFileNameWithoutExtension(path);
            if (!IsPinned(path))
            {
                _pins.Add(new PinnedApp(path, name, ReadText(item, "AppId"), ReadText(item, "Launch"), _grouped ? ReadText(item, "Group") : null));
            }
        }

        if (_grouped && document["Groups"] is JsonArray groups)
        {
            foreach (JsonNode? entry in groups)
            {
                if (entry is JsonObject item && ReadText(item, "Id") is string id && FindGroup(id) < 0)
                {
                    _groups.Add(new PinGroup(id, ReadText(item, "Name")));
                }
            }
        }
    }

    /// <summary>A grouped list has at least one category, and every pin sits in one of them (else the first).</summary>
    private void FixGroups()
    {
        if (!_grouped)
        {
            return;
        }

        if (_groups.Count == 0)
        {
            _groups.Add(new PinGroup(NewGroupId(), null));
        }

        for (int i = 0; i < _pins.Count; i++)
        {
            if (_pins[i].Group is not string group || FindGroup(group) < 0)
            {
                _pins[i] = _pins[i] with { Group = _groups[0].Id };
            }
        }
    }

    // ---------------------------------------------------------------- categories (Start)

    /// <summary>Renames a category; an empty name gives it back its default. Returns false when nothing changed.</summary>
    internal bool RenameGroup(string id, string? name)
    {
        int index = FindGroup(id);
        string? clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (index < 0 || _groups[index].Name == clean)
        {
            return false;
        }

        _groups[index] = _groups[index] with { Name = clean };
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Adds a category after <paramref name="afterId"/> (or at the end) and returns its ID.</summary>
    internal string AddGroup(string name, string? afterId = null)
    {
        var group = new PinGroup(NewGroupId(), string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        int after = afterId is null ? -1 : FindGroup(afterId);
        _groups.Insert(after < 0 ? _groups.Count : after + 1, group);
        Save();
        Changed?.Invoke();
        return group.Id;
    }

    /// <summary>
    /// Removes a category; its pins move to the category above it (the one below, for the
    /// first). Nothing is unpinned. The last category cannot be removed.
    /// </summary>
    internal bool RemoveGroup(string id)
    {
        int index = FindGroup(id);
        if (index < 0 || _groups.Count < 2)
        {
            return false;
        }

        string to = _groups[index == 0 ? 1 : index - 1].Id;
        _groups.RemoveAt(index);
        for (int i = 0; i < _pins.Count; i++)
        {
            if (string.Equals(_pins[i].Group, id, StringComparison.Ordinal))
            {
                _pins[i] = _pins[i] with { Group = to };
            }
        }

        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves a category up (-1) or down (+1). Returns false when it is already at that end.</summary>
    internal bool MoveGroup(string id, int step)
    {
        int index = FindGroup(id);
        int to = index + step;
        if (index < 0 || to < 0 || to >= _groups.Count)
        {
            return false;
        }

        PinGroup group = _groups[index];
        _groups.RemoveAt(index);
        _groups.Insert(to, group);
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves a pin to the end of a category. Returns false when it is not pinned or already there.</summary>
    internal bool MoveToGroup(string path, string groupId)
    {
        int from = IndexOf(path);
        if (from < 0 || FindGroup(groupId) < 0 || string.Equals(_pins[from].Group, groupId, StringComparison.Ordinal))
        {
            return false;
        }

        PinnedApp pin = _pins[from] with { Group = groupId };
        _pins.RemoveAt(from);
        _pins.Add(pin);
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Puts an app into a category at a place among that category's pins (0 = first; past the
    /// end = last): a pinned app moves there, an app not yet pinned is pinned there (a drag in
    /// Start). Returns false when the category is unknown.
    /// </summary>
    internal bool PlaceInGroup(PinnedApp pin, string groupId, int index)
    {
        if (!_grouped || FindGroup(groupId) < 0 || string.IsNullOrWhiteSpace(pin.Path))
        {
            return false;
        }

        PinnedApp placed;
        int from = IndexOf(pin.Path);
        if (from >= 0)
        {
            placed = _pins[from] with { Group = groupId };
            _pins.RemoveAt(from);
        }
        else
        {
            string name = string.IsNullOrWhiteSpace(pin.Name) ? System.IO.Path.GetFileNameWithoutExtension(pin.Path) : pin.Name;
            placed = pin with { Name = name, Group = groupId };
        }

        // In the whole list: before the category's pin at that place, else after its last pin,
        // else at the end.
        int at = -1;
        int lastOfGroup = -1;
        int seen = 0;
        for (int i = 0; i < _pins.Count; i++)
        {
            if (!string.Equals(_pins[i].Group, groupId, StringComparison.Ordinal))
            {
                continue;
            }

            if (seen == index)
            {
                at = i;
                break;
            }

            seen++;
            lastOfGroup = i;
        }

        if (at < 0)
        {
            at = lastOfGroup >= 0 ? lastOfGroup + 1 : _pins.Count;
        }

        _pins.Insert(at, placed);
        Save();
        Changed?.Invoke();
        return true;
    }

    private int FindGroup(string id)
    {
        for (int i = 0; i < _groups.Count; i++)
        {
            if (string.Equals(_groups[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private string NewGroupId()
    {
        string id;
        do
        {
            id = "g" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }
        while (FindGroup(id) >= 0);

        return id;
    }

    internal bool IsPinned(string path)
    {
        return IndexOf(path) >= 0;
    }

    /// <summary>Adds a pin by exe path. Returns false when it was already pinned.</summary>
    internal bool Pin(string path, string name)
    {
        return Pin(new PinnedApp(path, name));
    }

    /// <summary>Adds a pin at the end. Returns false when it was already pinned.</summary>
    internal bool Pin(PinnedApp pin)
    {
        return PinAt(pin, _pins.Count);
    }

    /// <summary>
    /// Adds a pin at <paramref name="index"/> (0 = first; clamped to the list). Returns false
    /// when it was already pinned.
    /// </summary>
    internal bool PinAt(PinnedApp pin, int index)
    {
        if (string.IsNullOrWhiteSpace(pin.Path) || IsPinned(pin.Path))
        {
            return false;
        }

        string name = string.IsNullOrWhiteSpace(pin.Name) ? System.IO.Path.GetFileNameWithoutExtension(pin.Path) : pin.Name;

        // Start's list: a new pin goes into its category, or the first one.
        string? group = !_grouped ? null
            : pin.Group is string wanted && FindGroup(wanted) >= 0 ? wanted
            : _groups.Count > 0 ? _groups[0].Id : null;
        _pins.Insert(Math.Clamp(index, 0, _pins.Count), pin with { Name = name, Group = group });
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Moves a pin to <paramref name="index"/>, counted in the list without it (0 = first;
    /// clamped). Returns false when it is not pinned or already there.
    /// </summary>
    internal bool Move(string path, int index)
    {
        int from = IndexOf(path);
        if (from < 0)
        {
            return false;
        }

        PinnedApp pin = _pins[from];
        _pins.RemoveAt(from);
        int to = Math.Clamp(index, 0, _pins.Count);
        _pins.Insert(to, pin);
        if (to == from)
        {
            return false;
        }

        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes a pin. Returns false when it was not pinned.</summary>
    internal bool Unpin(string path)
    {
        int index = IndexOf(path);
        if (index < 0)
        {
            return false;
        }

        _pins.RemoveAt(index);
        Save();
        Changed?.Invoke();
        return true;
    }

    private static string? ReadText(JsonObject item, string key)
    {
        return item[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    private int IndexOf(string path)
    {
        for (int i = 0; i < _pins.Count; i++)
        {
            if (string.Equals(_pins[i].Path, path, StringComparison.OrdinalIgnoreCase))
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
            Log.Warn(_label + ": not saved, because the file on disk could not be read at start and must not be overwritten.");
            return;
        }

        try
        {
            var entries = new JsonArray();
            foreach (PinnedApp pin in _pins)
            {
                var item = new JsonObject
                {
                    ["Path"] = pin.Path,
                    ["Name"] = pin.Name,
                };

                if (pin.AppId is not null)
                {
                    item["AppId"] = pin.AppId;
                }

                if (pin.Launch is not null)
                {
                    item["Launch"] = pin.Launch;
                }

                if (_grouped && pin.Group is not null)
                {
                    item["Group"] = pin.Group;
                }

                entries.Add(item);
            }

            var document = new JsonObject { ["Pins"] = entries };
            if (_grouped)
            {
                var groups = new JsonArray();
                foreach (PinGroup group in _groups)
                {
                    var item = new JsonObject { ["Id"] = group.Id };
                    if (group.Name is not null)
                    {
                        item["Name"] = group.Name;
                    }

                    groups.Add(item);
                }

                document["Groups"] = groups;
            }
            AtomicFile.WriteAllText(_file, document.ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            Log.Error(_label + ": the file could not be saved.", ex);
        }
    }

    private void KeepCopyOfUnreadableFile()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string copy = System.IO.Path.Combine(
                AppPaths.DataFolder,
                System.IO.Path.GetFileNameWithoutExtension(_file) + ".unreadable-" + stamp + ".json");
            File.Copy(_file, copy, overwrite: true);
            Log.Info(_label + ": a copy of the unreadable file was kept as " + System.IO.Path.GetFileName(copy) + ".");
        }
        catch (Exception ex)
        {
            Log.Warn(_label + ": could not keep a copy of the unreadable file.", ex);
        }
    }
}
