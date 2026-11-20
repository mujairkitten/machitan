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
/// (a Start-menu shortcut keeps its arguments and working folder).
/// </summary>
public sealed record PinnedApp(string Path, string Name, string? AppId = null, string? Launch = null)
{
    public string LaunchTarget => Launch ?? Path;
}

/// <summary>
/// A list of pinned apps in a file under %AppData%\Machitan: pins.json for the bar ("Pin to
/// taskbar"), start-pins.json for Start's Pinned box ("Pin to Start"). The two were one list
/// before; the Start list starts as a copy of the bar's the first time it is read.
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
    private readonly string _file;
    private readonly string _label;
    private readonly string? _seedFile;
    private bool _readFailed;

    /// <param name="file">The list's own file.</param>
    /// <param name="label">How the log names it ("Pins", "Start pins").</param>
    /// <param name="seedFile">Read once when <paramref name="file"/> does not exist yet, then saved as it.</param>
    internal PinStore(string file, string label, string? seedFile = null)
    {
        _file = file;
        _label = label;
        _seedFile = seedFile;
    }

    /// <summary>Raised when the list changed.</summary>
    internal event Action? Changed;

    internal IReadOnlyList<PinnedApp> Pins => _pins;

    /// <summary>True when the file on disk could not be read and saving is blocked.</summary>
    internal bool IsReadOnlyBecauseOfBadFile => _readFailed;

    /// <summary>Reads the file. Never throws.</summary>
    internal void Load()
    {
        _pins.Clear();
        _readFailed = false;

        if (!File.Exists(_file) && _seedFile is not null && File.Exists(_seedFile))
        {
            // First run with a separate list: start from the old shared one, then keep our own.
            try
            {
                ReadFrom(_seedFile);
                Log.Info(_label + ": " + _pins.Count.ToString(CultureInfo.InvariantCulture) + " pinned app(s) copied from "
                    + System.IO.Path.GetFileName(_seedFile) + " (one list until now).");
                Save();
            }
            catch (Exception ex)
            {
                _pins.Clear();
                Log.Warn(_label + ": " + System.IO.Path.GetFileName(_seedFile) + " could not be copied; starting with no pinned apps.", ex);
            }

            return;
        }

        try
        {
            if (!File.Exists(_file))
            {
                Log.Info(_label + ": no file yet; starting with no pinned apps.");
                return;
            }

            ReadFrom(_file);
            Log.Info(_label + ": " + _pins.Count.ToString(CultureInfo.InvariantCulture) + " pinned app(s) loaded.");
        }
        catch (Exception ex)
        {
            _pins.Clear();
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
                _pins.Add(new PinnedApp(path, name, ReadText(item, "AppId"), ReadText(item, "Launch")));
            }
        }
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
        _pins.Insert(Math.Clamp(index, 0, _pins.Count), pin with { Name = name });
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

                entries.Add(item);
            }

            var document = new JsonObject { ["Pins"] = entries };
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
