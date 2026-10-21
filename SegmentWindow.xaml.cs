using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Machitan.Native;

namespace Machitan;

public enum SegmentKind
{
    Widgets,
    Tray,
}

/// <summary>
/// One floating segment of the Default taskbar mode (the Addy "Dynamic
/// Taskbar"): a Widgets capsule on the left, a tray segment on the right,
/// flanking the main window, which carries the center Start + apps segment.
/// In Joined/Classic/Compact modes these windows stay hidden and the main
/// window shows the same clusters inside its single pill.
/// </summary>
public sealed partial class SegmentWindow : Window
{
    private const double WidgetsWidthWithTextDip = 150;
    private const double WidgetsWidthIconOnlyDip = 58;
    private const double TrayBaseWidthDip = 284;
    private const double TrayBatteryExtraDip = 64;

    private readonly SegmentKind _kind;
    private MachitanSettings _settings;
    private readonly IntPtr _hwnd;
    private bool _weatherTextVisible;
    private bool _batteryVisible;

    public SegmentWindow(SegmentKind kind, MachitanSettings settings)
    {
        _kind = kind;
        _settings = settings.Clone();
        this.InitializeComponent();

        if (kind == SegmentKind.Widgets)
            TrayGrid.Visibility = Visibility.Collapsed;
        else
            WidgetsGrid.Visibility = Visibility.Collapsed;

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowChrome.MakeBorderless(_hwnd, topmost: true);
        WindowChrome.SetCornerPreference(_hwnd, NativeMethods.DWMWCP_ROUND);

        try
        {
            AppWindow.IsShownInSwitchers = false;
        }
        catch
        {
            // Older Windows builds.
        }

        ApplySettings(_settings);
    }

    public IntPtr Hwnd => _hwnd;

    public double WidthDip => _kind == SegmentKind.Widgets
        ? (_weatherTextVisible ? WidgetsWidthWithTextDip : WidgetsWidthIconOnlyDip)
        : TrayBaseWidthDip + (_batteryVisible ? TrayBatteryExtraDip : 0);

    public void ApplySettings(MachitanSettings settings)
    {
        _settings = settings.Clone();
        RootGrid.RequestedTheme = _settings.Theme == ShellTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

        if (_settings.EnableAcrylic)
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            byte alpha = (byte)Math.Clamp(Math.Round(180 * _settings.Opacity), 0, 255);
            var tint = _settings.Theme == ShellTheme.Dark
                ? new Windows.UI.Color { A = alpha, R = 21, G = 24, B = 28 }
                : new Windows.UI.Color { A = alpha, R = 243, G = 243, B = 243 };
            SegmentBorder.Background = new SolidColorBrush(tint);
        }
        else
        {
            SystemBackdrop = null;
            var solid = _settings.Theme == ShellTheme.Dark
                ? new Windows.UI.Color { A = 255, R = 32, G = 32, B = 32 }
                : new Windows.UI.Color { A = 255, R = 243, G = 243, B = 243 };
            SegmentBorder.Background = new SolidColorBrush(solid);
        }
    }

    public void SetWeather(WeatherInfo info)
    {
        WeatherIcon.Glyph = info.Glyph;
        WeatherText.Text = info.TemperatureText;
        WeatherConditionText.Text = info.ConditionLabel;
        WeatherTextStack.Visibility = Visibility.Visible;
        _weatherTextVisible = true;
    }

    public void SetBattery(int percent, bool charging, bool hasBattery)
    {
        _batteryVisible = hasBattery;
        if (!hasBattery)
        {
            BatteryButton.Visibility = Visibility.Collapsed;
            return;
        }

        BatteryButton.Visibility = Visibility.Visible;
        int bucket = percent < 0 ? 5 : Math.Clamp(percent / 10, 0, 10);
        BatteryIcon.Glyph = ((char)(0xE850 + bucket)).ToString();
        BatteryText.Text = percent >= 0 ? $"{percent}%" : "";
        ToolTipService.SetToolTip(BatteryButton,
            percent >= 0
                ? (charging ? $"Battery {percent}% (charging)" : $"Battery {percent}%")
                : "Battery");
    }

    public void SetClock(DateTime now)
    {
        ClockText.Text = now.ToString("t");
        DateText.Text = now.ToString("ddd d MMM");
    }

    private void QuickSettings_Click(object sender, RoutedEventArgs e) => ShellPanels.OpenQuickSettings();

    private void Calendar_Click(object sender, RoutedEventArgs e) => ShellPanels.OpenNotifications();

    private void Notification_Click(object sender, RoutedEventArgs e) => ShellPanels.OpenNotifications();

    private void Settings_Click(object sender, RoutedEventArgs e) => App.MainWindow?.OpenSettings();

    private void TaskManager_Click(object sender, RoutedEventArgs e) => App.MainWindow?.ShowTaskManager();

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();
}
