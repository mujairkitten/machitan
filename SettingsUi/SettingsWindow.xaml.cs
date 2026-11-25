using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Machitan.SettingsUi;

/// <summary>
/// Machitan's Settings window: a sidebar of categories (as Seelen UI does), one page each.
/// Every change applies at once (sliders after a short pause) and is saved to settings.json.
/// Options for parts that are not built yet sit under About > Planned, off and labelled.
/// Every control carries an accessible name.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const double CardRadius = Radii.Medium;
    private const int WidthDip = 980;
    private const int HeightDip = 820;

    private readonly ShellHost _host;
    private readonly DispatcherQueueTimer _sliderTimer;
    private readonly List<Action> _refreshers = new();
    private Action<ShellSettings>? _pendingSliderChange;
    private TextBlock? _statusText;
    private bool _refreshing;
    private bool _builtDark;

    /// <summary>Redraws About's update row; set while the About page shows.</summary>
    private Action? _updateRefresh;

    internal SettingsWindow(ShellHost host)
    {
        _host = host;
        InitializeComponent();

        // Machitan's title bar (logo and name); Windows keeps the window's buttons.
        Title = Strings.SetWindowTitle;
        AppTitleText.Text = Strings.SetWindowTitle;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppIcon.Apply(AppWindow);

        _sliderTimer = DispatcherQueue.CreateTimer();
        _sliderTimer.Interval = TimeSpan.FromMilliseconds(250);
        _sliderTimer.IsRepeating = false;
        _sliderTimer.Tick += (_, _) => FlushSlider();

        BuildNavigation();
        ApplyTheme();
        ResizeToDip();

        _host.Settings.Changed += OnSettingsChanged;
        _host.Updates.Changed += OnUpdatesChanged;
        Closed += (_, _) =>
        {
            FlushSlider();
            _host.Settings.Changed -= OnSettingsChanged;
            _host.Updates.Changed -= OnUpdatesChanged;
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

    // ---------------------------------------------------------------- pages

    // The pages and their order come from SettingsPages, which search shares.

    private string _page = SettingsPages.General;
    private bool _selecting;

    /// <summary>The sidebar items and its search box; the first page shows.</summary>
    private void BuildNavigation()
    {
        foreach (SettingsPage page in SettingsPages.All())
        {
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = page.Name,
                Tag = page.Key,
                Icon = new FontIcon { Glyph = ((char)page.Glyph).ToString(), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") },
            });
        }

        Nav.AutoSuggestBox = BuildSearchBox();

        _selecting = true;
        try
        {
            Nav.SelectedItem = Nav.MenuItems[0];
        }
        finally
        {
            _selecting = false;
        }

        ShowPage(SettingsPages.General);
    }

    /// <summary>
    /// Opens a page from outside the sidebar (Start's or the bar's search, this window's search
    /// box): selects it in the sidebar and shows it from the top. An unknown key is logged and left.
    /// </summary>
    internal void GoToPage(string key)
    {
        if (SettingsPages.Named(key) is null)
        {
            Log.Warn("Settings: there is no page \"" + key + "\".");
            return;
        }

        _selecting = true;
        try
        {
            foreach (object item in Nav.MenuItems)
            {
                if (item is NavigationViewItem { Tag: string tag } && tag == key)
                {
                    Nav.SelectedItem = item;
                }
            }
        }
        finally
        {
            _selecting = false;
        }

        if (key != _page)
        {
            // A slider still settling belongs to the page being left.
            FlushSlider();
            ShowPage(key);
        }

        Scroller.ChangeView(null, 0, null, true);
    }

    /// <summary>
    /// The sidebar's search box: it lists the pages whose names fit what is typed (best fit
    /// first, as Start's search ranks); choosing one, or Enter for the first, opens it.
    /// </summary>
    private AutoSuggestBox BuildSearchBox()
    {
        var box = new AutoSuggestBox
        {
            PlaceholderText = Strings.SetSearchSettings,
            QueryIcon = new SymbolIcon(Symbol.Find),
            UpdateTextOnSelect = false,
        };
        AutomationProperties.SetName(box, Strings.SetSearchSettings);

        box.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            if (sender.Text.Trim().Length == 0)
            {
                sender.ItemsSource = null;
                return;
            }

            // Nothing that fits says so (it opens nothing).
            var items = new List<object>(SettingsPages.Find(sender.Text));
            if (items.Count == 0)
            {
                items.Add(Strings.SetSearchNoPage);
            }

            sender.ItemsSource = items;
        };

        box.QuerySubmitted += (sender, args) =>
        {
            SettingsPage? page = args.ChosenSuggestion as SettingsPage;
            if (page is null && args.ChosenSuggestion is null)
            {
                List<SettingsPage> found = SettingsPages.Find(args.QueryText);
                page = found.Count > 0 ? found[0] : null;
            }

            if (page is null)
            {
                return;
            }

            sender.Text = string.Empty;
            sender.ItemsSource = null;
            GoToPage(page.Key);
        };

        return box;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_selecting || args.SelectedItem is not NavigationViewItem { Tag: string key } || key == _page)
        {
            return;
        }

        // A slider still settling belongs to the page being left.
        FlushSlider();
        ShowPage(key);
        Scroller.ChangeView(null, 0, null, true);
    }

    /// <summary>Builds one page: its title, then its sections.</summary>
    private void ShowPage(string key)
    {
        _page = key;
        _builtDark = IsDark();
        Sections.Children.Clear();
        _refreshers.Clear();
        _updateRefresh = null;
        _statusText = null;

        if (SettingsPages.Named(key) is SettingsPage shown)
        {
            AddTitle(shown.Name);
        }

        switch (key)
        {
            case SettingsPages.Taskbar:
                BuildTaskbarPage();
                break;
            case SettingsPages.Start:
                BuildStartPage();
                break;
            case SettingsPages.Widgets:
                BuildWidgetsPage();
                break;
            case SettingsPages.Dynaland:
                BuildDynalandPage();
                break;
            case SettingsPages.Edge:
                BuildEdgePage();
                break;
            case SettingsPages.Shortcuts:
                BuildShortcutsPage();
                break;
            case SettingsPages.Privacy:
                BuildPrivacyPage();
                break;
            case SettingsPages.About:
                BuildAboutPage();
                break;
            default:
                BuildGeneralPage();
                break;
        }
    }

    private void BuildGeneralPage()
    {
        var languages = new List<string> { Strings.SetLanguageWindows };
        foreach ((string Code, string Name) language in Languages.Available)
        {
            languages.Add(language.Name);
        }

        AddCard(
            ComboRow(
                Strings.SetLanguage,
                Strings.SetLanguageNote,
                languages.ToArray(),
                s => Languages.IndexOf(s.Language),
                (s, i) => s.Language = Languages.CodeAt(i)),
            ComboRow(
                Strings.SetTheme,
                string.Empty,
                new[] { Strings.SetDark, Strings.SetLight, Strings.SetSystem },
                s => (int)s.Theme,
                (s, i) => s.Theme = (ThemeMode)i),
            ComboRow(
                Strings.SetAccentColour,
                string.Empty,
                new[] { Strings.SetAccentFromWallpaper, Strings.SetAccentFromWindows },
                s => (int)s.AccentSource,
                (s, i) => s.AccentSource = (AccentSource)i),
            ToggleRow(
                Strings.SetAcrylic,
                string.Empty,
                s => s.EnableAcrylic,
                (s, on) => s.EnableAcrylic = on),
            SliderRow(
                Strings.SetOpacity,
                string.Empty,
                ShellSettings.MinOpacity * 100,
                ShellSettings.MaxOpacity * 100,
                5,
                s => s.Opacity * 100,
                (s, v) => s.Opacity = Math.Round(v) / 100.0,
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"),
            ToggleRow(
                Strings.SetTurnOffAnimations,
                Strings.SetTurnOffAnimationsNote,
                s => s.TurnOffAnimations,
                (s, on) => s.TurnOffAnimations = on));

        AddSection(Strings.SetStartup);
        AddCard(RunAtStartupRow());
    }

    private void BuildTaskbarPage()
    {
        AddSection(Strings.SetCustomize);
        AddCard(
            ButtonRow(
                Strings.SetCustomize,
                string.Empty,
                () =>
                {
                    _host.Custom.Enter();
                    _host.Custom.OpenPicker(Machitan.Custom.PickerPage.StartCluster);
                }),
            ToggleRow(
                Strings.SetAppNames,
                string.Empty,
                s => s.DockLabels,
                (s, v) => s.DockLabels = v),
            ToggleRow(
                Strings.SetTrayOnBar,
                string.Empty,
                s => s.TrayOnBar,
                (s, v) => s.TrayOnBar = v));

        AddSection(Strings.SetLayout);
        AddCard(
            ComboRow(
                Strings.SetMode,
                Strings.SetModeNote,
                new[] { Strings.SetModeSegmented, Strings.SetModeJoined, Strings.SetModeClassic, Strings.SetModeCompact },
                s => (int)s.TaskbarMode,
                (s, i) => s.TaskbarMode = (TaskbarMode)i),
            ComboRow(
                Strings.SetSplit,
                Strings.SetSplitNote,
                new[] { Strings.SetSplitOff, Strings.SetSplitRows, Strings.SetSplitStacked },
                s => (int)s.TaskbarSplit,
                (s, i) => s.TaskbarSplit = (TaskbarSplit)i),
            ComboRow(
                Strings.SetPosition,
                string.Empty,
                new[] { Strings.SetBottom, Strings.SetTop },
                s => (int)s.DockEdge,
                (s, i) => s.DockEdge = (DockEdge)i),
            ComboRow(
                Strings.SetAlignment,
                string.Empty,
                new[] { Strings.SetLeft, Strings.SetCenter, Strings.SetRight },
                s => (int)s.DockAlignment,
                (s, i) => s.DockAlignment = (DockAlignment)i),
            ToggleRow(
                Strings.SetAutoHide,
                string.Empty,
                s => s.AutoHideBar,
                (s, on) => s.AutoHideBar = on),
            ToggleRow(
                Strings.SetPeekDesktop,
                Strings.SetPeekDesktopNote,
                s => s.PeekAtDesktop,
                (s, on) => s.PeekAtDesktop = on),
            ComboRow(
                Strings.SetPreviewsAs,
                string.Empty,
                new[] { Strings.SetThumbnails, Strings.SetList },
                s => (int)s.PreviewStyle,
                (s, i) => s.PreviewStyle = (PreviewStyle)i));

        AddSection(Strings.SetSize);
        AddCard(
            SliderRow(
                Strings.SetBarHeight,
                string.Empty,
                ShellSettings.MinBarSize,
                ShellSettings.MaxBarSize,
                2,
                s => s.BarSize,
                (s, v) => s.BarSize = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)),
            SliderRow(
                Strings.SetIconSize,
                string.Empty,
                ShellSettings.MinIconSize,
                ShellSettings.MaxIconSize,
                2,
                s => s.IconSize,
                (s, v) => s.IconSize = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)));

        AddSection(Strings.SetSearch);
        AddCard(
            ComboRow(
                Strings.SetSearch,
                Strings.SetSearchNote,
                new[] { Strings.SetSearchIcon, Strings.SetSearchBox, Strings.SetHidden },
                s => (int)s.SearchMode,
                (s, i) => s.SearchMode = (SearchMode)i),
            ComboRow(
                Strings.SetPlace,
                string.Empty,
                new[] { Strings.SetWithStart, Strings.SetBeforeApps, Strings.SetAfterApps },
                s => (int)s.SearchPlace,
                (s, i) => s.SearchPlace = (SearchPlace)i));

        AddSection(Strings.SetCalendar);
        AddCard(
            ToggleRow(
                Strings.SetSeconds,
                string.Empty,
                s => s.CalendarShowSeconds,
                (s, on) => s.CalendarShowSeconds = on));
    }

    private void BuildStartPage()
    {
        var (sleep, _, hibernate) = PowerActions.Capabilities();

        AddSection(Strings.SetStartMenu);
        AddCard(
            ToggleRow(Strings.SetMostUsed, Strings.SetMostUsedNote, s => s.StartMostUsed, (s, on) => s.StartMostUsed = on));

        AddSection(Strings.SetPowerMenu);
        AddCard(
            AlwaysOnRow(Strings.PowerLock, Strings.SetAlwaysInPowerMenuLock),
            OfferedToggleRow(Strings.PowerSleep, sleep, Strings.SetCannotSleep, s => s.PowerSleep, (s, on) => s.PowerSleep = on),
            OfferedToggleRow(Strings.PowerHibernate, hibernate, Strings.SetHibernationOff, s => s.PowerHibernate, (s, on) => s.PowerHibernate = on),
            ToggleRow(Strings.PowerShutDown, string.Empty, s => s.PowerShutDown, (s, on) => s.PowerShutDown = on),
            ToggleRow(Strings.PowerRestart, string.Empty, s => s.PowerRestart, (s, on) => s.PowerRestart = on),
            ToggleRow(Strings.PowerUpdateShutDown, Strings.SetShowsWhileUpdate, s => s.PowerUpdateShutDown, (s, on) => s.PowerUpdateShutDown = on),
            ToggleRow(Strings.PowerUpdateRestart, Strings.SetShowsWhileUpdate, s => s.PowerUpdateRestart, (s, on) => s.PowerUpdateRestart = on),
            AlwaysOnRow(Strings.BarExit, Strings.SetAlwaysInPowerMenu));

        AddSection(Strings.SetNextToPower);
        AddFolderCard();

        AddSection(Strings.SetStartButton);
        AddCard(
            ToggleRow(
                Strings.SetOwnPictures,
                Strings.SetOwnPicturesNote,
                s => s.CustomStartButton,
                (s, on) => s.CustomStartButton = on),
            PictureRow(
                Strings.SetPictureNormal,
                Strings.SetPictureNoneLogo,
                turnsOn: true,
                s => s.StartButtonPicture,
                (s, path) => s.StartButtonPicture = path),
            PictureRow(
                Strings.SetPictureHovered,
                Strings.SetPictureNoneNormal,
                turnsOn: false,
                s => s.StartButtonPictureHover,
                (s, path) => s.StartButtonPictureHover = path),
            PictureRow(
                Strings.SetPicturePressed,
                Strings.SetPictureNoneHovered,
                turnsOn: false,
                s => s.StartButtonPicturePressed,
                (s, path) => s.StartButtonPicturePressed = path));
    }

    /// <summary>
    /// One of the Start button's pictures: a small preview, what is chosen (or what shows
    /// instead), Choose picture and Remove. <paramref name="turnsOn"/>: choosing one also turns
    /// "My own pictures" on (the normal picture: without it nothing changes on the bar).
    /// </summary>
    private Grid PictureRow(string label, string noneText, bool turnsOn, Func<ShellSettings, string> read, Action<ShellSettings, string> write)
    {
        var preview = new Image { Width = 36, Height = 24, Stretch = Stretch.Uniform };
        AutomationProperties.SetAccessibilityView(preview, AccessibilityView.Raw);

        var status = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

        var choose = new Button { Content = Strings.SetChoosePicture, CornerRadius = new CornerRadius(Radii.Small) };
        AutomationProperties.SetName(choose, Fmt.F(Strings.SetChoosePictureName, label));

        var remove = new Button { Content = Strings.CmRemove, CornerRadius = new CornerRadius(Radii.Small) };
        AutomationProperties.SetName(remove, Fmt.F(Strings.SetRemovePictureName, label));

        string? shownPath = null;
        void ShowNoPreview()
        {
            preview.Source = null;
            preview.Visibility = Visibility.Collapsed;
        }

        void Refresh()
        {
            string path = read(_host.Settings.Current);
            remove.IsEnabled = path.Length > 0;
            if (path == shownPath)
            {
                return;
            }

            shownPath = path;
            if (path.Length == 0)
            {
                status.Text = noneText;
                status.ClearValue(ToolTipService.ToolTipProperty);
                ShowNoPreview();
                return;
            }

            ToolTipService.SetToolTip(status, path);
            if (!System.IO.File.Exists(path))
            {
                status.Text = Fmt.F(Strings.SetPictureMissing, path);
                ShowNoPreview();
                return;
            }

            status.Text = System.IO.Path.GetFileName(path);
            try
            {
                preview.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                {
                    CreateOptions = Microsoft.UI.Xaml.Media.Imaging.BitmapCreateOptions.IgnoreImageCache,
                    DecodePixelHeight = 48,
                    UriSource = new Uri(path),
                };
                preview.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowNoPreview();
                Log.Warn("Settings: the Start button picture could not be previewed (" + path + ").", ex);
            }
        }

        Refresh();
        _refreshers.Add(Refresh);

        async void Choose()
        {
            choose.IsEnabled = false;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                };
                foreach (string type in PictureTypes)
                {
                    picker.FileTypeFilter.Add(type);
                }

                // An unpackaged app's picker needs its owner window.
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file is null || string.IsNullOrEmpty(file.Path))
                {
                    // Cancelled (or not a file on this PC).
                    return;
                }

                string path = file.Path;
                _host.Settings.Update(s =>
                {
                    write(s, path);
                    if (turnsOn)
                    {
                        s.CustomStartButton = true;
                    }
                });
                shownPath = null;
                Refresh();
            }
            catch (Exception ex)
            {
                status.Text = Strings.SetPicturePickFailed;
                Log.Error("Settings: the picture picker failed.", ex);
            }
            finally
            {
                choose.IsEnabled = true;
            }
        }

        choose.Click += (_, _) => Choose();
        remove.Click += (_, _) =>
        {
            _host.Settings.Update(s => write(s, string.Empty));
            Refresh();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(preview);
        buttons.Children.Add(choose);
        buttons.Children.Add(remove);

        Grid row = Row(label, string.Empty, buttons);
        if (row.Children[0] is StackPanel text)
        {
            text.Children.Add(status);
        }

        return row;
    }

    /// <summary>The picture files the Start button can show.</summary>
    private static readonly string[] PictureTypes = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico" };

    /// <summary>
    /// The buttons next to Start's power button: each with its icon, on or off, and dragged
    /// by its handle to a new place (top to bottom here is left to right on Start); Alt+Up and
    /// Alt+Down move the one whose handle has the keyboard. Rebuilt when the order changes.
    /// </summary>
    private void AddFolderCard()
    {
        var rows = new StackPanel();
        AddCard(rows);
        var toggles = new List<(ToggleSwitch Toggle, Start.StartFolder Folder)>();
        var views = new List<Grid>();
        string builtOrder = string.Empty;
        string? focusId = null;

        int dragFrom = -1;
        int dragTo = -1;
        bool dragging = false;
        double dragStartY = 0;
        double[] tops = Array.Empty<double>();
        double[] heights = Array.Empty<double>();
        var clear = new SolidColorBrush(Colors.Transparent);
        Brush lifted = clear;

        void MoveTo(string id, int to)
        {
            List<string> order = ShellSettings.ParseFolderOrder(_host.Settings.Current.FolderOrder);
            int from = order.IndexOf(id);
            if (from < 0 || to < 0 || to >= order.Count || to == from)
            {
                return;
            }

            order.RemoveAt(from);
            order.Insert(to, id);
            focusId = id;
            string text = string.Join(",", order);
            _host.Settings.Update(s => s.FolderOrder = text);
            Refresh();
        }

        void ResetShifts()
        {
            foreach (Grid view in views)
            {
                if (view.RenderTransform is TranslateTransform shift)
                {
                    shift.Y = 0;
                }

                view.Background = clear;
                Canvas.SetZIndex(view, 0);
            }
        }

        void StartDrag(int index, double y)
        {
            dragFrom = index;
            dragTo = index;
            dragging = false;
            dragStartY = y;
            tops = new double[views.Count];
            heights = new double[views.Count];
            for (int i = 0; i < views.Count; i++)
            {
                tops[i] = views[i].TransformToVisual(rows).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                heights[i] = views[i].ActualHeight;
            }
        }

        void DragTo(double y)
        {
            if (dragFrom < 0 || dragFrom >= views.Count)
            {
                return;
            }

            double dy = y - dragStartY;
            if (!dragging && Math.Abs(dy) < 4)
            {
                return;
            }

            dragging = true;
            double size = heights[dragFrom];
            dy = Math.Clamp(dy, -tops[dragFrom], Math.Max(0, rows.ActualHeight - tops[dragFrom] - size));
            double centre = tops[dragFrom] + (size / 2) + dy;
            int to = dragFrom;
            for (int j = 0; j < views.Count; j++)
            {
                double middle = tops[j] + (heights[j] / 2);
                if (j < dragFrom && centre < middle)
                {
                    to = Math.Min(to, j);
                }
                else if (j > dragFrom && centre > middle)
                {
                    to = Math.Max(to, j);
                }
            }

            dragTo = to;
            for (int j = 0; j < views.Count; j++)
            {
                if (views[j].RenderTransform is not TranslateTransform shift)
                {
                    continue;
                }

                if (j == dragFrom)
                {
                    shift.Y = dy;
                    views[j].Background = lifted;
                    Canvas.SetZIndex(views[j], 1);
                }
                else if (j > dragFrom && j <= to)
                {
                    shift.Y = -size;
                }
                else if (j < dragFrom && j >= to)
                {
                    shift.Y = size;
                }
                else
                {
                    shift.Y = 0;
                }
            }
        }

        void EndDrag(string id)
        {
            if (dragFrom < 0)
            {
                return;
            }

            int to = dragTo;
            bool moved = dragging && to != dragFrom;
            dragFrom = -1;
            dragging = false;
            ResetShifts();
            if (moved)
            {
                MoveTo(id, to);
            }
        }

        void Build()
        {
            ShellSettings settings = _host.Settings.Current;
            List<Start.StartFolder> folders = Start.StartFolders.InOrder(settings);
            builtOrder = settings.FolderOrder;
            rows.Children.Clear();
            toggles.Clear();
            views.Clear();
            dragFrom = -1;
            Button? focusTarget = null;
            bool dark = IsDark();
            var line = new SolidColorBrush(dark ? ColorHelper.FromArgb(30, 255, 255, 255) : ColorHelper.FromArgb(30, 0, 0, 0));

            // While dragged, a row gets a solid fill, so it hides the rows it passes over.
            lifted = new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 50, 50, 50) : ColorHelper.FromArgb(255, 249, 249, 249));
            for (int i = 0; i < folders.Count; i++)
            {
                Start.StartFolder folder = folders[i];
                int index = i;

                var handle = new Button
                {
                    Content = new FontIcon { Glyph = ((char)0xE76F).ToString(), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 },
                    Width = 28,
                    Height = 32,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetName(handle, Fmt.F(Strings.SetMoveName, folder.Name));
                AutomationProperties.SetHelpText(handle, Strings.SetDragHelp);
                ToolTipService.SetToolTip(handle, Strings.SetDragTip);
                handle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
                {
                    handle.CapturePointer(e.Pointer);
                    StartDrag(index, e.GetCurrentPoint(rows).Position.Y);
                }), handledEventsToo: true);
                handle.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
                {
                    if (dragFrom == index)
                    {
                        DragTo(e.GetCurrentPoint(rows).Position.Y);
                    }
                }), handledEventsToo: true);
                handle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag(folder.Id)), handledEventsToo: true);
                handle.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag(folder.Id)), handledEventsToo: true);
                handle.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
                {
                    bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                        & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                    if (alt && e.Key is VirtualKey.Up or VirtualKey.Down)
                    {
                        e.Handled = true;
                        MoveTo(folder.Id, index + (e.Key == VirtualKey.Up ? -1 : 1));
                    }
                }), handledEventsToo: true);

                FrameworkElement icon = Start.StartFolders.Icon(folder, 16);
                icon.Width = 16;
                icon.Height = 16;
                icon.VerticalAlignment = VerticalAlignment.Center;

                var name = new TextBlock { Text = folder.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

                var toggle = new ToggleSwitch { OnContent = Strings.On, OffContent = Strings.Off, MinWidth = 0, IsOn = folder.On(settings), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(toggle, folder.Name);
                toggle.Toggled += (_, _) =>
                {
                    if (!_refreshing)
                    {
                        bool on = toggle.IsOn;
                        _host.Settings.Update(s => folder.Set(s, on));
                    }
                };
                toggles.Add((toggle, folder));

                var view = new Grid
                {
                    Padding = new Thickness(0, 8, 0, 8),
                    ColumnSpacing = 12,
                    Background = clear,
                    BorderBrush = line,
                    BorderThickness = new Thickness(0, i > 0 ? 1 : 0, 0, 0),
                    RenderTransform = new TranslateTransform(),
                };
                view.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                view.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                view.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                view.Children.Add(handle);
                Grid.SetColumn(icon, 1);
                view.Children.Add(icon);
                Grid.SetColumn(name, 2);
                view.Children.Add(name);
                Grid.SetColumn(toggle, 3);
                view.Children.Add(toggle);
                rows.Children.Add(view);
                views.Add(view);

                if (folder.Id == focusId)
                {
                    focusTarget = handle;
                }
            }

            if (focusTarget is not null)
            {
                focusId = null;
                Button target = focusTarget;
                DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Keyboard));
            }
        }

        void Refresh()
        {
            ShellSettings settings = _host.Settings.Current;
            if (settings.FolderOrder != builtOrder)
            {
                Build();
                return;
            }

            foreach ((ToggleSwitch toggle, Start.StartFolder folder) in toggles)
            {
                toggle.IsOn = folder.On(settings);
            }
        }

        Build();
        _refreshers.Add(Refresh);
    }

    private void BuildWidgetsPage()
    {
        AddSection(Strings.SetInfoLine);
        AddCard(
            ToggleRow(
                Strings.SetInfoLine,
                string.Empty,
                s => s.InfoLineEnabled,
                (s, on) => s.InfoLineEnabled = on),
            CornerRow(Strings.SetCorner, s => s.InfoLineCorner, (s, c) => s.InfoLineCorner = c),
            SliderRow(
                Strings.SetSize,
                string.Empty,
                ShellSettings.MinWidgetSize,
                ShellSettings.MaxWidgetSize,
                5,
                s => s.InfoLineSize,
                (s, v) => s.InfoLineSize = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"),
            ToggleRow(Strings.SetDate, string.Empty, s => s.InfoLineShowDate, (s, on) => s.InfoLineShowDate = on),
            ToggleRow(Strings.SetTime, string.Empty, s => s.InfoLineShowTime, (s, on) => s.InfoLineShowTime = on),
            ToggleRow(Strings.SetSeconds, string.Empty, s => s.InfoLineShowSeconds, (s, on) => s.InfoLineShowSeconds = on),
            ToggleRow(
                Strings.SetWeather,
                string.Empty,
                s => s.InfoLineShowWeather,
                (s, on) => s.InfoLineShowWeather = on));

        AddSection(Strings.SetWeatherPlace);
        AddCard(
            ComboRow(
                Strings.SetLocation,
                string.Empty,
                new[] { Strings.SetWindowsLocation, Strings.SetPlaceIChoose },
                s => s.WeatherUseManualPlace ? 1 : 0,
                (s, i) => s.WeatherUseManualPlace = i == 1),
            WeatherPlaceRow());

        AddSection(Strings.SetStatusRing);
        AddCard(
            ToggleRow(
                Strings.SetStatusRing,
                string.Empty,
                s => s.ShowDuoRing,
                (s, on) => s.ShowDuoRing = on),
            CornerRow(Strings.SetCorner, s => s.DuoRingCorner, (s, c) => s.DuoRingCorner = c),
            SliderRow(
                Strings.SetSize,
                string.Empty,
                ShellSettings.MinWidgetSize,
                ShellSettings.MaxWidgetSize,
                5,
                s => s.DuoRingSize,
                (s, v) => s.DuoRingSize = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"),
            SliderRow(
                Strings.SetLowBattery,
                Strings.SetLowBatteryNote,
                ShellSettings.MinRingLowBattery,
                ShellSettings.MaxRingLowBattery,
                5,
                s => s.DuoRingLowBattery,
                (s, v) => s.DuoRingLowBattery = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"),
            ComboRow(
                Strings.SetTransparency,
                Strings.SetRingTransparencyNote,
                new[] { Strings.SetAuto, Strings.SetManual },
                s => s.DuoRingTransparencyAuto ? 0 : 1,
                (s, i) => s.DuoRingTransparencyAuto = i == 0),
            EnabledWhen(
                SliderRow(
                    Strings.SetManualTransparency,
                    Strings.SetManualTransparencyNote,
                    0,
                    100,
                    5,
                    s => s.DuoRingTransparency,
                    (s, v) => s.DuoRingTransparency = (int)Math.Round(v),
                    v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"),
                s => !s.DuoRingTransparencyAuto));
    }

    private void BuildDynalandPage()
    {
        AddCard(
            ToggleRow(
                Strings.SetPageDynaland,
                string.Empty,
                s => s.EnableDynaland,
                (s, on) => s.EnableDynaland = on),
            ComboRow(
                Strings.SetPosition,
                string.Empty,
                new[] { Strings.SetOppositeBar, Strings.SetTop, Strings.SetBottom },
                s => (int)s.DynalandPosition,
                (s, i) => s.DynalandPosition = (DynalandPosition)i),
            ComboRow(
                Strings.SetShape,
                string.Empty,
                new[] { Strings.SetNotch, Strings.SetIsland },
                s => (int)s.DynalandStyle,
                (s, i) => s.DynalandStyle = (DynalandStyle)i),
            EnabledWhen(
                ComboRow(
                    Strings.SetNotchShape,
                    Strings.SetNotchShapeNote,
                    new[] { Strings.SetDefault, Strings.SetTrapezoid },
                    s => (int)s.DynalandNotchShape,
                    (s, i) => s.DynalandNotchShape = (DynalandNotchShape)i),
                s => s.DynalandStyle == DynalandStyle.Notch),
            SliderRow(
                Strings.SetTransparency,
                Strings.SetDynalandTransparencyNote,
                0,
                ShellSettings.MaxDynalandTransparency,
                5,
                s => s.DynalandTransparency,
                (s, v) => s.DynalandTransparency = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"),
            SliderRow(
                Strings.SetWidth,
                string.Empty,
                ShellSettings.MinDynalandWidth,
                ShellSettings.MaxDynalandWidth,
                10,
                s => s.DynalandWidth,
                (s, v) => s.DynalandWidth = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)),
            SliderRow(
                Strings.SetSize,
                string.Empty,
                ShellSettings.MinWidgetSize,
                ShellSettings.MaxWidgetSize,
                5,
                s => s.DynalandSize,
                (s, v) => s.DynalandSize = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"),
            ToggleRow(
                Strings.SetPrivacyDots,
                Strings.SetPrivacyDotsNote,
                s => s.ShowPrivacyDots,
                (s, on) => s.ShowPrivacyDots = on));
    }

    private void BuildEdgePage()
    {
        AddNote(Strings.SetEdgeNote);
        AddCard(
            ToggleRow(
                Strings.SetKeepEdgeClear,
                string.Empty,
                s => s.ReserveTopStrip,
                (s, on) => s.ReserveTopStrip = on),
            ToggleRow(
                Strings.SetCover,
                Strings.SetCoverNote,
                s => s.TopStripCover,
                (s, on) => s.TopStripCover = on),
            ComboRow(
                Strings.SetCoverColour,
                string.Empty,
                new[] { Strings.SetDark, Strings.SetLight, Strings.SetAccentColour },
                s => (int)s.TopStripCoverColour,
                (s, i) => s.TopStripCoverColour = (StripCoverColour)i),
            SliderRow(
                Strings.SetCoverOpacity,
                string.Empty,
                ShellSettings.MinCoverOpacity * 100,
                ShellSettings.MaxCoverOpacity * 100,
                5,
                s => s.TopStripCoverOpacity * 100,
                (s, v) => s.TopStripCoverOpacity = Math.Round(v) / 100.0,
                v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%"));
    }

    private void BuildShortcutsPage()
    {
        AddSection(Strings.SetStatus);
        _statusText = new TextBlock { Text = _host.SystemStatus, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(_statusText, Strings.SetHotkeyStatus);
        AddCard(Row(Strings.SetHotkeys, string.Empty, _statusText, stacked: true));

        AddSection(Strings.SetAdminApps);
        AddCard(AdminKeysRow());
    }

    /// <summary>
    /// "Shortcuts in apps running as administrator": read from the shell each time (on only for
    /// this copy of Machitan.exe); switching asks Windows (one prompt each way).
    /// </summary>
    private Grid AdminKeysRow()
    {
        var toggle = new ToggleSwitch { OnContent = Strings.On, OffContent = Strings.Off, MinWidth = 0 };
        string label = Strings.SetAdminKeys;
        AutomationProperties.SetName(toggle, label);

        bool settingIt = false;
        void Refresh()
        {
            settingIt = true;
            toggle.IsOn = _host.AdminAppShortcutsOn;
            settingIt = false;
        }

        Refresh();
        _refreshers.Add(Refresh);
        toggle.Toggled += async (_, _) =>
        {
            if (settingIt || _refreshing)
            {
                return;
            }

            bool on = toggle.IsOn;
            toggle.IsEnabled = false;
            try
            {
                await _host.SetAdminAppShortcutsAsync(on);
            }
            catch (Exception ex)
            {
                Log.Error("Settings: the shortcuts-in-admin-apps option could not be changed.", ex);
            }
            finally
            {
                toggle.IsEnabled = true;
                Refresh();
                RefreshStatus();
            }
        };

        return Row(label, Strings.SetAdminKeysNote, toggle);
    }

    private void BuildPrivacyPage()
    {
        AddCard(
            TextRow(
                Strings.SetWeather,
                Strings.SetPrivacyWeather),
            TextRow(
                Strings.SetStatusRing,
                Strings.SetPrivacyRing),
            TextRow(
                Strings.SetShelf,
                Fmt.F(Strings.SetPrivacyShelf, AppPaths.ShelfFile)),
            TextRow(
                Strings.SetUpdates,
                Strings.SetPrivacyUpdates),
            TextRow(
                Strings.SetDiagnosticsLog,
                Fmt.F(Strings.SetPrivacyLog, AppPaths.LogFile)),
            ButtonRow(Strings.SetOpenFolder, string.Empty, OpenDataFolder));
    }

    private void BuildAboutPage()
    {
        AddCard(TextRow(
            "Machitan " + typeof(SettingsWindow).Assembly.GetName().Version,
            Fmt.F(Strings.SetAboutText, Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture))));

        AddSection(Strings.SetUpdates);
        AddCard(
            UpdateRow(),
            ToggleRow(
                Strings.SetCheckForUpdates,
                Strings.SetCheckForUpdatesNote,
                s => s.CheckForUpdates,
                (s, on) => s.CheckForUpdates = on),
            ToggleRow(
                Strings.SetOfferPreReleases,
                Strings.SetOfferPreReleasesNote,
                s => s.OfferPreReleasesNow,
                (s, on) =>
                {
                    s.OfferPreReleases = on;
                    s.OfferPreReleasesChosen = true;
                }));
        UpdateService updates = _host.Updates;
        if (!updates.IsSetupCopy)
        {
            string target = updates.SetupFolder
                ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Machitan");
            AddNote(Fmt.F(
                Strings.UpdNotSetupCopy,
                System.IO.Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                System.IO.Path.TrimEndingDirectorySeparator(target)));
        }

        AddSection(Strings.SetPlanned);
        AddNote(Strings.SetNotBuiltYet);
        AddCard(
            DisabledToggleRow(Strings.SetSystemStats, s => s.ShowSystemStats),
            DisabledToggleRow(Strings.SetFocusedAppName, s => s.ShowFocusedAppName),
            DisabledToggleRow(Strings.SetRecentItems, s => s.ShowRecentItems));
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
                stack.Children.Add(CardLine());
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

    /// <summary>The thin line between a card's rows.</summary>
    private Border CardLine()
    {
        return new Border
        {
            Height = 1,
            Background = new SolidColorBrush(IsDark() ? ColorHelper.FromArgb(30, 255, 255, 255) : ColorHelper.FromArgb(30, 0, 0, 0)),
        };
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
        var toggle = new ToggleSwitch { OnContent = Strings.On, OffContent = Strings.Off, MinWidth = 0 };
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

    /// <summary>A row whose control is greyed while <paramref name="enabled"/> is false (kept in step with the settings).</summary>
    private Grid EnabledWhen(Grid row, Func<ShellSettings, bool> enabled)
    {
        if (row.Children.Count > 1 && row.Children[1] is FrameworkElement control)
        {
            void Refresh()
            {
                bool on = enabled(_host.Settings.Current);
                if (control is Control single)
                {
                    single.IsEnabled = on;
                }
                else if (control is Panel panel)
                {
                    foreach (UIElement child in panel.Children)
                    {
                        if (child is Control part)
                        {
                            part.IsEnabled = on;
                        }
                    }

                    panel.Opacity = on ? 1 : 0.6;
                }
            }

            Refresh();
            _refreshers.Add(Refresh);
        }

        return row;
    }

    /// <summary>An entry that is always there: its toggle shows On, greyed, and the note says why.</summary>
    private static Grid AlwaysOnRow(string label, string note)
    {
        var toggle = new ToggleSwitch
        {
            OnContent = Strings.On,
            OffContent = Strings.Off,
            MinWidth = 0,
            IsOn = true,
            IsEnabled = false,
        };
        AutomationProperties.SetName(toggle, label + " (" + note + ")");
        return Row(label, note, toggle);
    }

    /// <summary>A toggle for something this PC may not offer: when it does not, the toggle is greyed and says why.</summary>
    private Grid OfferedToggleRow(string label, bool offered, string whyNot, Func<ShellSettings, bool> read, Action<ShellSettings, bool> write)
    {
        Grid row = ToggleRow(label, offered ? string.Empty : whyNot, read, write);
        if (!offered && row.Children[1] is ToggleSwitch toggle)
        {
            toggle.IsEnabled = false;
            AutomationProperties.SetName(toggle, label + " (" + whyNot + ")");
        }

        return row;
    }

    /// <summary>"Run at startup": read from and written to Windows' startup list itself (see StartupRegistration).</summary>
    private Grid RunAtStartupRow()
    {
        var toggle = new ToggleSwitch { OnContent = Strings.On, OffContent = Strings.Off, MinWidth = 0 };
        AutomationProperties.SetName(toggle, Strings.SetRunAtStartup);

        bool settingIt = false;
        void Refresh()
        {
            settingIt = true;
            toggle.IsOn = StartupRegistration.IsOn();
            settingIt = false;
        }

        Refresh();
        _refreshers.Add(Refresh);
        toggle.Toggled += (_, _) =>
        {
            if (settingIt || _refreshing)
            {
                return;
            }

            StartupRegistration.Set(toggle.IsOn);
            Refresh();
        };

        return Row(Strings.SetRunAtStartup, string.Empty, toggle);
    }

    private Grid DisabledToggleRow(string label, Func<ShellSettings, bool> read)
    {
        var toggle = new ToggleSwitch { OnContent = Strings.On, OffContent = Strings.Off, MinWidth = 0, IsEnabled = false, IsOn = read(_host.Settings.Current) };
        AutomationProperties.SetName(toggle, Fmt.F(Strings.SetNotBuiltName, label));
        return Row(label, string.Empty, toggle);
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
            new[] { Strings.SetTopLeft, Strings.SetTopRight, Strings.SetBottomLeft, Strings.SetBottomRight },
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
            PlaceholderText = Strings.SetPlaceBox,
            Width = 280,
            Text = _host.Settings.Current.WeatherPlaceName,
        };
        AutomationProperties.SetName(box, Strings.SetWeatherPlace);

        var find = new Button { Content = Strings.SetFind, CornerRadius = new CornerRadius(Radii.Small) };
        AutomationProperties.SetName(find, Strings.SetFindName);

        var status = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(status, Strings.SetPlaceInUseName);

        void Refresh() => status.Text = DescribeWeatherPlace(_host.Settings.Current);
        Refresh();
        _refreshers.Add(Refresh);

        async void FindPlace()
        {
            string query = box.Text;
            find.IsEnabled = false;
            status.Text = Fmt.F(Strings.SetLookingUp, query.Trim());
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
                status.Text = Strings.SetPlaceSearchFailed;
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
            Strings.SetWeatherPlace,
            string.Empty,
            control,
            stacked: true);
    }

    private static string DescribeWeatherPlace(ShellSettings settings)
    {
        bool hasPlace = settings.WeatherLatitude is not null && settings.WeatherLongitude is not null;
        if (!settings.WeatherUseManualPlace)
        {
            return hasPlace
                ? Fmt.F(Strings.SetInUseWindowsKept, settings.WeatherPlaceName)
                : Strings.SetInUseWindows;
        }

        if (!hasPlace)
        {
            return Strings.SetInUseNone;
        }

        return Fmt.F(Strings.SetInUsePlace,
            settings.WeatherPlaceName,
            settings.WeatherLatitude!.Value.ToString("0.00", CultureInfo.InvariantCulture),
            settings.WeatherLongitude!.Value.ToString("0.00", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// About's update row: where the update stands (UpdateService), in words, with what can be
    /// done now (check, download and install, cancel, start Setup again, the release's page).
    /// Redrawn whenever the update moves on, while the page shows.
    /// </summary>
    private Grid UpdateRow()
    {
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(detail);
        text.Children.Add(bar);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { Padding = new Thickness(0, 12, 0, 12), ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        UpdateService updates = _host.Updates;

        void AddButton(string label, Action onClick, bool accent = false)
        {
            var button = new Button { Content = label, CornerRadius = new CornerRadius(Radii.Small) };
            AutomationProperties.SetName(button, label);
            if (accent)
            {
                Start.ResultActions.PaintAccent(button, _host.Accent.Current);
            }

            button.Click += (_, _) => onClick();
            buttons.Children.Add(button);
        }

        void Check() => _ = updates.CheckAsync();
        void Install() => _ = updates.DownloadAndInstallAsync();
        void OpenPage() => Launcher.Start(updates.ReleaseUrl);

        void Refresh()
        {
            string thisCopy = Fmt.F(Strings.UpdThisCopy, updates.InstalledText, updates.ThisPc);
            string checkedAt = updates.CheckedAt is DateTime at ? Fmt.F(Strings.UpdCheckedAt, at.ToString("t", CultureInfo.CurrentCulture)) : string.Empty;
            buttons.Children.Clear();
            bar.Visibility = Visibility.Collapsed;
            bar.IsIndeterminate = false;
            switch (updates.State)
            {
                case UpdateState.Checking:
                    title.Text = Strings.UpdChecking;
                    detail.Text = thisCopy;
                    bar.IsIndeterminate = true;
                    bar.Visibility = Visibility.Visible;
                    break;
                case UpdateState.UpToDate:
                    title.Text = Fmt.F(Strings.UpdUpToDate, updates.InstalledText);
                    detail.Text = thisCopy + " " + checkedAt;
                    AddButton(Strings.UpdCheckAgain, Check);
                    break;
                case UpdateState.CheckFailed when updates.Problem == UpdateProblem.NoFinalRelease:
                    // The check worked: there are only pre-releases, and they are not offered.
                    title.Text = Strings.UpdNoOffer;
                    detail.Text = ProblemText(updates.Problem) + " " + checkedAt;
                    AddButton(Strings.UpdCheckAgain, Check);
                    break;
                case UpdateState.CheckFailed:
                    title.Text = Strings.UpdCheckFailed;
                    detail.Text = ProblemText(updates.Problem) + " " + Strings.UpdNothingKnown + " " + checkedAt;
                    AddButton(Strings.UpdTryAgain, Check);
                    break;
                case UpdateState.Available:
                    title.Text = Fmt.F(updates.LatestIsPreRelease ? Strings.UpdAvailablePre : Strings.UpdAvailable, updates.LatestText);
                    if (updates.CanInstall)
                    {
                        string megabytes = (updates.AssetSize / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture);
                        detail.Text = Fmt.F(Strings.UpdAvailableNote, updates.AssetName, megabytes);
                        AddButton(Strings.UpdDownloadInstall, Install, accent: true);
                        AddButton(Strings.UpdWhatsNew, OpenPage);
                    }
                    else
                    {
                        detail.Text = updates.Problem == UpdateProblem.NoInstaller
                            ? Fmt.F(Strings.UpdNoInstaller, updates.ThisPc)
                            : Strings.UpdNoDigest;
                        AddButton(Strings.UpdReleasePage, OpenPage, accent: true);
                    }

                    break;
                case UpdateState.Downloading:
                    int percent = (int)Math.Round(updates.Progress * 100);
                    title.Text = Fmt.F(Strings.UpdDownloading, updates.LatestText, percent);
                    detail.Text = updates.AssetName;
                    bar.Value = percent;
                    bar.Visibility = Visibility.Visible;
                    AddButton(Strings.Cancel, updates.CancelDownload);
                    break;
                case UpdateState.DownloadFailed:
                    title.Text = Strings.UpdDownloadFailed;
                    detail.Text = ProblemText(updates.Problem);
                    AddButton(Strings.UpdTryAgain, Install, accent: true);
                    AddButton(Strings.UpdReleasePage, OpenPage);
                    break;
                case UpdateState.SetupStarted:
                    title.Text = Strings.UpdSetupOpen;
                    detail.Text = Fmt.F(Strings.UpdSetupOpenNote, updates.LatestText);
                    AddButton(Strings.UpdStartSetup, () => _ = updates.StartSetupAgainAsync());
                    break;
                case UpdateState.SetupFailed:
                    title.Text = Strings.UpdSetupFailed;
                    detail.Text = ProblemText(updates.Problem);
                    AddButton(Strings.UpdTryAgain, () => _ = updates.StartSetupAgainAsync(), accent: true);
                    AddButton(Strings.UpdReleasePage, OpenPage);
                    break;
                default:
                    title.Text = Strings.UpdNotChecked;
                    detail.Text = thisCopy;
                    AddButton(Strings.UpdCheck, Check, accent: true);
                    break;
            }

            detail.Visibility = detail.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(grid, title.Text);
        }

        Refresh();
        _updateRefresh = Refresh;
        return grid;
    }

    /// <summary>Why an update step did not happen, in words.</summary>
    private static string ProblemText(UpdateProblem problem) => problem switch
    {
        UpdateProblem.NoConnection => Strings.UpdNoConnection,
        UpdateProblem.Refused => Strings.UpdRefused,
        UpdateProblem.NoRelease => Strings.UpdNoRelease,
        UpdateProblem.NoFinalRelease => Strings.UpdNoFinalRelease,
        UpdateProblem.BadAnswer => Strings.UpdBadAnswer,
        UpdateProblem.NoDigest => Strings.UpdNoDigest,
        UpdateProblem.Broken => Strings.UpdBroken,
        UpdateProblem.Cancelled => Strings.UpdCancelled,
        UpdateProblem.CannotSave => Strings.UpdCannotSave,
        UpdateProblem.SetupDeclined => Strings.UpdSetupDeclined,
        UpdateProblem.SetupDidNotStart => Strings.UpdSetupDidNotStart,
        _ => Strings.UpdOther,
    };

    private void OnUpdatesChanged()
    {
        _updateRefresh?.Invoke();
    }

    private static Grid TextRow(string label, string description)
    {
        return Row(label, description, new Border());
    }

    private static Grid ButtonRow(string label, string description, Action onClick)
    {
        var button = new Button { Content = label, CornerRadius = new CornerRadius(Radii.Small) };
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
                // Card colours depend on the theme: rebuild the page, keeping the scroll position.
                double offset = Scroller.VerticalOffset;
                ShowPage(_page);
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

        // The window's buttons sit on Machitan's title bar: give them the same theme.
        try
        {
            bool dark = IsDark();
            Microsoft.UI.Windowing.AppWindowTitleBar bar = AppWindow.TitleBar;
            Windows.UI.Color text = dark ? Colors.White : Colors.Black;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = text;
            bar.ButtonHoverForegroundColor = text;
            bar.ButtonPressedForegroundColor = text;
            bar.ButtonInactiveForegroundColor = dark ? ColorHelper.FromArgb(120, 255, 255, 255) : ColorHelper.FromArgb(120, 0, 0, 0);
            bar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(24, 255, 255, 255) : ColorHelper.FromArgb(24, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark ? ColorHelper.FromArgb(40, 255, 255, 255) : ColorHelper.FromArgb(40, 0, 0, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Settings: the window's buttons could not take the theme's colours.", ex);
        }
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
