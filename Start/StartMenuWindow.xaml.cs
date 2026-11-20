using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using VirtualKey = Windows.System.VirtualKey;

namespace Machitan.Start;

/// <summary>
/// The Start menu: separate floating boxes on a see-through window, height capped at 400 DIP.
/// Left: search (focused on open, filters both lists) over the Pinned grid ("Pin to Start";
/// unpin on hover). While searching files, a preview of the pointed file takes its place.
/// Right: All apps, as tall as the left column; each row's "..." button (on hover, always on
/// touch devices, or a right-click) opens Windows' Start menu entries and the app's jump list
/// tasks.
/// Below: a slim user strip with avatar, name, the folder buttons chosen in Settings, and the
/// power button (its menu holds the entries chosen in Settings that this PC offers, and
/// always Exit Machitan).
/// No recently used apps, no recent items, no widgets.
///
/// Search scopes: "apps:" (the default) and "files:". There is deliberately no web scope.
/// Everything listed comes from Windows: Start menu shortcuts, store apps, the user's files.
/// </summary>
public sealed partial class StartMenuWindow : Window
{
    private const double ColumnsWidthDip = 300 + 8 + 340;
    private const double MenuHeightDip = 400;
    private const double PadDip = 4;
    private const double GapToBarDip = 8;

    // Radius scale: 16 large surfaces, 10 medium controls, 8 small inner buttons; search is a capsule.
    private const double LargeRadius = Radii.Large;
    private const double MediumRadius = Radii.Medium;
    private const double SmallRadius = Radii.Small;
    private const double SearchRadius = 20;

    private const double RowIconDip = 24;
    private const double TileIconDip = 32;
    private const int MaxFileResults = 40;

    // Opening and closing (Visuals/Motion.cs): Start rises from the bar's side and fades in,
    // and sinks back and fades out, as Windows' Start does.
    private const float OpenFromDip = 40;
    private const int OpenMs = 250;
    private const float CloseToDip = 24;
    private const int CloseMs = 150;

    private static readonly string UnpinGlyph = ((char)0xE77A).ToString();
    private static readonly string MoreGlyph = ((char)0xE712).ToString();
    private static readonly string FileGlyph = ((char)0xE8A5).ToString();

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly ShellHost _host;
    private readonly nint _hwnd;
    private readonly WindowCutter _cutter;
    private readonly DispatcherQueueTimer _fileTimer;
    private readonly bool _hasTouch;
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppRow> _appRows = new();
    private readonly List<(PinnedApp Pin, FrameworkElement Tile)> _pinTiles = new();
    private readonly ResultActions _actions;
    private readonly FilePreview _preview;

    private WindowSubclass? _subclass;
    private CancellationTokenSource? _fileSearch;
    private bool _open;
    private bool _dark = true;
    private string _fillLook = string.Empty;
    private long _closedByFocusLossAt;
    private bool _filesMode;
    private string _query = string.Empty;
    private string _folderLook = string.Empty;
    private int _popupsOpen;
    private AppRow? _asking;

    internal StartMenuWindow(ShellHost host)
    {
        _host = host;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hasTouch = User32.GetSystemMetrics(User32.SM_MAXIMUMTOUCHES) > 0;

        // Text from the string table (the XAML's own text is the English fallback).
        SearchInput.PlaceholderText = Strings.StartSearchPlaceholder;
        AutomationProperties.SetName(SearchInput, Strings.SearchBoxName);
        PinnedHeader.Text = Strings.StartPinned;
        AppsHeader.Text = Strings.StartAllApps;
        AutomationProperties.SetName(UserButton, Strings.StartAccountSettings);
        ToolTipService.SetToolTip(UserButton, Strings.StartAccountSettings);
        AutomationProperties.SetName(PowerButton, Strings.StartPower);
        ToolTipService.SetToolTip(PowerButton, Strings.StartPower);

        SeeThrough.Prepare(this, _hwnd, alwaysOnTop: true);
        _subclass = new WindowSubclass(_hwnd, OnWindowMessage);

        _cutter = new WindowCutter(_hwnd, Root);
        _cutter.Add(SearchBox, SearchRadius);
        _cutter.Add(PinnedBox, LargeRadius);
        _cutter.Add(AppsBox, LargeRadius);
        _cutter.Add(UserStrip, LargeRadius);
        Layout.LayoutUpdated += (_, _) => _cutter.Update();

        // The search box is a bare field inside its capsule: no fill or border of its own.
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (string key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
                 })
        {
            SearchInput.Resources[key] = clear;
        }

