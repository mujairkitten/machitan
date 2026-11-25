using System;
using System.Collections.Generic;

namespace Machitan.Config;

public enum ThemeMode
{
    Dark = 0,
    Light = 1,
    System = 2,
}

public enum TaskbarMode
{
    Segmented = 0,
    Joined = 1,
    Classic = 2,
    Compact = 3,
}

/// <summary>
/// Pinned apps and open apps on the bar: together (default), or split with the pinned apps on
/// top and the open apps below, either as two rows in one bar or as two bars stacked.
/// </summary>
public enum TaskbarSplit
{
    Off = 0,
    Rows = 1,
    Stacked = 2,
}

/// <summary>How hovering a running app on the bar shows its windows (Windows: "Show taskbar previews as").</summary>
public enum PreviewStyle
{
    Thumbnails = 0,
    List = 1,
}

public enum DockEdge
{
    Bottom = 0,
    Top = 1,
}

public enum DockAlignment
{
    Left = 0,
    Center = 1,
    Right = 2,
}

public enum SearchMode
{
    Icon = 0,
    Box = 1,
    Hidden = 2,
}

/// <summary>Where search sits: with Start, or inside the apps group at its start or end.</summary>
public enum SearchPlace
{
    WithStart = 0,
    BeforeApps = 1,
    AfterApps = 2,
}

public enum ScreenCorner
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

/// <summary>Dynaland's shape.</summary>
public enum DynalandStyle
{
    /// <summary>Notch Nook style: hanging from the top edge (the default).</summary>
    Notch = 0,

    /// <summary>iPhone-inspired island: floating just below the top edge, round all around.</summary>
    Island = 1,
}

/// <summary>The notch's sides (Dynaland's notch shape only).</summary>
public enum DynalandNotchShape
{
    /// <summary>Straight sides that curve out into the screen's edge at the top (the default).</summary>
    Default = 0,

    /// <summary>Sides that slant out towards the screen's edge: wider at the edge than at the bottom.</summary>
    Trapezoid = 1,
}

/// <summary>Which screen edge Dynaland sits on.</summary>
public enum DynalandPosition
{
    /// <summary>The edge without the bar: the top while the bar is at the bottom, and the other way round.</summary>
    Auto = 0,
    Top = 1,
    Bottom = 2,
}

/// <summary>The colour of the cover over the top strip.</summary>
public enum StripCoverColour
{
    Dark = 0,
    Light = 1,

    /// <summary>Machitan's accent colour (see <see cref="AccentSource"/>).</summary>
    Accent = 2,
}

/// <summary>Where Machitan's accent colour comes from.</summary>
public enum AccentSource
{
    /// <summary>The wallpaper's strongest colour (the spec's default).</summary>
    Wallpaper = 0,

    /// <summary>Windows' own accent colour.</summary>
    WindowsAccent = 1,
}

/// <summary>
/// The user's settings. The property initialisers are the fresh-install defaults from the spec.
/// A record, so two instances compare equal when every value matches.
/// </summary>
public sealed record ShellSettings
{
    public const int MinBarSize = 40;
    public const int MaxBarSize = 96;
    public const int MinIconSize = 16;
    public const int MaxIconSize = 48;
    public const double MinOpacity = 0.2;
    public const double MaxOpacity = 1.0;

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    public TaskbarMode TaskbarMode { get; set; } = TaskbarMode.Segmented;

    /// <summary>Pinned apps on top and open apps below (Settings > Taskbar). Off by default.</summary>
    public TaskbarSplit TaskbarSplit { get; set; } = TaskbarSplit.Off;

    /// <summary>Window previews on the bar: live pictures (default) or a list of titles.</summary>
    public PreviewStyle PreviewStyle { get; set; } = PreviewStyle.Thumbnails;

    public DockEdge DockEdge { get; set; } = DockEdge.Bottom;

    public DockAlignment DockAlignment { get; set; } = DockAlignment.Center;

    public int BarSize { get; set; } = 64;

    public int IconSize { get; set; } = 24;

    public double Opacity { get; set; } = 0.9;

