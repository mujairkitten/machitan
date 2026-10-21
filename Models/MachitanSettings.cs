namespace Machitan;

public enum ShellTheme
{
    Light,
    Dark,
}

public enum DockEdge
{
    Top,
    Bottom,
}

public enum DockAlignment
{
    Left,
    Center,
    Right,
}

public enum TaskbarMode
{
    // Numeric values deliberately preserve the old layout (Dock=0,
    // Segmented=1, Classic=2) so existing settings.json files migrate:
    // old Dock users get Joined (the same single pill), and anyone who
    // picked the never-implemented Segmented finally gets the real thing.
    Joined = 0,
    Default = 1,
    Classic = 2,
    Compact = 3,
}

public sealed class MachitanSettings
{
    public ShellTheme Theme { get; set; } = ShellTheme.Light;
    public double Opacity { get; set; } = 0.9;
    public bool EnableAcrylic { get; set; } = true;
    public double BarSize { get; set; } = 72;
    public double IconSize { get; set; } = 28;
    public DockEdge DockEdge { get; set; } = DockEdge.Bottom;
    public DockAlignment DockAlignment { get; set; } = DockAlignment.Center;
    public TaskbarMode TaskbarMode { get; set; } = TaskbarMode.Default;

    public void Normalize()
    {
        if (!Enum.IsDefined(typeof(ShellTheme), Theme))
            Theme = ShellTheme.Light;
        if (!Enum.IsDefined(typeof(DockEdge), DockEdge))
            DockEdge = DockEdge.Bottom;
        if (!Enum.IsDefined(typeof(DockAlignment), DockAlignment))
            DockAlignment = DockAlignment.Center;
        if (!Enum.IsDefined(typeof(TaskbarMode), TaskbarMode))
            TaskbarMode = TaskbarMode.Default;

        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.35, 1) : 0.9;
        BarSize = double.IsFinite(BarSize) ? Math.Clamp(BarSize, 56, 120) : 72;
        IconSize = double.IsFinite(IconSize) ? Math.Clamp(IconSize, 20, 48) : 28;
    }

    public MachitanSettings Clone() => new()
    {
        Theme = Theme,
        Opacity = Opacity,
        EnableAcrylic = EnableAcrylic,
        BarSize = BarSize,
        IconSize = IconSize,
        DockEdge = DockEdge,
        DockAlignment = DockAlignment,
        TaskbarMode = TaskbarMode,
    };
}
