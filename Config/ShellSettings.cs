using System;

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

    /// <summary>Windows' accent colour.</summary>
    Accent = 2,
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

    public DockEdge DockEdge { get; set; } = DockEdge.Bottom;

    public DockAlignment DockAlignment { get; set; } = DockAlignment.Center;

    public int BarSize { get; set; } = 64;

    public int IconSize { get; set; } = 24;

    public double Opacity { get; set; } = 0.9;

    public bool EnableAcrylic { get; set; } = true;

    public SearchMode SearchMode { get; set; } = SearchMode.Icon;

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

    /// <summary>A colour over the top strip (only while the strip is kept). Off by default.</summary>
    public bool TopStripCover { get; set; }

    public StripCoverColour TopStripCoverColour { get; set; } = StripCoverColour.Dark;

    public const double MinCoverOpacity = 0.1;
    public const double MaxCoverOpacity = 1.0;

    /// <summary>How solid the cover is: 0.1 (faint) to 1 (solid).</summary>
    public double TopStripCoverOpacity { get; set; } = 0.6;

    public ScreenCorner DuoRingCorner { get; set; } = ScreenCorner.TopRight;

    /// <summary>
    /// Keeps a strip at the top of the screen for widgets in the top corners (an AppBar of its
    /// own), so maximised windows start below them. On by default.
    /// </summary>
    public bool ReserveTopStrip { get; set; } = true;

    /// <summary>Calendar flyout (Win+N): show seconds on its clock.</summary>
    public bool CalendarShowSeconds { get; set; }

    /// <summary>Calendar flyout (Win+N): the month calendar is folded away (set from the flyout itself).</summary>
    public bool CalendarCollapsed { get; set; }

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

        if (!Enum.IsDefined(TopStripCoverColour))
        {
            TopStripCoverColour = StripCoverColour.Dark;
        }

        if (double.IsNaN(TopStripCoverOpacity) || double.IsInfinity(TopStripCoverOpacity))
        {
            TopStripCoverOpacity = 0.6;
        }

        TopStripCoverOpacity = Math.Clamp(TopStripCoverOpacity, MinCoverOpacity, MaxCoverOpacity);

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