    public bool EnableAcrylic { get; set; } = true;

    /// <summary>
    /// Start, the tray box and the bar appear and go at once, with no motion (Visuals/Motion.cs).
    /// Off by default; Windows' own Animation effects setting being off does the same.
    /// </summary>
    public bool TurnOffAnimations { get; set; }

    public SearchMode SearchMode { get; set; } = SearchMode.Icon;

    /// <summary>Set by dragging the search icon on the bar, or in Settings.</summary>
    public SearchPlace SearchPlace { get; set; } = SearchPlace.WithStart;

    /// <summary>App dock variant: each app's name beside its icon (custom mode, Settings).</summary>
    public bool DockLabels { get; set; }

    /// <summary>The Start bubble is on the bar (custom mode can remove it).</summary>
    public bool ShowStartOnBar { get; set; } = true;

    /// <summary>The apps bubble is on the bar (custom mode can remove it; groups stay).</summary>
    public bool ShowAppsOnBar { get; set; } = true;

    /// <summary>The tray icons sit in their own bubble on the bar instead of Dynaland's general page.</summary>
    public bool TrayOnBar { get; set; }

    /// <summary>
    /// Resting the pointer on Show Desktop peeks at the desktop (every window see-through, as
    /// Windows 7 and 10 did). Off by default, as in Windows 11.
    /// </summary>
    public bool PeekAtDesktop { get; set; }

    public bool ShowSystemStats { get; set; }

    public bool ShowFocusedAppName { get; set; }

    public bool ShowRecentItems { get; set; }

    public bool AutoHideBar { get; set; } = true;

    public bool EnableDynaland { get; set; } = true;

    /// <summary>Corner info line (date, time, weather). Used from Batch 5.</summary>
    public bool InfoLineEnabled { get; set; } = true;

    public bool InfoLineShowDate { get; set; } = true;

    public bool InfoLineShowTime { get; set; } = true;

    /// <summary>The info line's time shows seconds too. Off by default.</summary>
    public bool InfoLineShowSeconds { get; set; }

    public bool InfoLineShowWeather { get; set; } = true;

    public ScreenCorner InfoLineCorner { get; set; } = ScreenCorner.TopLeft;

    /// <summary>Weather for a place the user chose instead of Windows' location.</summary>
    public bool WeatherUseManualPlace { get; set; }

    /// <summary>The chosen place's name as found (shown in Settings and the tooltip).</summary>
    public string WeatherPlaceName { get; set; } = string.Empty;

    /// <summary>The chosen place, degrees; null when none is chosen.</summary>
    public double? WeatherLatitude { get; set; }

    public double? WeatherLongitude { get; set; }

    /// <summary>
    /// Corner duo status ring (battery, Wi-Fi, volume). Saved as "ShowDuoRing"; files from
    /// before the ring + Dynaland prompt said "DuoRingEnabled", which is still read.
    /// </summary>
    public bool ShowDuoRing { get; set; } = true;

    /// <summary>Dynaland's green camera and orange mic dots (and its "In use now" view on the pill).</summary>
    public bool ShowPrivacyDots { get; set; } = true;

    /// <summary>Dynaland's shape: notch (default) or island.</summary>
    public DynalandStyle DynalandStyle { get; set; } = global::Machitan.Config.DynalandStyle.Notch;

    /// <summary>Dynaland's edge: Auto (opposite the bar, the default), Top or Bottom.</summary>
    public DynalandPosition DynalandPosition { get; set; } = global::Machitan.Config.DynalandPosition.Auto;

    /// <summary>The notch's sides: Default (curving out at the top) or Trapezoid (slanting). Notch shape only.</summary>
    public DynalandNotchShape DynalandNotchShape { get; set; } = global::Machitan.Config.DynalandNotchShape.Default;

    public const int MaxDynalandTransparency = 50;

    /// <summary>
    /// How see-through Dynaland's black is, in percent: 0 (solid, the default) to 50, so its
    /// white text stays easy to read. The text and pictures on it stay solid.
    /// </summary>
    public int DynalandTransparency { get; set; }

