using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Machitan.SettingsUi;

/// <summary>
/// Machitan's Settings window. Every change applies at once (sliders after a short pause) and
/// is saved to settings.json. Options for parts that are not built yet are shown, saved, and
/// clearly labelled as such, so nothing pretends to work.
/// Every control carries an accessible name.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const double CardRadius = 10;
    private const int WidthDip = 760;
    private const int HeightDip = 820;

    private readonly ShellHost _host;
    private readonly DispatcherQueueTimer _sliderTimer;
    private readonly List<Action> _refreshers = new();
    private Action<ShellSettings>? _pendingSliderChange;
    private TextBlock? _statusText;
    private bool _refreshing;
    private bool _builtDark;

    internal SettingsWindow(ShellHost host)
    {
        _host = host;
        InitializeComponent();

        _sliderTimer = DispatcherQueue.CreateTimer();
        _sliderTimer.Interval = TimeSpan.FromMilliseconds(250);
        _sliderTimer.IsRepeating = false;
        _sliderTimer.Tick += (_, _) => FlushSlider();

        BuildSections();
        ApplyTheme();
        ResizeToDip();

        _host.Settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            FlushSlider();
            _host.Settings.Changed -= OnSettingsChanged;
        };
    }

    /// <summary>Called when the window is opened again while it already exists.</summary>
    internal void RefreshStatus()
    {
        if (_statusText is not null)
        {
            _statusText.Text = _host.SystemStatus;
        }
    }

    // ---------------------------------------------------------------- sections

    private void BuildSections()
    {
        _builtDark = IsDark();
        AddTitle("Settings");

        AddSection("Bar");
        AddCard(
            ComboRow(
                "Taskbar mode",
                "Segmented: separate pills. Joined: one pill. Classic: full width. Compact is not built yet and shows as Segmented.",
                new[] { "Segmented", "Joined", "Classic", "Compact" },
                s => (int)s.TaskbarMode,
                (s, i) => s.TaskbarMode = (TaskbarMode)i),
            ComboRow(
                "Position",
                "Which screen edge the bar sits on.",
                new[] { "Bottom", "Top" },
                s => (int)s.DockEdge,
                (s, i) => s.DockEdge = (DockEdge)i),
            ComboRow(
                "Alignment",
                "Where the bar sits along the edge. In Classic mode it places the app dock inside the full-width bar.",
                new[] { "Left", "Center", "Right" },
                s => (int)s.DockAlignment,
                (s, i) => s.DockAlignment = (DockAlignment)i),
            SliderRow(
                "Bar size",
                "Height of the bar strip, in DIP.",
                ShellSettings.MinBarSize,
                ShellSettings.MaxBarSize,
                2,
                s => s.BarSize,
                (s, v) => s.BarSize = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)),
            SliderRow(
                "Icon size",
                "Size of the app icons, in DIP.",
                ShellSettings.MinIconSize,
                ShellSettings.MaxIconSize,
                2,
                s => s.IconSize,
                (s, v) => s.IconSize = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)),
            ToggleRow(
                "Auto-hide",
                "The bar slides away when the pointer leaves it and returns at the screen edge. Off: the bar keeps its strip of the screen.",
                s => s.AutoHideBar,
                (s, on) => s.AutoHideBar = on),
            ComboRow(
                "Search",
                "Icon or a search box next to Start, or no search button. Both open Start with the search box ready.",
                new[] { "Icon", "Box", "Hidden" },
                s => (int)s.SearchMode,
                (s, i) => s.SearchMode = (SearchMode)i));

        AddSection("Look");
        AddCard(
            ComboRow(
                "Theme",
                "Dark, light, or follow Windows.",
                new[] { "Dark", "Light", "System" },
                s => (int)s.Theme,
                (s, i) => s.Theme = (ThemeMode)i),
            ToggleRow(
                "Acrylic",
                "Blur what is behind the bar and the Start menu. If it shows as a solid dark colour, Windows is not providing the blur on this PC.",
                s => s.EnableAcrylic,
                (s, on) => s.EnableAcrylic = on),
            SliderRow(
                "Opacity",
                "How strongly the bar and Start menu are tinted: 100% is solid.",
                ShellSettings.MinOpacity * 100,
                ShellSettings.MaxOpacity * 100,
                5,
                s => s.Opacity * 100,
                (s, v) => s.Opacity = Math.Round(v) / 100.0,
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"));

        AddSection("Calendar (Win+N)");
        AddCard(
            ToggleRow(
                "Show seconds",
                "The calendar's clock shows seconds too.",
                s => s.CalendarShowSeconds,
                (s, on) => s.CalendarShowSeconds = on));

        AddSection("Corner widgets");
        AddNote("Not built yet: the corner widgets arrive in a later batch. These choices are saved and will apply then.");
        AddCard(
            ToggleRow(
                "Info line",
                "One line with date, time and weather in a screen corner. A click opens Windows' own notification centre.",
                s => s.InfoLineEnabled,
                (s, on) => s.InfoLineEnabled = on),
            ToggleRow("Show date", "Part of the info line.", s => s.InfoLineShowDate, (s, on) => s.InfoLineShowDate = on),
            ToggleRow("Show time", "Part of the info line.", s => s.InfoLineShowTime, (s, on) => s.InfoLineShowTime = on),
            ToggleRow(
                "Show weather",
                "Part of the info line. Uses your approximate location; see Privacy below.",
                s => s.InfoLineShowWeather,
                (s, on) => s.InfoLineShowWeather = on),
            CornerRow("Info line corner", s => s.InfoLineCorner, (s, c) => s.InfoLineCorner = c),
            ToggleRow(
                "Status ring",
                "Battery, Wi-Fi and volume in a screen corner. A click opens Windows' own quick settings.",
                s => s.DuoRingEnabled,
                (s, on) => s.DuoRingEnabled = on),
            CornerRow("Status ring corner", s => s.DuoRingCorner, (s, c) => s.DuoRingCorner = c));

        AddSection("Dynaland");
        AddNote("Not built yet: Dynaland arrives in a later batch. This choice is saved and will apply then.");
        AddCard(
            ToggleRow(
                "Dynaland",
                "A pill at the top edge with the playing music and the clock.",
                s => s.EnableDynaland,
                (s, on) => s.EnableDynaland = on));

        AddSection("Other options from the plan");
        AddNote("Not built yet. These stay off and cannot be changed until the parts that use them exist.");
        AddCard(
            DisabledToggleRow("System stats", s => s.ShowSystemStats),
            DisabledToggleRow("Focused app name", s => s.ShowFocusedAppName),
            DisabledToggleRow("Recent items", s => s.ShowRecentItems));

        AddSection("Privacy");
        AddCard(
            TextRow(
                "Weather",
                "When the info line's weather is on (from a later batch), Machitan sends your approximate location to Open-Meteo (open-meteo.com) to get the forecast. Nothing else leaves this PC."),
            TextRow(
                "Diagnostics log",
                "Machitan keeps a log for troubleshooting at " + AppPaths.LogFile
                + ". It can contain app names, file paths and window titles. It stays on this PC unless you share it."),
            ButtonRow("Open Machitan's folder", "Settings, pins and the log.", OpenDataFolder));

        AddSection("System status");
        _statusText = new TextBlock { Text = _host.SystemStatus, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(_statusText, "System status");
        AddCard(Row("Hotkeys", "Whether Machitan owns the Windows key and its shortcuts.", _statusText, stacked: true));

        AddSection("About");
        AddCard(TextRow(
            "Machitan " + typeof(SettingsWindow).Assembly.GetName().Version,
            "Free software under the GNU GPL, version 3. Windows build "
            + Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture) + "."));
    }

    // ---------------------------------------------------------------- building blocks

    private void AddTitle(string text)
    {
        var title = new TextBlock { Text = text, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        Sections.Children.Add(title);
    }

    private void AddSection(string text)
    {
        var header = new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 4) };
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
        Sections.Children.Add(header);
    }

    private void AddNote(string text)
    {
        Sections.Children.Add(new TextBlock { Text = text, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
    }

    /// <summary>A card: rows on a subtle rounded background, separated by thin lines.</summary>
    private void AddCard(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(IsDark() ? ColorHelper.FromArgb(30, 255, 255, 255) : ColorHelper.FromArgb(30, 0, 0, 0)),
                });
            }

            stack.Children.Add(rows[i]);
        }

        // Windows 11 card colours, picked for Machitan's theme setting (not the system's).
        bool dark = IsDark();
        var card = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(CardRadius),
            Padding = new Thickness(16, 4, 16, 4),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(dark ? ColorHelper.FromArgb(13, 255, 255, 255) : ColorHelper.FromArgb(179, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(dark ? ColorHelper.FromArgb(25, 0, 0, 0) : ColorHelper.FromArgb(15, 0, 0, 0)),
        };
        Sections.Children.Add(card);
    }

    /// <summary>Label and description on the left, the control on the right (or below when stacked).</summary>
    private static Grid Row(string label, string description, FrameworkElement control, bool stacked = false)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
        {
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        }

        var grid = new Grid { Padding = new Thickness(0, 12, 0, 12), ColumnSpacing = 24, RowSpacing = 8 };
        if (stacked)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(control, 1);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(control, 1);
            control.VerticalAlignment = VerticalAlignment.Center;
        }

        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private Grid ToggleRow(string label, string description, Func<ShellSettings, bool> read, Action<ShellSettings, bool> write)
    {
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        AutomationProperties.SetName(toggle, label);

        void Refresh() => toggle.IsOn = read(_host.Settings.Current);
        Refresh();
        _refreshers.Add(Refresh);

        toggle.Toggled += (_, _) =>
        {
            if (!_refreshing)
            {
                bool on = toggle.IsOn;
                _host.Settings.Update(s => write(s, on));
            }
        };

        return Row(label, description, toggle);
    }

    private Grid DisabledToggleRow(string label, Func<ShellSettings, bool> read)
    {
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0, IsEnabled = false, IsOn = read(_host.Settings.Current) };
        AutomationProperties.SetName(toggle, label + " (not built yet)");
        return Row(label, "Not built yet.", toggle);
    }

    private Grid ComboRow(string label, string description, string[] choices, Func<ShellSettings, int> read, Action<ShellSettings, int> write)
    {
        var combo = new ComboBox { MinWidth = 160 };
        foreach (string choice in choices)
        {
            combo.Items.Add(choice);
        }

        AutomationProperties.SetName(combo, label);

        void Refresh() => combo.SelectedIndex = Math.Clamp(read(_host.Settings.Current), 0, choices.Length - 1);
        Refresh();
        _refreshers.Add(Refresh);

        combo.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && combo.SelectedIndex >= 0)
            {
                int index = combo.SelectedIndex;
                _host.Settings.Update(s => write(s, index));
            }
        };

        return Row(label, description, combo);
    }

    private Grid CornerRow(string label, Func<ShellSettings, ScreenCorner> read, Action<ShellSettings, ScreenCorner> write)
    {
        return ComboRow(
            label,
            string.Empty,
            new[] { "Top left", "Top right", "Bottom left", "Bottom right" },
            s => (int)read(s),
            (s, i) => write(s, (ScreenCorner)i));
    }

    /// <summary>A slider applies after a short pause, so dragging does not rebuild the bar on every step.</summary>
    private Grid SliderRow(
        string label,
        string description,
        double minimum,
        double maximum,
        double step,
        Func<ShellSettings, double> read,
        Action<ShellSettings, double> write,
        Func<double, string> format)
    {
        var value = new TextBlock { MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = step,
            SmallChange = step,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, label);

        void Refresh()
        {
            slider.Value = read(_host.Settings.Current);
            value.Text = format(slider.Value);
        }

        Refresh();
        _refreshers.Add(Refresh);

        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            if (_refreshing)
            {
                return;
            }

            double newValue = e.NewValue;
            _pendingSliderChange = s => write(s, newValue);
            _sliderTimer.Stop();
            _sliderTimer.Start();
        };

        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        control.Children.Add(slider);
        control.Children.Add(value);
        return Row(label, description, control);
    }

    private static Grid TextRow(string label, string description)
    {
        return Row(label, description, new Border());
    }

    private static Grid ButtonRow(string label, string description, Action onClick)
    {
        var button = new Button { Content = label, CornerRadius = new CornerRadius(8) };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return Row(label, description, button);
    }

    // ---------------------------------------------------------------- behaviour

    private void FlushSlider()
    {
        _sliderTimer.Stop();
        Action<ShellSettings>? change = _pendingSliderChange;
        _pendingSliderChange = null;
        if (change is not null)
        {
            _host.Settings.Update(change);
        }
    }

    /// <summary>Settings can also change by hand in settings.json; keep the controls in step.</summary>
    private void OnSettingsChanged()
    {
        _refreshing = true;
        try
        {
            if (_builtDark != IsDark())
            {
                // Card colours depend on the theme: rebuild, keeping the scroll position.
                double offset = Scroller.VerticalOffset;
                Sections.Children.Clear();
                _refreshers.Clear();
                BuildSections();
                Scroller.ChangeView(null, offset, null, true);
            }
            else
            {
                foreach (Action refresh in _refreshers)
                {
                    refresh();
                }
            }

            ApplyTheme();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _host.Settings.Current.Theme switch
        {
            ThemeMode.Dark => ElementTheme.Dark,
            ThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    private static void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureDataFolder();
            using Process? explorer = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.DataFolder + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Machitan's folder could not be opened.", ex);
        }
    }

    private bool IsDark()
    {
        return _host.Settings.Current.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
    }

    /// <summary>AppWindow sizes are physical pixels.</summary>
    private void ResizeToDip()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = User32.GetDpiForWindow(hwnd);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        AppWindow.Resize(new SizeInt32((int)Math.Round(WidthDip * scale), (int)Math.Round(HeightDip * scale)));
    }
}
