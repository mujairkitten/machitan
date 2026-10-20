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
    Dock,
    Segmented,
    Classic,
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
    public TaskbarMode TaskbarMode { get; set; } = TaskbarMode.Dock;

    public void Normalize()
    {
        if (!Enum.IsDefined(typeof(ShellTheme), Theme))
            Theme = ShellTheme.Light;
        if (!Enum.IsDefined(typeof(DockEdge), DockEdge))
            DockEdge = DockEdge.Bottom;
        if (!Enum.IsDefined(typeof(DockAlignment), DockAlignment))
            DockAlignment = DockAlignment.Center;
        if (!Enum.IsDefined(typeof(TaskbarMode), TaskbarMode))
            TaskbarMode = TaskbarMode.Dock;

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