    /// <summary>A colour over the top strip (only while the strip is kept). Off by default.</summary>
    public bool TopStripCover { get; set; }

    public StripCoverColour TopStripCoverColour { get; set; } = StripCoverColour.Dark;

    public const double MinCoverOpacity = 0.1;
    public const double MaxCoverOpacity = 1.0;

    /// <summary>How solid the cover is: 0.1 (faint) to 1 (solid).</summary>
    public double TopStripCoverOpacity { get; set; } = 0.6;

    public ScreenCorner DuoRingCorner { get; set; } = ScreenCorner.TopRight;

    public const int MinRingLowBattery = 5;
    public const int MaxRingLowBattery = 50;

    /// <summary>The status ring's battery rim turns red below this charge, in percent (20 by default).</summary>
    public int DuoRingLowBattery { get; set; } = 20;

    /// <summary>
    /// The status ring's soft background, Auto (the default): only where the wallpaper behind it
    /// is too mixed for the ink alone, at the ring's own strength. Off: Manual, always shown, as
    /// see-through as <see cref="DuoRingTransparency"/>.
    /// </summary>
    public bool DuoRingTransparencyAuto { get; set; } = true;

    /// <summary>Manual transparency of the status ring's background, in percent: 0 (solid) to 100 (none).</summary>
    public int DuoRingTransparency { get; set; } = 60;

    /// <summary>Machitan's accent colour: from the wallpaper (spec) or Windows' own.</summary>
    public AccentSource AccentSource { get; set; } = AccentSource.Wallpaper;

    public const int MinWidgetSize = 75;
    public const int MaxWidgetSize = 150;

    /// <summary>The info line's size, in percent of its standard size (32 DIP tall).</summary>
    public int InfoLineSize { get; set; } = 100;

    /// <summary>The status ring's size, in percent of its standard size (32 DIP tall).</summary>
    public int DuoRingSize { get; set; } = 100;

    /// <summary>Dynaland's size, in percent: the pill and every page it opens.</summary>
    public int DynalandSize { get; set; } = 100;

    public const int MinDynalandWidth = 360;
    public const int MaxDynalandWidth = 560;

    /// <summary>Width of Dynaland's open pages (player, general page), DIP at 100% size.</summary>
    public int DynalandWidth { get; set; } = 400;

    /// <summary>
    /// Keeps a strip at the top of the screen for widgets in the top corners (an AppBar of its
    /// own), so maximised windows start below them. On by default.
    /// </summary>
    public bool ReserveTopStrip { get; set; } = true;

    /// <summary>
    /// Machitan's language: empty for Windows' display language, else a language code ("en",
    /// "id"). Read when Machitan starts.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Machitan's shortcuts also while an app running as administrator is in front (a helper
    /// runs as administrator; Windows asks once when it is turned on). Off by default.
    /// </summary>
    public bool AdminAppShortcuts { get; set; }

    /// <summary>The Machitan.exe the admin-keys task was made for (another copy needs it made again).</summary>
    public string AdminAppShortcutsFor { get; set; } = string.Empty;

    /// <summary>Calendar flyout (Win+N): show seconds on its clock.</summary>
    public bool CalendarShowSeconds { get; set; }

    /// <summary>Calendar flyout (Win+N): the month calendar is folded away (set from the flyout itself).</summary>
    public bool CalendarCollapsed { get; set; }

    // Start's power menu. Restart and Shut down were always there; the rest are new and off.

    /// <summary>Power menu: Sleep (shown only when this PC can sleep).</summary>
    public bool PowerSleep { get; set; }

    /// <summary>Power menu: Hibernate (shown only when hibernation is turned on).</summary>
    public bool PowerHibernate { get; set; }

    public bool PowerRestart { get; set; } = true;

    public bool PowerShutDown { get; set; } = true;

    /// <summary>Power menu: Update and restart (shown only while Windows has an update waiting).</summary>
    public bool PowerUpdateRestart { get; set; }

