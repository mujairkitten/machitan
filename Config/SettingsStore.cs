using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Machitan.Core;
using Microsoft.UI.Dispatching;

namespace Machitan.Config;

/// <summary>
/// Loads and saves %AppData%\Machitan\settings.json.
/// - A missing file is a fresh install: the defaults are written once.
/// - An existing file keeps every value the user saved; only missing or unreadable values
///   fall back to their default, one by one.
/// - Enums are written as text and read as text or as the older numeric form.
/// - Keys this version does not know are kept untouched when saving.
/// - Writes are atomic.
/// </summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private JsonObject _document = new(NodeOptions);
    private string? _lastWrittenText;
    private FileSystemWatcher? _watcher;

    /// <summary>Raised on the UI thread when the values changed.</summary>
    internal event Action? Changed;

    internal ShellSettings Current { get; private set; } = new();

    /// <summary>
    /// Changes settings from inside Machitan (the Settings window, or a flyout): applies,
    /// saves, and tells everyone at once. Does nothing when nothing actually changed.
    /// <paramref name="source"/> is named in the log.
    /// </summary>
    internal void Update(Action<ShellSettings> change, string source = "the Settings window")
    {
        try
        {
            ShellSettings before = Current;
            ShellSettings next = before with { };
            change(next);
            next.Normalize();
            if (next.Equals(before))
            {
                return;
            }

            Current = next;
            Log.Info("Settings: changed in " + source + ": " + DescribeChanges(before, next) + ".");
            Save();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Settings: a change could not be applied.", ex);
        }
    }

    /// <summary>Reads the file. Never throws.</summary>
    internal void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                _document = new JsonObject(NodeOptions);
                Current = new ShellSettings();
                Save();
                Log.Info("Settings: no file found, defaults written.");
                return;
            }

            string text = File.ReadAllText(AppPaths.SettingsFile);
            if (JsonNode.Parse(text, NodeOptions, DocumentOptions) is not JsonObject document)
            {
                throw new JsonException("The settings file does not contain a JSON object.");
            }

            _document = document;
            Current = ReadValues(document);
        }
        catch (Exception ex)
        {
            Log.Error("Settings: the file could not be read; defaults are used for this run.", ex);
            KeepCopyOfUnreadableFile();
            _document = new JsonObject(NodeOptions);
            Current = new ShellSettings();
        }
    }

    /// <summary>Writes the current values. Never throws.</summary>
    internal void Save()
    {
        try
        {
            ShellSettings s = Current;
            s.Normalize();

            _document["Theme"] = s.Theme.ToString();
            _document["TaskbarMode"] = s.TaskbarMode.ToString();
            _document["DockEdge"] = s.DockEdge.ToString();
            _document["DockAlignment"] = s.DockAlignment.ToString();
            _document["BarSize"] = s.BarSize;
            _document["IconSize"] = s.IconSize;
            _document["Opacity"] = s.Opacity;
            _document["EnableAcrylic"] = s.EnableAcrylic;
            _document["SearchMode"] = s.SearchMode.ToString();
            _document["ShowSystemStats"] = s.ShowSystemStats;
            _document["ShowFocusedAppName"] = s.ShowFocusedAppName;
            _document["ShowRecentItems"] = s.ShowRecentItems;
            _document["AutoHideBar"] = s.AutoHideBar;
            _document["EnableDynaland"] = s.EnableDynaland;
            _document["InfoLineEnabled"] = s.InfoLineEnabled;
            _document["InfoLineShowDate"] = s.InfoLineShowDate;
            _document["InfoLineShowTime"] = s.InfoLineShowTime;
            _document["InfoLineShowWeather"] = s.InfoLineShowWeather;
            _document["InfoLineCorner"] = s.InfoLineCorner.ToString();
            _document["DuoRingEnabled"] = s.DuoRingEnabled;
            _document["DuoRingCorner"] = s.DuoRingCorner.ToString();
            _document["CalendarShowSeconds"] = s.CalendarShowSeconds;
            _document["CalendarCollapsed"] = s.CalendarCollapsed;

            string text = _document.ToJsonString(WriteOptions);
            AtomicFile.WriteAllText(AppPaths.SettingsFile, text);
            _lastWrittenText = text;
        }
        catch (Exception ex)
        {
            Log.Error("Settings: the file could not be saved.", ex);
        }
    }

    /// <summary>
    /// Watches the file so an edit made by hand is picked up while the shell runs.
    /// (There is no Settings window until Batch 3.)
    /// </summary>
    internal void StartWatching(DispatcherQueue queue)
    {
        try
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(400);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => ReloadFromDisk();

            AppPaths.EnsureDataFolder();
            _watcher = new FileSystemWatcher(AppPaths.DataFolder, Path.GetFileName(AppPaths.SettingsFile))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            // Watcher events arrive on a worker thread; hop to the UI thread and debounce there.
            void Schedule()
            {
                queue.TryEnqueue(() =>
                {
                    timer.Stop();
                    timer.Start();
                });
            }

            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Settings: the file watcher could not be started; edits apply on the next start.", ex);
        }
    }

    private void ReloadFromDisk()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return;
            }

            // Machitan's own save (from the Settings window) is already applied.
            if (_lastWrittenText is not null && File.ReadAllText(AppPaths.SettingsFile) == _lastWrittenText)
            {
                return;
            }

            ShellSettings before = Current;
            Load();
            if (Current.Equals(before))
            {
                Log.Info("Settings: file saved, but no setting changed.");
                return;
            }

            Log.Info("Settings: file changed on disk: " + DescribeChanges(before, Current) + ".");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Settings: reload failed.", ex);
        }
    }

    private static ShellSettings ReadValues(JsonObject document)
    {
        var s = new ShellSettings();

        if (TryEnum(document["Theme"], out ThemeMode theme))
        {
            s.Theme = theme;
        }
        else
        {
            NoteUnreadable(document, "Theme");
        }

        if (TryEnum(document["TaskbarMode"], out TaskbarMode mode))
        {
            s.TaskbarMode = mode;
        }
        else
        {
            NoteUnreadable(document, "TaskbarMode");
        }

        if (TryEnum(document["DockEdge"], out DockEdge edge))
        {
            s.DockEdge = edge;
        }
        else
        {
            NoteUnreadable(document, "DockEdge");
        }

        if (TryEnum(document["DockAlignment"], out DockAlignment alignment))
        {
            s.DockAlignment = alignment;
        }
        else
        {
            NoteUnreadable(document, "DockAlignment");
        }

        if (TryInt(document["BarSize"], out int barSize))
        {
            s.BarSize = barSize;
        }
        else
        {
            NoteUnreadable(document, "BarSize");
        }

        if (TryInt(document["IconSize"], out int iconSize))
        {
            s.IconSize = iconSize;
        }
        else
        {
            NoteUnreadable(document, "IconSize");
        }

        if (TryDouble(document["Opacity"], out double opacity))
        {
            s.Opacity = opacity;
        }
        else
        {
            NoteUnreadable(document, "Opacity");
        }

        if (TryBool(document["EnableAcrylic"], out bool acrylic))
        {
            s.EnableAcrylic = acrylic;
        }
        else
        {
            NoteUnreadable(document, "EnableAcrylic");
        }

        if (TryEnum(document["SearchMode"], out SearchMode search))
        {
            s.SearchMode = search;
        }
        else
        {
            NoteUnreadable(document, "SearchMode");
        }

        if (TryBool(document["ShowSystemStats"], out bool stats))
        {
            s.ShowSystemStats = stats;
        }
        else
        {
            NoteUnreadable(document, "ShowSystemStats");
        }

        if (TryBool(document["ShowFocusedAppName"], out bool focusedName))
        {
            s.ShowFocusedAppName = focusedName;
        }
        else
        {
            NoteUnreadable(document, "ShowFocusedAppName");
        }

        if (TryBool(document["ShowRecentItems"], out bool recent))
        {
            s.ShowRecentItems = recent;
        }
        else
        {
            NoteUnreadable(document, "ShowRecentItems");
        }

        if (TryBool(document["AutoHideBar"], out bool autoHide))
        {
            s.AutoHideBar = autoHide;
        }
        else
        {
            NoteUnreadable(document, "AutoHideBar");
        }

        if (TryBool(document["InfoLineEnabled"], out bool infoLine))
        {
            s.InfoLineEnabled = infoLine;
        }
        else
        {
            NoteUnreadable(document, "InfoLineEnabled");
        }

        if (TryBool(document["InfoLineShowDate"], out bool infoDate))
        {
            s.InfoLineShowDate = infoDate;
        }
        else
        {
            NoteUnreadable(document, "InfoLineShowDate");
        }

        if (TryBool(document["InfoLineShowTime"], out bool infoTime))
        {
            s.InfoLineShowTime = infoTime;
        }
        else
        {
            NoteUnreadable(document, "InfoLineShowTime");
        }

        if (TryBool(document["InfoLineShowWeather"], out bool infoWeather))
        {
            s.InfoLineShowWeather = infoWeather;
        }
        else
        {
            NoteUnreadable(document, "InfoLineShowWeather");
        }

        if (TryEnum(document["InfoLineCorner"], out ScreenCorner infoCorner))
        {
            s.InfoLineCorner = infoCorner;
        }
        else
        {
            NoteUnreadable(document, "InfoLineCorner");
        }

        if (TryBool(document["DuoRingEnabled"], out bool duoRing))
        {
            s.DuoRingEnabled = duoRing;
        }
        else
        {
            NoteUnreadable(document, "DuoRingEnabled");
        }

        if (TryEnum(document["DuoRingCorner"], out ScreenCorner duoCorner))
        {
            s.DuoRingCorner = duoCorner;
        }
        else
        {
            NoteUnreadable(document, "DuoRingCorner");
        }

        if (TryBool(document["CalendarShowSeconds"], out bool calendarSeconds))
        {
            s.CalendarShowSeconds = calendarSeconds;
        }
        else
        {
            NoteUnreadable(document, "CalendarShowSeconds");
        }

        if (TryBool(document["CalendarCollapsed"], out bool calendarCollapsed))
        {
            s.CalendarCollapsed = calendarCollapsed;
        }
        else
        {
            NoteUnreadable(document, "CalendarCollapsed");
        }

        if (TryBool(document["EnableDynaland"], out bool dynaland))
        {
            s.EnableDynaland = dynaland;
        }
        else
        {
            NoteUnreadable(document, "EnableDynaland");
        }

        s.Normalize();
        return s;
    }

    /// <summary>A key that is present but whose value is not understood keeps its default; say so.</summary>
    private static void NoteUnreadable(JsonObject document, string key)
    {
        JsonNode? node = document[key];
        if (node is null)
        {
            return;
        }

        Log.Warn("Settings: \"" + key + "\" has a value that is not understood (" + node.ToJsonString() + "); the default is used.");
    }

    /// <summary>Names every setting that differs, with its old and new value.</summary>
    private static string DescribeChanges(ShellSettings before, ShellSettings after)
    {
        var changes = new System.Collections.Generic.List<string>();
        foreach (System.Reflection.PropertyInfo property in typeof(ShellSettings).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            object? oldValue = property.GetValue(before);
            object? newValue = property.GetValue(after);
            if (!Equals(oldValue, newValue))
            {
                changes.Add(property.Name + " " + Convert.ToString(oldValue, CultureInfo.InvariantCulture)
                    + " -> " + Convert.ToString(newValue, CultureInfo.InvariantCulture));
            }
        }

        return string.Join(", ", changes);
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue json)
        {
            return false;
        }

        if (json.TryGetValue(out int whole))
        {
            value = whole;
            return true;
        }

        if (json.TryGetValue(out double number) && !double.IsNaN(number) && !double.IsInfinity(number)
            && number >= int.MinValue && number <= int.MaxValue)
        {
            value = (int)Math.Round(number);
            return true;
        }

        return false;
    }

    private static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue json)
        {
            return false;
        }

        if (json.TryGetValue(out double number) && !double.IsNaN(number) && !double.IsInfinity(number))
        {
            value = number;
            return true;
        }

        return false;
    }

    private static bool TryBool(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue json && json.TryGetValue(out value);
    }

    /// <summary>Accepts the name ("Dark") or the older numeric form (0).</summary>
    private static bool TryEnum<T>(JsonNode? node, out T value)
        where T : struct, Enum
    {
        value = default;
        if (node is not JsonValue json)
        {
            return false;
        }

        if (json.TryGetValue(out string? text) && text is not null)
        {
            text = text.Trim();

            // Enum.TryParse would also accept "7"; a number in quotes is handled below instead.
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quoted))
            {
                return TryEnumFromNumber(quoted, out value);
            }

            return Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
        }

        return TryInt(node, out int number) && TryEnumFromNumber(number, out value);
    }

    private static bool TryEnumFromNumber<T>(int number, out T value)
        where T : struct, Enum
    {
        value = default;
        if (!Enum.IsDefined(typeof(T), number))
        {
            return false;
        }

        value = (T)Enum.ToObject(typeof(T), number);
        return true;
    }

    private static void KeepCopyOfUnreadableFile()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string copy = Path.Combine(AppPaths.DataFolder, "settings.unreadable-" + stamp + ".json");
            File.Copy(AppPaths.SettingsFile, copy, overwrite: true);
            Log.Info("Settings: a copy of the unreadable file was kept as " + Path.GetFileName(copy) + ".");
        }
        catch (Exception ex)
        {
            Log.Warn("Settings: could not keep a copy of the unreadable file.", ex);
        }
    }
}