        _actions = new ResultActions(host, _hwnd, HideMenu, () => _host.Catalog.RefreshIfStale(ListIconPixels, _dark));
        _preview = new FilePreview(_actions, Scale);
        PreviewHost.Children.Add(_preview);

        _fileTimer = DispatcherQueue.CreateTimer();
        _fileTimer.Interval = TimeSpan.FromMilliseconds(250);
        _fileTimer.IsRepeating = false;
        _fileTimer.Tick += (_, _) => _ = RunFileSearchAsync();

        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _subclass?.Dispose();
            _subclass = null;
        };

        _host.Catalog.Changed += OnCatalogChanged;
        _host.StartPins.Changed += OnPinsChanged;
        _host.Settings.Changed += OnSettingsChanged;

        ApplyLook();
        BuildFolders();
        LoadUser();
        RebuildApps();
    }

    /// <summary>One All apps row: the app, its "..." button, and room for the uninstall question.</summary>
    private sealed class AppRow
    {
        internal AppRow(AppEntry app, Grid row, Button main, Button more)
        {
            App = app;
            Row = row;
            Main = main;
            More = more;
        }

        internal AppEntry App { get; }

        internal Grid Row { get; }

        internal Button Main { get; }

        internal Button More { get; }

        internal FrameworkElement? Question { get; set; }
    }

    internal bool IsOpen => _open;

    /// <summary>
    /// Raised on the UI thread when the menu opens (true) or closes (false). The bar's Start
    /// button keeps its pressed picture while the menu is open (Settings > Start > Start button).
    /// </summary>
    internal static event Action<bool>? OpenChanged;

    /// <summary>Icon size for the app list, in physical pixels (also used to build the catalog).</summary>
    internal int ListIconPixels => IconService.PickSize((int)Math.Ceiling(TileIconDip * Scale()));

    internal bool IsDark => _dark;

    /// <summary>
    /// The Start button. Pressing it while the menu is open first moves focus to the bar,
    /// which closes the menu (see OnActivated) before the click arrives. A click that comes
    /// right after such a close is that same click: it means "close", so the menu stays shut.
    /// </summary>
    internal void Toggle()
    {
        const long SameClickMs = 500;

        if (_open)
        {
            HideMenu();
        }
        else if (Environment.TickCount64 - _closedByFocusLossAt < SameClickMs)
        {
            _closedByFocusLossAt = 0;
        }
        else
        {
            Open();
        }
    }

    /// <summary>The Win key: open or close, with no click to account for (see <see cref="Toggle"/>).</summary>
    internal void ToggleFromKeyboard()
    {
        if (_open)
        {
            HideMenu();
        }
        else
        {
            Open();
        }
    }

    /// <summary>Opens the menu above the bar with the search box focused. Does nothing extra when already open.</summary>
    internal void Open()
    {
        try
        {
            if (_open)
            {
                SearchInput.Focus(FocusState.Programmatic);
                return;
            }

            _host.Catalog.RefreshIfStale(ListIconPixels, _dark);

            SearchInput.Text = string.Empty;
            ParseQuery();
            ApplyFilter();
            ResetPowerPanel();
            PlaceAboveBar();
            _cutter.Invalidate();

            // Animated: the cut is lifted while the boxes move (else their edges would clip),
            // and the motion starts before the window shows, so its first frame is already the
            // start of it. Still on screen means it was closing: it turns back from where it is.
            Layout.IsHitTestVisible = true;
            if (Motion.IsOn(_host.Settings.Current))
            {
                _cutter.Suspend();
                Action settled = () =>
                {
                    if (_open)
                    {
                        _cutter.Resume();
                    }
                };

                if (User32.IsWindowVisible(_hwnd))
                {
                    Motion.Return(Layout, OpenMs, settled);
                }
                else
                {
                    Motion.Arrive(Layout, FromBarSide(OpenFromDip), OpenMs, settled);
                }
            }
            else
            {
                Motion.Reset(Layout);
                _cutter.Resume();
            }

            User32.ShowWindow(_hwnd, User32.SW_SHOW);
            Activate();

            // Opened by the Win key, Windows may not hand over the keyboard by itself.
            WindowActivator.BringToFront(_hwnd);
            _open = true;
            Log.Info("Start menu opened.");
            RaiseOpenChanged(true);

            AppsScroller.ChangeView(null, 0, null, true);
            DispatcherQueue.TryEnqueue(() => SearchInput.Focus(FocusState.Programmatic));
        }
        catch (Exception ex)
        {
            Log.Error("The Start menu could not be opened.", ex);
        }
    }

    internal void HideMenu()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _fileSearch?.Cancel();
        if (Motion.IsOn(_host.Settings.Current) && User32.IsWindowVisible(_hwnd))
        {
            // Hidden once it has sunk away; nothing in it takes a click meanwhile.
            Layout.IsHitTestVisible = false;
            _cutter.Suspend();
            Motion.Leave(Layout, FromBarSide(CloseToDip), CloseMs, () =>
            {
                if (!_open)
                {
                    User32.ShowWindow(_hwnd, User32.SW_HIDE);
                }
            });
        }
        else
        {
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
        }

        Log.Info("Start menu closed.");
        RaiseOpenChanged(false);
    }

    /// <summary>A distance towards the bar: down for a bottom bar, up for a top bar.</summary>
    private float FromBarSide(float distanceDip)
    {
        return _host.Settings.Current.DockEdge == DockEdge.Top ? -distanceDip : distanceDip;
    }

    private static void RaiseOpenChanged(bool open)
    {
        try
        {
            OpenChanged?.Invoke(open);
        }
        catch (Exception ex)
        {
            Log.Warn("Start menu: a part that follows it opening and closing failed.", ex);
        }
    }

    // ---------------------------------------------------------------- window plumbing

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // A click anywhere else closes the menu, like Windows' own Start. Start's own menus
        // ("...", power) are windows of their own: focus moving to them is not "elsewhere".
        if (args.WindowActivationState == WindowActivationState.Deactivated && _open
            && _popupsOpen == 0 && !OwnsForeground())
        {
            HideMenu();
            _closedByFocusLossAt = Environment.TickCount64;
        }
    }

    /// <summary>True when the foreground window is Start or one of its menus.</summary>
    private bool OwnsForeground()
    {
        nint foreground = User32.GetForegroundWindow();
        return foreground == _hwnd
            || (foreground != 0 && User32.GetAncestor(foreground, User32.GA_ROOTOWNER) == _hwnd);
    }

    /// <summary>
    /// Opens one of Start's menus. While it is open, Start does not close on focus loss; once it
    /// closes, Start closes too if the click that closed it went to another window.
    /// </summary>
    private void ShowPopup(FlyoutBase menu, FrameworkElement target, FlyoutShowOptions options)
    {
        menu.Opening += (_, _) => _popupsOpen++;
        menu.Closed += (_, _) =>
        {
            _popupsOpen = Math.Max(0, _popupsOpen - 1);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_open && _popupsOpen == 0 && !OwnsForeground())
                {
                    HideMenu();
                    _closedByFocusLossAt = Environment.TickCount64;
                }
            });
        };
        menu.ShowAt(target, options);
    }

    private bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        return SeeThrough.TryHandleErase(_hwnd, message, wParam, out result);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        if (_asking is AppRow row)
        {
            EndQuestion(row);
        }
        else if (PowerPanel.Children.Count > 0)
        {
            ResetPowerPanel();
        }
        else
        {
            HideMenu();
        }
    }

    /// <summary>
    /// Lines the boxes up with the bar the way the bar is aligned, 8 DIP above it (below a
    /// top bar). The same rule places the calendar and quick settings (PopupPlacement).
    /// </summary>
    private void PlaceAboveBar()
    {
        double scale = Scale();
        int pad = (int)Math.Round(PadDip * scale);
        int gap = (int)Math.Round(GapToBarDip * scale);
        int width = (int)Math.Round((ColumnsWidthDip + (2 * PadDip)) * scale);
        int height = (int)Math.Round((MenuHeightDip + (2 * PadDip)) * scale);
        AppWindow.MoveAndResize(PopupPlacement.BesideBar(_host, width, height, pad, gap));
    }

    private double Scale()
    {
        uint dpi = User32.GetDpiForWindow(_hwnd);
        return (dpi == 0 ? 96 : dpi) / 96.0;
    }

    // ---------------------------------------------------------------- look

    private void OnSettingsChanged()
    {
        ApplyLook();
        BuildFolders();
    }

    private void ApplyLook()
    {
        ShellSettings settings = _host.Settings.Current;
        Root.RequestedTheme = settings.Theme switch
        {
            ThemeMode.Dark => ElementTheme.Dark,
            ThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };

        _dark = settings.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };

        // Replace the fills only when their look changes, not on every settings change.
        string fillLook = settings.EnableAcrylic + "|" + settings.Opacity.ToString(CultureInfo.InvariantCulture) + "|" + _dark;
        if (fillLook == _fillLook)
        {
            return;
        }

        _fillLook = fillLook;
        SetFill(SearchBox, settings, SearchRadius);
        SetFill(PinnedBox, settings, LargeRadius);
        SetFill(AppsBox, settings, LargeRadius);
        SetFill(UserStrip, settings, LargeRadius);
    }

    /// <summary>Puts a fresh fill behind the box's content (replacing an earlier one).</summary>
    private void SetFill(Grid box, ShellSettings settings, double radius)
    {
        if (box.Children.Count > 0 && box.Children[0] is FrameworkElement { Tag: "fill" } old)
        {
            // Detached on the UI thread before it is dropped.
            SurfaceFill.Release(old);
            box.Children.RemoveAt(0);
        }

        FrameworkElement fill = SurfaceFill.Create(settings, _dark, radius);
        fill.Tag = "fill";
        box.Children.Insert(0, fill);
    }

    // ---------------------------------------------------------------- user strip and power

    private void LoadUser()
    {
        string name = UserInfo.DisplayName();
        UserName.Text = name;
        Avatar.DisplayName = name;
        AutomationProperties.SetName(Avatar, name);
        _ = LoadAvatarAsync();
    }

    private async Task LoadAvatarAsync()
    {
        try
        {
            string? path = await Task.Run(() => UserInfo.AccountPicturePath());
            if (path is null)
            {
                // No picture set: PersonPicture shows the name's initials.
                return;
            }

            Avatar.ProfilePicture = await LoadBitmapAsync(await File.ReadAllBytesAsync(path), 64);
        }
        catch (Exception ex)
        {
            Log.Warn("The account picture could not be shown.", ex);
        }
    }

    /// <summary>The picture and name: Windows' Settings at Accounts > Your info.</summary>
    private void OnUserClicked(object sender, RoutedEventArgs e)
    {
        HideMenu();
        Launcher.Start("ms-settings:yourinfo");
    }

    /// <summary>
    /// The power menu: the entries chosen in Settings > Start that this PC offers right now,
    /// then, always, Exit Machitan (it cannot be turned off, so Machitan can always be closed
    /// from Start).
    /// </summary>
    private void OnPowerClicked(object sender, RoutedEventArgs e)
    {
        if (PowerPanel.Children.Count > 0)
        {
            ResetPowerPanel();
            return;
        }

        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        List<PowerChoice> choices = PowerActions.Choices(_host.Settings.Current);
        foreach (PowerChoice choice in choices)
        {
            var item = new MenuFlyoutItem { Text = choice.Label };
            item.Click += (_, _) =>
            {
                if (choice.Ask is string question)
                {
                    ConfirmPower(choice, question);
                }
                else
                {
                    choice.Run(_hwnd);
                    HideMenu();
                }
            };
            menu.Items.Add(item);
        }

        if (choices.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = Strings.StartPowerNothing, IsEnabled = false });
        }

        // No question, as on the bar's own menu; Machitan's exit closes Start itself.
        menu.Items.Add(new MenuFlyoutSeparator());
        var exit = new MenuFlyoutItem { Text = Strings.BarExit };
        exit.Click += (_, _) =>
        {
            Log.Info("Start: Exit Machitan chosen from the power menu.");
            _host.Shutdown();
        };
        menu.Items.Add(exit);

        ShowPopup(menu, PowerButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedRight });
    }

    /// <summary>Inline confirmation: the strip asks once more before anything happens.</summary>
    private void ConfirmPower(PowerChoice choice, string question)
    {
        PowerPanel.Children.Clear();
        FolderPanel.Visibility = Visibility.Collapsed;
        PowerPanel.Children.Add(new TextBlock { Text = question, VerticalAlignment = VerticalAlignment.Center });

        Button confirm = MakeTextButton(choice.Label, () => choice.Run(_hwnd));
        ResultActions.PaintAccent(confirm, _host.Accent.Current);

        PowerPanel.Children.Add(confirm);
        PowerPanel.Children.Add(MakeTextButton(Strings.Cancel, ResetPowerPanel));
        confirm.Focus(FocusState.Programmatic);
    }

    private void ResetPowerPanel()
    {
        PowerPanel.Children.Clear();
        FolderPanel.Visibility = Visibility.Visible;
    }

    /// <summary>The folder buttons left of the power button, in the order set in Settings; rebuilt when their settings change.</summary>
    private void BuildFolders()
    {
        ShellSettings settings = _host.Settings.Current;
        List<StartFolder> folders = StartFolders.InOrder(settings);
        var look = new System.Text.StringBuilder();
        foreach (StartFolder folder in folders)
        {
            look.Append(folder.Id).Append(folder.On(settings) ? '1' : '0');
        }

        if (look.ToString() == _folderLook)
        {
            return;
        }

        _folderLook = look.ToString();
        FolderPanel.Children.Clear();
        foreach (StartFolder folder in folders)
        {
            if (!folder.On(settings))
            {
                continue;
            }

            var button = new Button
            {
                Content = StartFolders.Icon(folder, 16),
                Width = 36,
                Height = 32,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(SmallRadius),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(button, folder.Name);
            ToolTipService.SetToolTip(button, folder.Name);
            string? target = folder.Target;
            button.Click += (_, _) =>
            {
                HideMenu();
                if (target is null)
                {
                    _host.OpenSettings();
                }
                else
                {
                    OpenFolder(target);
                }
            };
            FolderPanel.Children.Add(button);
        }
    }

    /// <summary>Settings by its link, File Explorer by name, the folders by their shell names in File Explorer.</summary>
    private static void OpenFolder(string target)
    {
        if (!target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            Launcher.Start(target);
            return;
        }

        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(target);
            using Process? explorer = Process.Start(start);
        }
        catch (Exception ex)
        {
            Log.Error("Start: could not open " + target, ex);
        }
    }

    private static Button MakeTextButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(SmallRadius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------------------------------------------------------- search

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ParseQuery();
        ApplyFilter();

        if (_filesMode)
        {
            _fileTimer.Stop();
            _fileTimer.Start();
        }
        else
        {
            _fileSearch?.Cancel();
        }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        StackPanel panel = _filesMode ? FilesPanel : AppsPanel;
        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility == Visibility.Visible && child is FrameworkElement { Tag: string target })
            {
                LaunchAndClose(target);
                return;
            }
        }
    }

    private void ParseQuery()
    {
        string text = SearchInput.Text.Trim();
        if (text.StartsWith("files:", StringComparison.OrdinalIgnoreCase))
        {
            _filesMode = true;
            _query = text.Substring("files:".Length).Trim();
        }
        else if (text.StartsWith("apps:", StringComparison.OrdinalIgnoreCase))
        {
            _filesMode = false;
            _query = text.Substring("apps:".Length).Trim();
        }
        else
        {
            _filesMode = false;
            _query = text;
        }
    }

    private const int NoMatch = SearchRank.NoMatch;

    /// <summary>How well a name fits what was typed (see SearchRank; shared with the bar's search box).</summary>
    private int Rank(string name) => SearchRank.Of(name, _query);

    /// <summary>Best fit first; equal fits keep their usual order (pin order, or A to Z).</summary>
    private static int ByRank((int Rank, int Index) a, (int Rank, int Index) b)
    {
        return a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index);
    }

    /// <summary>Shows only matching pins and apps (or the file results), with honest empty states.</summary>
    private void ApplyFilter()
    {
        PinnedPanel.Children.Clear();
        var pins = new List<(int Rank, int Index)>();
        for (int i = 0; i < _pinTiles.Count; i++)
        {
            int rank = Rank(_pinTiles[i].Pin.Name);
            if (rank != NoMatch)
            {
                pins.Add((rank, i));
            }
        }

        pins.Sort(ByRank);
        foreach (var (_, index) in pins)
        {
            PinnedPanel.Children.Add(_pinTiles[index].Tile);
        }

        if (_pinTiles.Count == 0)
        {
            ShowNote(PinnedEmpty, Strings.StartNoPins);
        }
        else if (PinnedPanel.Children.Count == 0)
        {
            ShowNote(PinnedEmpty, Strings.StartNoPinMatch);
        }
        else
        {
            PinnedEmpty.Visibility = Visibility.Collapsed;
        }

        if (_filesMode)
        {
            AppsHeader.Text = Strings.SearchFiles;
            AppsPanel.Visibility = Visibility.Collapsed;
            FilesPanel.Visibility = Visibility.Visible;
            if (_query.Length < 2)
            {
                FilesPanel.Children.Clear();
                ShowPreview(null);
                ShowNote(AppsEmpty, Strings.StartFilesHint);
            }

            return;
        }

        ShowPreview(null);

        AppsHeader.Text = _query.Length == 0 ? Strings.StartAllApps : Strings.StartResults;
        AppsPanel.Visibility = Visibility.Visible;
        FilesPanel.Visibility = Visibility.Collapsed;

        int shown = 0;
        var order = new List<(int Rank, int Index)>(_appRows.Count);
        for (int i = 0; i < _appRows.Count; i++)
        {
            AppRow row = _appRows[i];
            int rank = Rank(row.App.Name);
            bool match = rank != NoMatch;
            row.Row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match)
            {
                shown++;
            }

            order.Add((rank, i));
        }

        // Best fit at the top (Enter opens the first one); rows are moved only when the order changes.
        order.Sort(ByRank);
        bool sameOrder = AppsPanel.Children.Count == order.Count;
        for (int i = 0; sameOrder && i < order.Count; i++)
        {
            sameOrder = ReferenceEquals(AppsPanel.Children[i], _appRows[order[i].Index].Row);
        }

        if (!sameOrder)
        {
            AppsPanel.Children.Clear();
            foreach (var (_, index) in order)
            {
                AppsPanel.Children.Add(_appRows[index].Row);
            }
        }

        if (!_host.Catalog.IsReady)
        {
            ShowNote(AppsEmpty, Strings.SearchReadingApps);
        }
        else if (shown == 0)
        {
            ShowNote(AppsEmpty, _appRows.Count == 0 ? Strings.StartNoApps : Strings.SearchNoAppMatch);
        }
        else
        {
            AppsEmpty.Visibility = Visibility.Collapsed;
        }
    }

    private static void ShowNote(TextBlock note, string text)
    {
        note.Text = text;
        note.Visibility = Visibility.Visible;
    }

    private async Task RunFileSearchAsync()
    {
        _fileSearch?.Cancel();
        if (!_filesMode || _query.Length < 2)
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        _fileSearch = cancel;
        string query = _query;

        FilesPanel.Children.Clear();
        ShowPreview(null);
        ShowNote(AppsEmpty, Strings.SearchSearchingFiles);

        List<string> files;
        try
        {
            files = await Task.Run(() => FileSearch.Find(query, MaxFileResults, cancel.Token));
        }
        catch (Exception ex)
        {
            Log.Warn("File search failed.", ex);
            files = new List<string>();
        }

        // A newer search, or the menu closing, replaces this one.
        if (cancel.IsCancellationRequested || !_filesMode || query != _query)
        {
            return;
        }

        foreach (string file in files)
        {
            FilesPanel.Children.Add(CreateFileRow(file));
        }

        if (files.Count == 0)
        {
            ShowNote(AppsEmpty, Strings.SearchNoFileMatch);
        }
        else
        {
            AppsEmpty.Visibility = Visibility.Collapsed;
            ShowPreview(files[0]);
        }
    }

    /// <summary>The file's preview in place of Pinned (the first result, or the one pointed at); null shows Pinned again.</summary>
    private void ShowPreview(string? path)
    {
        if (path is null)
        {
            _preview.Clear();
            PreviewHost.Visibility = Visibility.Collapsed;
            PinnedContent.Visibility = Visibility.Visible;
            return;
        }

        _preview.Show(path);
        PinnedContent.Visibility = Visibility.Collapsed;
        PreviewHost.Visibility = Visibility.Visible;
    }

    private Button CreateFileRow(string path)
    {
        var glyph = new FontIcon { Glyph = FileGlyph, FontFamily = IconFont, FontSize = 18, Width = RowIconDip };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = Path.GetDirectoryName(path) ?? string.Empty,
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(glyph);
        content.Children.Add(text);

        Button row = MakeRowButton(content, Path.GetFileName(path), 48);
        row.Tag = path;
        row.Click += (_, _) => LaunchAndClose(path);

        // The pointed (or keyboard-focused) file is the one previewed, with its actions.
        row.PointerEntered += (_, _) => ShowPreview(path);
        row.GotFocus += (_, _) => ShowPreview(path);
        return row;
    }

    // ---------------------------------------------------------------- All apps

    private void OnCatalogChanged()
    {
        RebuildApps();
    }

    private void RebuildApps()
    {
        try
        {
            AppsPanel.Children.Clear();
            _appRows.Clear();

            foreach (AppEntry app in _host.Catalog.Apps)
            {
                AppRow row = CreateAppRow(app);
                _appRows.Add(row);
                AppsPanel.Children.Add(row.Row);
            }

            RebuildPins();
        }
        catch (Exception ex)
        {
            Log.Error("The All apps list could not be built.", ex);
        }
    }

    private AppRow CreateAppRow(AppEntry app)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(CreateIcon(app.Key, app, null, RowIconDip));
        content.Children.Add(new TextBlock
        {
            Text = app.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        Button main = MakeRowButton(content, app.Name, 40);
        main.Click += (_, _) => LaunchAndClose(app.LaunchTarget);

        var more = new Button
        {
            Content = new FontIcon { Glyph = MoreGlyph, FontFamily = IconFont, FontSize = 14 },
            Width = 32,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = _hasTouch ? 1 : 0,
        };
        AutomationProperties.SetName(more, Fmt.F(Strings.MoreOptionsFor, app.Name));
        ToolTipService.SetToolTip(more, Strings.MoreOptions);

        var row = new Grid { Tag = app.LaunchTarget };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(more, 1);
        row.Children.Add(main);
        row.Children.Add(more);

        var appRow = new AppRow(app, row, main, more);
        more.Click += (_, _) => ShowAppMenu(appRow, more, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        main.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowAppMenu(appRow, main, e.TryGetPosition(main, out Windows.Foundation.Point at)
                ? new FlyoutShowOptions { Position = at }
                : new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        };

        // "..." button: hover only (and while it has keyboard focus); always on touch devices.
        row.PointerEntered += (_, _) => more.Opacity = 1;
        row.PointerExited += (_, _) => more.Opacity = _hasTouch || more.FocusState != FocusState.Unfocused ? 1 : 0;
        more.GotFocus += (_, _) => more.Opacity = 1;
        more.LostFocus += (_, _) => more.Opacity = _hasTouch ? 1 : 0;

        return appRow;
    }

    private void ShowAppMenu(AppRow row, FrameworkElement target, FlyoutShowOptions options)
    {
        try
        {
            MenuFlyout menu = _actions.AppMenu(row.App, () => AskUninstall(row), ListIconPixels / 2);
            ShowPopup(menu, target, options);
        }
        catch (Exception ex)
        {
            Log.Error("Start: the menu for " + row.App.Name + " could not be opened.", ex);
        }
    }

    /// <summary>The store-app uninstall question, in place of the row (one row at a time).</summary>
    private void AskUninstall(AppRow row)
    {
        if (_asking is AppRow other)
        {
            EndQuestion(other);
        }

        _asking = row;
        Grid question = _actions.UninstallQuestion(row.App, () => EndQuestion(row));
        Grid.SetColumnSpan(question, 2);
        row.Question = question;
        row.Main.Visibility = Visibility.Collapsed;
        row.More.Visibility = Visibility.Collapsed;
        row.Row.Children.Add(question);
    }

    /// <summary>From a pinned tile: the question goes on the app's All apps row, shown unfiltered.</summary>
    private void AskUninstallFromPin(AppEntry app)
    {
        AppRow? row = _appRows.Find(r => ReferenceEquals(r.App, app));
        if (row is null)
        {
            return;
        }

        if (SearchInput.Text.Length > 0)
        {
            SearchInput.Text = string.Empty;
            ParseQuery();
            ApplyFilter();
        }

        AskUninstall(row);
        row.Row.StartBringIntoView();
    }

    private void EndQuestion(AppRow row)
    {
        if (row.Question is FrameworkElement question)
        {
            row.Row.Children.Remove(question);
        }

        row.Question = null;
        row.Main.Visibility = Visibility.Visible;
        row.More.Visibility = Visibility.Visible;
        if (ReferenceEquals(_asking, row))
        {
            _asking = null;
        }
    }

    /// <summary>A full-width row: content on the left, no tile at rest, a background on hover.</summary>
    private static Button MakeRowButton(UIElement content, string name, double height)
    {
        var button = new Button
        {
            Content = content,
            Height = height,
            MinHeight = 0,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(MediumRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    // ---------------------------------------------------------------- Pinned

    private void OnPinsChanged()
    {
        RebuildPins();
    }

    private void RebuildPins()
    {
        _pinTiles.Clear();

        var byPinPath = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry app in _host.Catalog.Apps)
        {
            byPinPath.TryAdd(app.ToPin().Path, app);
        }

        foreach (PinnedApp pin in _host.StartPins.Pins)
        {
            byPinPath.TryGetValue(pin.Path, out AppEntry? app);
            _pinTiles.Add((pin, CreatePinTile(pin, app)));
        }

        ApplyFilter();
    }

    private Grid CreatePinTile(PinnedApp pin, AppEntry? app)
    {
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(CreateIcon(app?.Key ?? pin.Path, app, pin.AppId is null ? pin.Path : null, TileIconDip));
        content.Children.Add(new TextBlock
        {
            Text = pin.Name,
            FontSize = 11,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var main = new Button
        {
            Content = content,
            Width = 64,
            Height = 72,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(2, 6, 2, 2),
            CornerRadius = new CornerRadius(MediumRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(main, pin.Name);
        main.Click += (_, _) => LaunchAndClose(pin.LaunchTarget);

        var unpin = new Button
        {
            Content = new FontIcon { Glyph = UnpinGlyph, FontFamily = IconFont, FontSize = 10 },
            Width = 20,
            Height = 20,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = _hasTouch ? 1 : 0,
        };
        AutomationProperties.SetName(unpin, Fmt.F(Strings.UnpinXFromStart, pin.Name));
        ToolTipService.SetToolTip(unpin, Strings.UnpinFromStart);
        unpin.Click += (_, _) => _host.StartPins.Unpin(pin.Path);

        // Right-click: the same menu as in All apps (for apps still in the app list).
        if (app is not null)
        {
            main.ContextRequested += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    MenuFlyout menu = _actions.AppMenu(app, () => AskUninstallFromPin(app), ListIconPixels / 2);
                    ShowPopup(menu, main, e.TryGetPosition(main, out Windows.Foundation.Point at)
                        ? new FlyoutShowOptions { Position = at }
                        : new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
                }
                catch (Exception ex)
                {
                    Log.Error("Start: the menu for " + pin.Name + " could not be opened.", ex);
                }
            };
        }

        var tile = new Grid { Width = 64, Height = 72 };
        tile.Children.Add(main);
        tile.Children.Add(unpin);

        // Unpin button: hover only (and while it has keyboard focus); always on touch devices.
        tile.PointerEntered += (_, _) => unpin.Opacity = 1;
        tile.PointerExited += (_, _) => unpin.Opacity = _hasTouch || unpin.FocusState != FocusState.Unfocused ? 1 : 0;
        unpin.GotFocus += (_, _) => unpin.Opacity = 1;
        unpin.LostFocus += (_, _) => unpin.Opacity = _hasTouch ? 1 : 0;

        return tile;
    }

    // ---------------------------------------------------------------- icons

    /// <summary>
    /// The app's real icon: from the catalog (shortcut icon or store logo), else from the pinned
    /// exe, else the first letter of the name. Images are cached for the run.
    /// </summary>
    private FrameworkElement CreateIcon(string cacheKey, AppEntry? app, string? exePath, double sizeDip)
    {
        var image = new Image
        {
            Width = sizeDip,
            Height = sizeDip,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (_icons.TryGetValue(cacheKey, out ImageSource? cached))
        {
            image.Source = cached;
            return image;
        }

        try
        {
            if (app?.Icon is IconPixels pixels)
            {
                image.Source = Remember(cacheKey, ToBitmap(pixels));
                return image;
            }

            if (app?.LogoBytes is byte[] bytes)
            {
                _ = SetLogoAsync(image, cacheKey, bytes);
                return image;
            }

            if (exePath is not null && File.Exists(exePath)
                && IconReader.FromExecutable(exePath, ListIconPixels) is IconPixels exeIcon)
            {
                image.Source = Remember(cacheKey, ToBitmap(exeIcon));
                return image;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("An app icon could not be shown.", ex);
        }

        string name = app?.Name ?? Path.GetFileNameWithoutExtension(cacheKey);
        return new TextBlock
        {
            Text = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?",
            Width = sizeDip,
            FontSize = sizeDip * 0.62,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    private async Task SetLogoAsync(Image image, string cacheKey, byte[] bytes)
    {
        try
        {
            if (!_icons.TryGetValue(cacheKey, out ImageSource? source))
            {
                source = Remember(cacheKey, await LoadBitmapAsync(bytes, ListIconPixels));
            }

            image.Source = source;
        }
        catch (Exception ex)
        {
            Log.Warn("A store-app logo could not be shown.", ex);
        }
    }

    private ImageSource Remember(string cacheKey, ImageSource source)
    {
        _icons[cacheKey] = source;
        return source;
    }

    internal static async Task<BitmapImage> LoadBitmapAsync(byte[] bytes, int decodePixels)
    {
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = decodePixels,
            DecodePixelType = DecodePixelType.Physical,
        };

        using var memory = new InMemoryRandomAccessStream();
        await memory.WriteAsync(bytes.AsBuffer());
        memory.Seek(0);
        await bitmap.SetSourceAsync(memory);
        return bitmap;
    }

    internal static WriteableBitmap ToBitmap(IconPixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels.Bgra, 0, pixels.Bgra.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    // ---------------------------------------------------------------- launching

    private void LaunchAndClose(string target)
    {
        HideMenu();
        Launcher.Start(target);
    }
}