    /// <summary>Power menu: Update and shut down (shown only while Windows has an update waiting).</summary>
    public bool PowerUpdateShutDown { get; set; }

    // Start's buttons next to the power button, as in Windows' Start > Folders. All off.

    /// <summary>The folder buttons' ids, in their default order (left to right, the power button after the last).</summary>
    public static readonly string[] FolderIds =
    {
        "Settings", "Explorer", "Documents", "Downloads", "Music", "Pictures", "Videos", "Network", "Personal", "Machitan",
    };

    /// <summary>The folder buttons' order: their ids, comma separated (see FolderIds).</summary>
    public string FolderOrder { get; set; } = string.Join(",", FolderIds);

    public bool FolderSettings { get; set; }

    public bool FolderExplorer { get; set; }

    public bool FolderDocuments { get; set; }

    public bool FolderDownloads { get; set; }

    public bool FolderMusic { get; set; }

    public bool FolderPictures { get; set; }

    public bool FolderVideos { get; set; }

    public bool FolderNetwork { get; set; }

    public bool FolderPersonal { get; set; }

    /// <summary>A button for Machitan's own settings.</summary>
    public bool FolderMachitan { get; set; }

    /// <summary>
    /// Start's All apps begins with the apps opened most from Start, counted by Machitan on this
    /// PC (see AppUsage). Off by default; turning it off deletes the counts.
    /// </summary>
    public bool StartMostUsed { get; set; }

    /// <summary>Start's Most used list is folded away (set from Start itself).</summary>
    public bool StartMostUsedCollapsed { get; set; }

    /// <summary>The Start button shows the user's own pictures instead of Machitan's logo. Off by default.</summary>
    public bool CustomStartButton { get; set; }

    /// <summary>Full path of the Start button's picture at rest; empty for none (the logo stays).</summary>
    public string StartButtonPicture { get; set; } = string.Empty;

    /// <summary>Full path of its picture while the pointer is on it; empty: the picture at rest.</summary>
    public string StartButtonPictureHover { get; set; } = string.Empty;

    /// <summary>Full path of its picture while it is pressed; empty: the hover picture, else the one at rest.</summary>
    public string StartButtonPicturePressed { get; set; } = string.Empty;

    /// <summary>Longest picture path kept (longer ones are dropped as broken).</summary>
    public const int MaxPicturePath = 2048;

