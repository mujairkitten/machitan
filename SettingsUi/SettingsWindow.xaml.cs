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
using VirtualKey = Windows.System.VirtualKey;

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
                "Icon or a search box next to Start, or no search button. The icon opens Windows' own search (Win+S). In the box you type right on the bar, and the results show next to it (apps, or files: for files).",
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
        AddNote("Both stay on top and step aside while a full-screen app is in front. Widgets in the same corner stand side by side; on the bar's edge they sit beside the bar, in line with it.");
        AddCard(
            ToggleRow(
                "Info line",
                "One line with date, time and weather in a screen corner. A click opens Windows' own notification centre.",
                s => s.InfoLineEnabled,
                (s, on) => s.InfoLineEnabled = on),
            ToggleRow("Show date", "Part of the info line.", s => s.InfoLineShowDate, (s, on) => s.InfoLineShowDate = on),
            ToggleRow("Show time", "Part of the info line.", s => s.InfoLineShowTime, (s, on) => s.InfoLineShowTime = on),
            ToggleRow(
                "Show seconds",
                "The info line's time shows seconds too.",
                s => s.InfoLineShowSeconds,
                (s, on) => s.InfoLineShowSeconds = on),
            ToggleRow(
                "Show weather",
                "Part of the info line, from Open-Meteo; hidden when there is no recent reading. Uses your approximate location; see Privacy below.",
                s => s.InfoLineShowWeather,
                (s, on) => s.InfoLineShowWeather = on),
            ComboRow(
                "Weather location",
                "Windows' location, or a place you choose below.",
                new[] { "Windows' location", "A place I choose" },
                s => s.WeatherUseManualPlace ? 1 : 0,
                (s, i) => s.WeatherUseManualPlace = i == 1),
            WeatherPlaceRow(),
            CornerRow("Info line corner", s => s.InfoLineCorner, (s, c) => s.InfoLineCorner = c),
            ToggleRow(
                "Status ring",
                "Battery, Wi-Fi and volume in a screen corner. Its colours follow the wallpaper behind it, with a soft backing where the wallpaper is too busy. A click opens Windows' own quick settings.",
                s => s.ShowDuoRing,
                (s, on) => s.ShowDuoRing = on),
            CornerRow("Status ring corner", s => s.DuoRingCorner, (s, c) => s.DuoRingCorner = c),
            ToggleRow(
                "Keep the free edge clear",
                "Keeps a strip at the screen edge without the bar (the top while the bar is at the bottom, and the other way round) for the widgets and Dynaland there, so maximised windows stay clear of them.",
                s => s.ReserveTopStrip,
                (s, on) => s.ReserveTopStrip = on),
            ToggleRow(
                "Cover the strip",
                "Fills the strip at the free edge with one colour, like a menu bar. Shows only while the strip is kept, and steps aside for full-screen apps.",
                s => s.TopStripCover,
                (s, on) => s.TopStripCover = on),
            ComboRow(
                "Cover colour",
                "Dark, light, or Windows' accent colour.",
                new[] { "Dark", "Light", "Accent colour" },
                s => (int)s.TopStripCoverColour,
                (s, i) => s.TopStripCoverColour = (StripCoverColour)i),
            SliderRow(
                "Cover opacity",
                "How solid the cover is: 100% hides what is behind it.",
                ShellSettings.MinCoverOpacity * 100,
                ShellSettings.MaxCoverOpacity * 100,
                5,
                s => s.TopStripCoverOpacity * 100,
                (s, v) => s.TopStripCoverOpacity = Math.Round(v) / 100.0,
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"));

        AddSection("Dynaland");
        AddCard(
            ToggleRow(
                "Dynaland",
                "A black pill at the top or bottom of the screen. Small and quiet when nothing is happening; it shows what is playing (from Windows' media controls) and the volume when it changes. Hover it to open: the player first, and an arrow to the page with what is using the camera or mic.",
                s => s.EnableDynaland,
                (s, on) => s.EnableDynaland = on),
            ComboRow(
                "Position",
                "Opposite the bar (the top while the bar is at the bottom, and the other way round), or always the top or the bottom. On the bar's own edge it floats just past the bar when the bar is in the middle.",
                new[] { "Opposite the bar", "Top", "Bottom" },
                s => (int)s.DynalandPosition,
                (s, i) => s.DynalandPosition = (DynalandPosition)i),
            ComboRow(
                "Shape",
                "Notch hangs from the screen's edge, its sides curving out into it. Island floats just off the edge, round all around, in line with the corner widgets.",
                new[] { "Notch", "Island" },
                s => (int)s.DynalandStyle,
                (s, i) => s.DynalandStyle = (DynalandStyle)i),
            ToggleRow(
                "Camera and mic dots",
                "A green dot while an app uses the camera, an orange one for the mic, and a short look at which app when it starts.",
                s => s.ShowPrivacyDots,
                (s, on) => s.ShowPrivacyDots = on));

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
                "When the info line's weather is on, Machitan asks Windows for this PC's location (at most once an hour), rounds it to about 1 km, and sends that to Open-Meteo (open-meteo.com) every 15 minutes to get the current weather. If Windows does not allow desktop apps to use your location, the weather stays hidden. With a place you chose, Windows' location is not used: that place's coordinates (rounded to about 1 km) are sent instead, and finding a place sends the name you typed to Open-Meteo's place search. Nothing else leaves this PC."),
            TextRow(
                "Status ring",
                "Battery, network and volume are read from Windows on this PC. The Wi-Fi name comes from Windows' network list, so the ring does not need location permission."),
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

    /// <summary>
    /// The chosen weather place: type a city (or "latitude, longitude") and press Find; the
    /// first match becomes the place and the weather switches to it. Below: what is used now.
    /// </summary>
    private Grid WeatherPlaceRow()
    {
        var box = new TextBox
        {
            PlaceholderText = "A city, or latitude, longitude",
            Width = 280,
            Text = _host.Settings.Current.WeatherPlaceName,
        };
        AutomationProperties.SetName(box, "Weather place");

        var find = new Button { Content = "Find", CornerRadius = new CornerRadius(8) };
        AutomationProperties.SetName(find, "Find this place and use it for the weather");

        var status = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(status, "Weather place in use");

        void Refresh() => status.Text = DescribeWeatherPlace(_host.Settings.Current);
        Refresh();
        _refreshers.Add(Refresh);

        async void FindPlace()
        {
            string query = box.Text;
            find.IsEnabled = false;
            status.Text = "Looking up \"" + query.Trim() + "\"...";
            try
            {
                (WeatherPlace? place, string problem) = await WeatherService.FindPlaceAsync(query);
                if (place is WeatherPlace found)
                {
                    _host.Settings.Update(s =>
                    {
                        s.WeatherUseManualPlace = true;
                        s.WeatherPlaceName = found.Name;
                        s.WeatherLatitude = found.Latitude;
                        s.WeatherLongitude = found.Longitude;
                    });
                    box.Text = found.Name;
                    Refresh();
                }
                else
                {
                    status.Text = problem;
                }
            }
            catch (Exception ex)
            {
                status.Text = "The place search did not work.";
                Log.Error("Settings: the weather place search failed.", ex);
            }
            finally
            {
                find.IsEnabled = true;
            }
        }

        find.Click += (_, _) => FindPlace();
        // Seen even if the text box keeps Enter for itself.
        box.AddHandler(
            UIElement.KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
            {
                if (e.Key == VirtualKey.Enter && find.IsEnabled)
                {
                    e.Handled = true;
                    FindPlace();
                }
            }),
            true);

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(box);
        line.Children.Add(find);

        var control = new StackPanel { Spacing = 6 };
        control.Children.Add(line);
        control.Children.Add(status);
        return Row(
            "Weather place",
            "Type a city and press Find (or Enter). The first match is used, and the weather switches to it.",
            control,
            stacked: true);
    }

    private static string DescribeWeatherPlace(ShellSettings settings)
    {
        bool hasPlace = settings.WeatherLatitude is not null && settings.WeatherLongitude is not null;
        if (!settings.WeatherUseManualPlace)
        {
            return hasPlace
                ? "In use: Windows' location. Chosen place kept for later: " + settings.WeatherPlaceName + "."
                : "In use: Windows' location.";
        }

        if (!hasPlace)
        {
            return "In use: no place chosen yet, so the weather stays hidden. Find one above, or switch back to Windows' location.";
        }

        return "In use: " + settings.WeatherPlaceName + " ("
            + settings.WeatherLatitude!.Value.ToString("0.00", CultureInfo.InvariantCulture) + ", "
            + settings.WeatherLongitude!.Value.ToString("0.00", CultureInfo.InvariantCulture) + ").";
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