    /// <summary>The folder order as a list: every known id once, unknown ones dropped, missing ones added at their default place.</summary>
    public static List<string> ParseFolderOrder(string? order)
    {
        var ids = new List<string>();
        foreach (string part in (order ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string? known = Array.Find(FolderIds, id => string.Equals(id, part, StringComparison.OrdinalIgnoreCase));
            if (known is not null && !ids.Contains(known))
            {
                ids.Add(known);
            }
        }

        for (int i = 0; i < FolderIds.Length; i++)
        {
            if (ids.Contains(FolderIds[i]))
            {
                continue;
            }

            // After the one that comes before it by default, so a new button lands where it would be.
            int after = i == 0 ? -1 : ids.IndexOf(FolderIds[i - 1]);
            ids.Insert(after + 1, FolderIds[i]);
        }

        return ids;
    }

    /// <summary>A picture path as kept: trimmed; empty when missing, too long or not a full path.</summary>
    private static string CleanPicturePath(string? path)
    {
        string clean = (path ?? string.Empty).Trim();
        if (clean.Length == 0 || clean.Length > MaxPicturePath)
        {
            return string.Empty;
        }

        return System.IO.Path.IsPathFullyQualified(clean) ? clean : string.Empty;
    }

    /// <summary>Pulls every value back into its allowed range.</summary>
    public void Normalize()
    {
        BarSize = Math.Clamp(BarSize, MinBarSize, MaxBarSize);
        IconSize = Math.Clamp(IconSize, MinIconSize, MaxIconSize);

        if (double.IsNaN(Opacity) || double.IsInfinity(Opacity))
        {
            Opacity = 0.9;
        }

        Opacity = Math.Clamp(Opacity, MinOpacity, MaxOpacity);

        if (!Enum.IsDefined(Theme))
        {
            Theme = ThemeMode.Dark;
        }

        if (!Enum.IsDefined(TaskbarMode))
        {
            TaskbarMode = TaskbarMode.Segmented;
        }

        if (!Enum.IsDefined(TaskbarSplit))
        {
            TaskbarSplit = TaskbarSplit.Off;
        }

        if (!Enum.IsDefined(PreviewStyle))
        {
            PreviewStyle = PreviewStyle.Thumbnails;
        }

        FolderOrder = string.Join(",", ParseFolderOrder(FolderOrder));

        if (!Enum.IsDefined(DockEdge))
        {
            DockEdge = DockEdge.Bottom;
        }

        if (!Enum.IsDefined(DockAlignment))
        {
            DockAlignment = DockAlignment.Center;
        }

        if (!Enum.IsDefined(SearchMode))
        {
            SearchMode = SearchMode.Icon;
        }

        if (!Enum.IsDefined(SearchPlace))
        {
            SearchPlace = SearchPlace.WithStart;
        }

        if (!Enum.IsDefined(InfoLineCorner))
        {
            InfoLineCorner = ScreenCorner.TopLeft;
        }

        if (!Enum.IsDefined(DuoRingCorner))
        {
            DuoRingCorner = ScreenCorner.TopRight;
        }

        if (!Enum.IsDefined(DynalandStyle))
        {
            DynalandStyle = global::Machitan.Config.DynalandStyle.Notch;
        }

        if (!Enum.IsDefined(DynalandPosition))
        {
            DynalandPosition = global::Machitan.Config.DynalandPosition.Auto;
        }

        if (!Enum.IsDefined(DynalandNotchShape))
        {
            DynalandNotchShape = global::Machitan.Config.DynalandNotchShape.Default;
        }

        if (!Enum.IsDefined(TopStripCoverColour))
        {
            TopStripCoverColour = StripCoverColour.Dark;
        }

        if (double.IsNaN(TopStripCoverOpacity) || double.IsInfinity(TopStripCoverOpacity))
        {
            TopStripCoverOpacity = 0.6;
        }

        TopStripCoverOpacity = Math.Clamp(TopStripCoverOpacity, MinCoverOpacity, MaxCoverOpacity);

        if (!Enum.IsDefined(AccentSource))
        {
            AccentSource = AccentSource.Wallpaper;
        }

        InfoLineSize = Math.Clamp(InfoLineSize, MinWidgetSize, MaxWidgetSize);
        DuoRingSize = Math.Clamp(DuoRingSize, MinWidgetSize, MaxWidgetSize);
        DynalandSize = Math.Clamp(DynalandSize, MinWidgetSize, MaxWidgetSize);
        DynalandWidth = Math.Clamp(DynalandWidth, MinDynalandWidth, MaxDynalandWidth);
        DynalandTransparency = Math.Clamp(DynalandTransparency, 0, MaxDynalandTransparency);
        DuoRingLowBattery = Math.Clamp(DuoRingLowBattery, MinRingLowBattery, MaxRingLowBattery);
        DuoRingTransparency = Math.Clamp(DuoRingTransparency, 0, 100);

        StartButtonPicture = CleanPicturePath(StartButtonPicture);
        StartButtonPictureHover = CleanPicturePath(StartButtonPictureHover);
        StartButtonPicturePressed = CleanPicturePath(StartButtonPicturePressed);

        WeatherPlaceName = (WeatherPlaceName ?? string.Empty).Trim();
        if (WeatherPlaceName.Length > 200)
        {
            WeatherPlaceName = WeatherPlaceName[..200];
        }

        bool latitudeOk = WeatherLatitude is double latitude && double.IsFinite(latitude) && latitude is >= -90 and <= 90;
        bool longitudeOk = WeatherLongitude is double longitude && double.IsFinite(longitude) && longitude is >= -180 and <= 180;
        if (!latitudeOk || !longitudeOk)
        {
            // A place needs both; half a place is no place.
            WeatherLatitude = null;
            WeatherLongitude = null;
        }
    }
}
