using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
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
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using VirtualKey = Windows.System.VirtualKey;

namespace Machitan.Start;

/// <summary>
/// The Start menu: separate floating boxes on a see-through window, 480 DIP tall.
/// Top: the user's box: picture and a greeting with the first name (opens Windows' account
/// settings), the folder buttons chosen in Settings, and the power button (its menu, with
/// icons, holds Lock, the entries chosen in Settings that this PC offers, and always Exit
/// Machitan).
/// Left: search (focused on open, filters both lists) over the pinned apps ("Pin to Start";
/// unpin on hover) in categories the user names, orders and adds (a category's menu: right-
/// click its name or its "..."; an app's "Move to"). Pinned apps drag to any place in any
/// category; an app dragged from All apps is pinned where it is dropped (and stays in All
/// apps). While searching, the matching pins show best first without categories; while
/// searching files, a preview of the pointed file takes their place.
/// Right: All apps: Most used first (Settings, off by default; folds away), then A to Z under
/// letters; a letter opens the letters' grid to jump to another, as in Windows' Start. Each
/// row's "..." button (on hover, always on touch devices, or a right-click) opens Windows'
/// Start menu entries and the app's jump list tasks.
/// No recent items, no widgets.
///
/// Search scopes: "apps:" (apps only), "files:", and the default: apps and actions together,
/// best fit first (the calculator's answer on top, Settings pages, Windows tools, power; see
/// SearchActions; one that asks first asks in the strip at the top, as the power menu does).
/// There is deliberately no web scope.
/// Everything listed comes from Windows: Start menu shortcuts, store apps, the user's files;
/// Most used is Machitan's own count of apps opened from Start (AppUsage).
/// </summary>
public sealed partial class StartMenuWindow : Window
{
    private const double ColumnsWidthDip = 376 + 8 + 276;
    private const double MenuHeightDip = 480;
    private const double PadDip = 4;
    private const double GapToBarDip = 8;

    // Radius scale: 16 large surfaces, 10 medium controls, 8 small inner buttons; search is a capsule.
    private const double LargeRadius = Radii.Large;
    private const double MediumRadius = Radii.Medium;
    private const double SmallRadius = Radii.Small;
    private const double SearchRadius = 20;

    private const double RowIconDip = 24;
    private const double TileIconDip = 32;
    private const double TileWidthDip = 84;
    private const double TileHeightDip = 88;

    // Dragging (pinned apps, and apps from All apps into Pinned): starts once the pointer moves
    // this far; the pinned box scrolls while the pointer is near its top or bottom.
    private const double DragStartDip = 6;
    private const double DimmedOpacity = 0.35;
    private const double DragScrollEdgeDip = 28;
    private const double DragScrollStepDip = 14;
    private const int MaxFileResults = 40;
    private const int MostUsedCount = 5;
    private const int CategoryNameMax = 40;

    // Opening and closing (Visuals/Motion.cs): Start rises from the bar's side and fades in,
    // and sinks back and fades out, as Windows' Start does.
    private const float OpenFromDip = 40;
    private const int OpenMs = 250;
    private const float CloseToDip = 24;
    private const int CloseMs = 150;

    private static readonly string UnpinGlyph = ((char)0xE77A).ToString();
    private static readonly string MoreGlyph = ((char)0xE712).ToString();
    private static readonly string FileGlyph = ((char)0xE8A5).ToString();
    private static readonly string ExpandedGlyph = ((char)0xE70D).ToString();
    private static readonly string FoldedGlyph = ((char)0xE76C).ToString();

    private const int RenameGlyph = 0xE8AC;
    private const int AddGlyph = 0xE710;
    private const int UpGlyph = 0xE74A;
    private const int DownGlyph = 0xE74B;
    private const int RemoveGlyph = 0xE74D;
    private const int MoveGlyph = 0xE8DE;
    private const int UnpinMenuGlyph = 0xE77A;

    /// <summary>The letters of All apps, in Windows' order: "#" (digits, signs and other scripts), then A to Z.</summary>
    private static readonly string[] Letters =
    {
        "#", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
    };

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly ShellHost _host;
    private readonly nint _hwnd;
    private readonly WindowCutter _cutter;
    private readonly DispatcherQueueTimer _fileTimer;
    private readonly bool _hasTouch;
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppRow> _appRows = new();
    private readonly List<(PinnedApp Pin, FrameworkElement Tile)> _pinTiles = new();
    private readonly List<PinSection> _pinSections = new();
    private readonly List<UIElement> _alphaChildren = new();
    private readonly Dictionary<string, Button> _letterHeaders = new(StringComparer.Ordinal);
    private readonly ResultActions _actions;
    private readonly FilePreview _preview;

    private WindowSubclass? _subclass;
    private CancellationTokenSource? _fileSearch;
    private bool _open;
    private bool _dark = true;
    private string _fillLook = string.Empty;
    private long _closedByFocusLossAt;
    private bool _filesMode;

    /// <summary>"apps:" was typed: apps only, no actions.</summary>
    private bool _appsOnly;
    private string _query = string.Empty;
    private string _folderLook = string.Empty;
    private int _popupsOpen;
    private AppRow? _asking;
    private string _firstName = string.Empty;
    private bool _mostUsedShown;
    private bool _mostUsedOn;
    private StackPanel? _mostUsedRows;
    private DispatcherQueueTimer? _renameTimer;
    private StartPress? _press;
    private StartDrag? _drag;
    private DispatcherQueueTimer? _dragTimer;
    private FontIcon? _mostUsedChevron;
    private Button? _mostUsedHeader;

    internal StartMenuWindow(ShellHost host)
    {
        _host = host;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hasTouch = User32.GetSystemMetrics(User32.SM_MAXIMUMTOUCHES) > 0;

        // Text from the string table (the XAML's own text is the English fallback).
        SearchInput.PlaceholderText = Strings.StartSearchPlaceholder;
        AutomationProperties.SetName(SearchInput, Strings.SearchBoxName);
        PinnedHeader.Text = Upper(Strings.StartPinned);
        AppsHeader.Text = Upper(Strings.StartAllApps);
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

        // While dragging, moves over Start redraw at once (the drag timer covers the rest).
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnRootDragMoved), true);
        Root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnRootDragReleased), true);

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

    /// <summary>One category of the pinned apps: its name row, its tiles, and the note shown while it is empty.</summary>
    private sealed class PinSection
    {
        internal PinSection(PinGroup group, StackPanel panel, Grid header, TextBlock title, VariableSizedWrapGrid grid, TextBlock empty)
        {
            Group = group;
            Panel = panel;
            Header = header;
            Title = title;
            TileGrid = grid;
            Empty = empty;
        }

        internal PinGroup Group { get; }

        internal StackPanel Panel { get; }

        internal Grid Header { get; }

        internal TextBlock Title { get; }

        internal VariableSizedWrapGrid TileGrid { get; }

        internal TextBlock Empty { get; }

        /// <summary>Indexes into _pinTiles, in pin order.</summary>
        internal List<int> Tiles { get; } = new();
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
            UserName.Text = Greeting();
            HideLetterGrid();
            RebuildMostUsed();
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

        _press = null;
        EndDrag(apply: false);
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
        if (_drag is not null)
        {
            EndDrag(apply: false);
        }
        else if (LetterGrid.Visibility == Visibility.Visible)
        {
            HideLetterGrid();
        }
        else if (_asking is AppRow row)
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
        ShellSettings settings = _host.Settings.Current;
        _host.Usage.SetOn(settings.StartMostUsed);
        if (settings.StartMostUsed != _mostUsedOn)
        {
            RebuildMostUsed();
            ApplyFilter();
        }
        else
        {
            ApplyMostUsedFold();
        }
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
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _firstName = words.Length > 0 ? words[0] : name;
        UserName.Text = Greeting();
        Avatar.DisplayName = name;
        AutomationProperties.SetName(Avatar, name);
        _ = LoadAvatarAsync();
    }

    /// <summary>"Good evening, Sam": by the time of day now (as Indonesian has it: pagi, siang, sore, malam), with the first name.</summary>
    private string Greeting()
    {
        int hour = DateTime.Now.Hour;
        string format = hour >= 4 && hour < 11 ? Strings.StartGreetMorning
            : hour >= 11 && hour < 15 ? Strings.StartGreetMidday
            : hour >= 15 && hour < 18 ? Strings.StartGreetAfternoon
            : Strings.StartGreetEvening;
        return Fmt.F(format, _firstName);
    }

    private static string Upper(string text) => text.ToUpper(CultureInfo.CurrentUICulture);

    private static FontIcon MenuGlyph(int glyph) => new() { Glyph = ((char)glyph).ToString(), FontFamily = IconFont };

    private static MenuFlyoutItem MenuItem(string text, int glyph, Action onClick, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = MenuGlyph(glyph), IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
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
    /// The power menu: always Lock first; the entries chosen in Settings > Start that this PC
    /// offers right now; then, always, Exit Machitan (it cannot be turned off, so Machitan can
    /// always be closed from Start).
    /// </summary>
    private void OnPowerClicked(object sender, RoutedEventArgs e)
    {
        if (PowerPanel.Children.Count > 0)
        {
            ResetPowerPanel();
            return;
        }

        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };

        // No question, as Win+L asks none; Start goes first, so it is not there after unlocking.
        var lockItem = new MenuFlyoutItem { Text = Strings.PowerLock, Icon = MenuGlyph(PowerActions.LockGlyph) };
        lockItem.Click += (_, _) =>
        {
            HideMenu();
            if (User32.LockWorkStation())
            {
                Log.Info("Start: Lock chosen from the power menu.");
            }
            else
            {
                Log.Warn("Start: Windows did not lock the PC (error " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError() + ").");
            }
        };
        menu.Items.Add(lockItem);

        List<PowerChoice> choices = PowerActions.Choices(_host.Settings.Current);
        foreach (PowerChoice choice in choices)
        {
            var item = new MenuFlyoutItem { Text = choice.Label };
            if (choice.Glyph != 0)
            {
                item.Icon = MenuGlyph(choice.Glyph);
            }

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
        var exit = new MenuFlyoutItem { Text = Strings.BarExit, Icon = MenuGlyph(PowerActions.ExitGlyph) };
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
            if (child.Visibility != Visibility.Visible || child is not FrameworkElement element)
            {
                continue;
            }

            if (element.Tag is AppEntry app)
            {
                LaunchApp(app, app.LaunchTarget);
                return;
            }

            if (element.Tag is SearchAction action)
            {
                RunSearchAction(action);
                return;
            }

            if (element.Tag is string target)
            {
                LaunchAndClose(target);
                return;
            }
        }
    }

    private void ParseQuery()
    {
        string text = SearchInput.Text.Trim();
        _appsOnly = false;
        if (text.StartsWith("files:", StringComparison.OrdinalIgnoreCase))
        {
            _filesMode = true;
            _query = text.Substring("files:".Length).Trim();
        }
        else if (text.StartsWith("apps:", StringComparison.OrdinalIgnoreCase))
        {
            _filesMode = false;
            _appsOnly = true;
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

    /// <summary>
    /// Shows the pins in their categories and All apps under their letters, or, while
    /// searching, only the matches best first (or the file results), with honest empty states.
    /// </summary>
    private void ApplyFilter()
    {
        // A tile sits either in its category or in the search results, never both.
        PinnedPanel.Children.Clear();
        foreach (PinSection section in _pinSections)
        {
            section.TileGrid.Children.Clear();
        }

        bool searching = _query.Length > 0 || _filesMode;
        PinnedHeader.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        bool noPins = _pinTiles.Count == 0 && _drag is null;
        PinnedGroups.Visibility = searching || noPins ? Visibility.Collapsed : Visibility.Visible;
        PinnedPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        if (searching)
        {
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
        }
        else
        {
            foreach (PinSection section in _pinSections)
            {
                foreach (int index in section.Tiles)
                {
                    section.TileGrid.Children.Add(_pinTiles[index].Tile);
                }

                section.Empty.Visibility = section.Tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        if (noPins)
        {
            ShowNote(PinnedEmpty, Strings.StartNoPins);
        }
        else if (searching && PinnedPanel.Children.Count == 0)
        {
            ShowNote(PinnedEmpty, Strings.StartNoPinMatch);
        }
        else
        {
            PinnedEmpty.Visibility = Visibility.Collapsed;
        }

        if (searching)
        {
            HideLetterGrid();
        }

        MostUsedHost.Visibility = !searching && _mostUsedShown ? Visibility.Visible : Visibility.Collapsed;

        if (_filesMode)
        {
            AppsHeader.Text = Upper(Strings.SearchFiles);
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

        AppsHeader.Text = Upper(_query.Length == 0 ? Strings.StartAllApps : Strings.StartResults);
        AppsPanel.Visibility = Visibility.Visible;
        FilesPanel.Visibility = Visibility.Collapsed;

        int shown = 0;
        var wanted = new List<UIElement>(_alphaChildren.Count);
        if (_query.Length == 0)
        {
            // A to Z under the letters.
            foreach (AppRow row in _appRows)
            {
                row.Row.Visibility = Visibility.Visible;
            }

            shown = _appRows.Count;
            wanted.AddRange(_alphaChildren);
        }
        else
        {
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

            // The actions among them (not with "apps:"), ranked the same way; on equal fits the apps come first.
            var actionRows = new List<Button>();
            if (!_appsOnly)
            {
                foreach ((int rank, SearchAction action) in SearchActions.Find(_host, _query))
                {
                    order.Add((rank, _appRows.Count + actionRows.Count));
                    actionRows.Add(CreateActionRow(action));
                }

                shown += actionRows.Count;
            }

            // Best fit at the top (Enter opens the first one), no letters.
            order.Sort(ByRank);
            foreach (var (_, index) in order)
            {
                wanted.Add(index < _appRows.Count ? (UIElement)_appRows[index].Row : actionRows[index - _appRows.Count]);
            }
        }

        // Rows are moved only when the order changes.
        bool sameOrder = AppsPanel.Children.Count == wanted.Count;
        for (int i = 0; sameOrder && i < wanted.Count; i++)
        {
            sameOrder = ReferenceEquals(AppsPanel.Children[i], wanted[i]);
        }

        if (!sameOrder)
        {
            AppsPanel.Children.Clear();
            foreach (UIElement child in wanted)
            {
                AppsPanel.Children.Add(child);
            }
        }

        if (!_host.Catalog.IsReady)
        {
            ShowNote(AppsEmpty, Strings.SearchReadingApps);
        }
        else if (shown == 0)
        {
            // Without "apps:" the actions were searched too.
            ShowNote(AppsEmpty, _appRows.Count == 0 ? Strings.StartNoApps : _appsOnly ? Strings.SearchNoAppMatch : Strings.SearchNothingMatches);
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

    /// <summary>A search action's row: its icon, its name and what kind it is.</summary>
    private Button CreateActionRow(SearchAction action)
    {
        var glyph = new FontIcon { Glyph = ((char)action.Glyph).ToString(), FontFamily = IconFont, FontSize = 18, Width = RowIconDip };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = action.Name,
            FontWeight = action.IsAnswer ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = action.Detail,
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(glyph);
        content.Children.Add(text);

        Button row = MakeRowButton(content, action.Name, 48);
        row.Tag = action;
        row.Click += (_, _) => RunSearchAction(action);
        return row;
    }

    /// <summary>
    /// A search action: Start closes and it is done (the calculator's answer is copied); one that
    /// asks first (shut down, restart, sign out) asks in the strip at the top, as the power menu does.
    /// </summary>
    private void RunSearchAction(SearchAction action)
    {
        if (action.Ask is string question)
        {
            ConfirmPower(new PowerChoice(action.Name, question, action.Run), question);
            return;
        }

        HideMenu();
        action.Run(_hwnd);
    }

    // ---------------------------------------------------------------- All apps

    private void OnCatalogChanged()
    {
        RebuildApps();
    }

    /// <summary>
    /// All apps again from the catalog: one row per app, kept in the catalog's order within
    /// each letter, under the letters in Windows' order ("#" first, then A to Z; a letter
    /// with no apps has no heading). Then Most used and the pins, which use the same apps.
    /// </summary>
    private void RebuildApps()
    {
        try
        {
            HideLetterGrid();
            AppsPanel.Children.Clear();
            _appRows.Clear();
            _alphaChildren.Clear();
            _letterHeaders.Clear();

            var byLetter = new Dictionary<string, List<AppRow>>(StringComparer.Ordinal);
            foreach (AppEntry app in _host.Catalog.Apps)
            {
                AppRow row = CreateAppRow(app);
                _appRows.Add(row);
                string letter = LetterOf(app.Name);
                if (!byLetter.TryGetValue(letter, out List<AppRow>? rows))
                {
                    rows = new List<AppRow>();
                    byLetter[letter] = rows;
                }

                rows.Add(row);
            }

            foreach (string letter in Letters)
            {
                if (!byLetter.TryGetValue(letter, out List<AppRow>? rows))
                {
                    continue;
                }

                Button header = CreateLetterHeader(letter);
                _letterHeaders[letter] = header;
                _alphaChildren.Add(header);
                foreach (AppRow row in rows)
                {
                    _alphaChildren.Add(row.Row);
                }
            }

            foreach (UIElement child in _alphaChildren)
            {
                AppsPanel.Children.Add(child);
            }

            RebuildMostUsed();
            RebuildPins();
        }
        catch (Exception ex)
        {
            Log.Error("The All apps list could not be built.", ex);
        }
    }

    /// <summary>The letter an app name is listed under: its first letter without accents, A to Z, else "#".</summary>
    private static string LetterOf(string name)
    {
        foreach (char c in name.Normalize(NormalizationForm.FormD))
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            char upper = char.ToUpperInvariant(c);
            return upper is >= 'A' and <= 'Z' ? upper.ToString() : "#";
        }

        return "#";
    }

    /// <summary>A letter's heading in All apps: a click opens the letters' grid, to jump to another letter.</summary>
    private Button CreateLetterHeader(string letter)
    {
        var header = new Button
        {
            Content = new TextBlock { Text = letter, FontSize = 13, FontWeight = FontWeights.SemiBold },
            Height = 30,
            MinWidth = 36,
            MinHeight = 0,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = 0.8,
        };
        AutomationProperties.SetName(header, Fmt.F(Strings.StartLetterName, letter));
        ToolTipService.SetToolTip(header, Strings.StartJumpToLetter);
        header.Click += (_, _) => ShowLetterGrid(letter);
        return header;
    }

    /// <summary>
    /// The letters' grid in place of the list, as in Windows' Start: "#" and A to Z, four a
    /// row; letters with no apps are greyed. A click jumps to that letter; Escape goes back.
    /// </summary>
    private void ShowLetterGrid(string current)
    {
        LetterGrid.Children.Clear();
        LetterGrid.RowDefinitions.Clear();
        LetterGrid.ColumnDefinitions.Clear();
        const int Columns = 4;
        int rowCount = (Letters.Length + Columns - 1) / Columns;
        for (int i = 0; i < Columns; i++)
        {
            LetterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < rowCount; i++)
        {
            LetterGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        }

        Button? focus = null;
        for (int i = 0; i < Letters.Length; i++)
        {
            string letter = Letters[i];
            var button = new Button
            {
                Content = letter,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                CornerRadius = new CornerRadius(MediumRadius),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                IsEnabled = _letterHeaders.ContainsKey(letter),
            };
            AutomationProperties.SetName(button, Fmt.F(Strings.StartAppsWithLetter, letter));
            Grid.SetRow(button, i / Columns);
            Grid.SetColumn(button, i % Columns);
            button.Click += (_, _) => JumpToLetter(letter);
            LetterGrid.Children.Add(button);
            if (letter == current)
            {
                focus = button;
            }
        }

        AppsScroller.Visibility = Visibility.Collapsed;
        LetterGrid.Visibility = Visibility.Visible;
        focus?.Focus(FocusState.Programmatic);
    }

    private void HideLetterGrid()
    {
        if (LetterGrid.Visibility == Visibility.Visible)
        {
            LetterGrid.Visibility = Visibility.Collapsed;
            AppsScroller.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Back to the list, scrolled so the letter's heading is at the top (and focused, for the keyboard).</summary>
    private void JumpToLetter(string letter)
    {
        HideLetterGrid();
        if (!_letterHeaders.TryGetValue(letter, out Button? header) || AppsScroller.Content is not UIElement list)
        {
            return;
        }

        try
        {
            AppsScroller.UpdateLayout();
            double top = header.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            AppsScroller.ChangeView(null, Math.Max(0, top - header.Margin.Top), null, true);
            header.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Log.Warn("Start: could not jump to the letter " + letter + ".", ex);
        }
    }

    /// <summary>
    /// Most used (Settings > Start, off by default): the apps opened most from Start, at most
    /// five, above A to Z; its heading folds them away (remembered). On but nothing counted yet:
    /// the heading and a note. Hidden while searching.
    /// </summary>
    private void RebuildMostUsed()
    {
        MostUsedHost.Children.Clear();
        _mostUsedRows = null;
        _mostUsedChevron = null;
        _mostUsedHeader = null;
        _mostUsedOn = _host.Settings.Current.StartMostUsed;
        _mostUsedShown = _mostUsedOn && _host.Catalog.IsReady;
        if (!_mostUsedShown)
        {
            MostUsedHost.Visibility = Visibility.Collapsed;
            return;
        }

        var byKey = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry app in _host.Catalog.Apps)
        {
            byKey.TryAdd(app.Key, app);
        }

        var rows = new StackPanel { Spacing = 2 };
        foreach (string key in _host.Usage.Top(MostUsedCount))
        {
            if (byKey.TryGetValue(key, out AppEntry? app))
            {
                rows.Children.Add(CreateAppRow(app).Row);
            }
        }

        if (rows.Children.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = Strings.StartMostUsedEmpty,
                Margin = new Thickness(8, 0, 8, 4),
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var chevron = new FontIcon { FontFamily = IconFont, FontSize = 10, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = Strings.StartMostUsed, FontSize = 13, FontWeight = FontWeights.SemiBold, Opacity = 0.8 });
        title.Children.Add(chevron);
        var header = new Button
        {
            Content = title,
            Height = 30,
            MinHeight = 0,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(header, Strings.StartMostUsed);
        header.Click += (_, _) =>
        {
            bool fold = !_host.Settings.Current.StartMostUsedCollapsed;
            _host.Settings.Update(s => s.StartMostUsedCollapsed = fold, "Start");
        };

        MostUsedHost.Children.Add(header);
        MostUsedHost.Children.Add(rows);
        _mostUsedRows = rows;
        _mostUsedChevron = chevron;
        _mostUsedHeader = header;
        ApplyMostUsedFold();
    }

    /// <summary>Most used folded away or not, as remembered; the arrow and its name follow.</summary>
    private void ApplyMostUsedFold()
    {
        if (_mostUsedRows is null || _mostUsedChevron is null || _mostUsedHeader is null)
        {
            return;
        }

        bool folded = _host.Settings.Current.StartMostUsedCollapsed;
        _mostUsedRows.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        _mostUsedChevron.Glyph = folded ? FoldedGlyph : ExpandedGlyph;
        ToolTipService.SetToolTip(_mostUsedHeader, folded ? Strings.StartExpand : Strings.StartCollapse);
        AutomationProperties.SetHelpText(_mostUsedHeader, folded ? Strings.StartExpand : Strings.StartCollapse);
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
        main.Click += (_, _) => LaunchApp(app, app.LaunchTarget);

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

        var row = new Grid { Tag = app };
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

        // Dragged into Pinned: pinned there, and still listed here.
        AttachDrag(main, row, app.ToPin(), app, fromPins: false);

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

        HideLetterGrid();
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

    /// <summary>
    /// The pinned apps again, category by category in the user's order; each pin under the
    /// category it sits in (see PinStore). A category with no apps keeps its name and a note.
    /// </summary>
    private void RebuildPins()
    {
        _pinTiles.Clear();
        _pinSections.Clear();
        PinnedGroups.Children.Clear();

        var byPinPath = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry app in _host.Catalog.Apps)
        {
            byPinPath.TryAdd(app.ToPin().Path, app);
        }

        IReadOnlyList<PinGroup> groups = _host.StartPins.Groups;
        foreach (PinGroup group in groups)
        {
            PinSection section = CreatePinSection(group, groups.Count);
            _pinSections.Add(section);
            PinnedGroups.Children.Add(section.Panel);
        }

        foreach (PinnedApp pin in _host.StartPins.Pins)
        {
            byPinPath.TryGetValue(pin.Path, out AppEntry? app);
            PinSection? section = _pinSections.Find(s => string.Equals(s.Group.Id, pin.Group, StringComparison.Ordinal))
                ?? (_pinSections.Count > 0 ? _pinSections[0] : null);
            section?.Tiles.Add(_pinTiles.Count);
            _pinTiles.Add((pin, CreatePinTile(pin, app)));
        }

        ApplyFilter();
    }

    /// <summary>The category's name as shown: the user's, else "Pinned" (the first one before it is renamed).</summary>
    private static string NameOf(PinGroup group) => group.Name ?? Strings.StartPinned;

    /// <summary>
    /// One category: its name in capitals with a "..." button (on hover, always on touch
    /// devices; a right-click on the name does the same; a double-click renames), then its
    /// tiles four a row, or a note while it has none.
    /// </summary>
    private PinSection CreatePinSection(PinGroup group, int groupCount)
    {
        string name = NameOf(group);
        var title = new TextBlock
        {
            Text = Upper(name),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 60,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var more = new Button
        {
            Content = new FontIcon { Glyph = MoreGlyph, FontFamily = IconFont, FontSize = 12 },
            Width = 28,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = _hasTouch ? 1 : 0,
        };
        AutomationProperties.SetName(more, Fmt.F(Strings.StartCategoryOptions, name));
        ToolTipService.SetToolTip(more, Strings.MoreOptions);

        var header = new Grid { Height = 28, Background = new SolidColorBrush(Colors.Transparent) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(more, 1);
        header.Children.Add(title);
        header.Children.Add(more);

        var grid = new VariableSizedWrapGrid
        {
            ItemWidth = TileWidthDip + 4,
            ItemHeight = TileHeightDip + 4,
            MaximumRowsOrColumns = 4,
            Orientation = Orientation.Horizontal,
        };

        var empty = new TextBlock
        {
            Text = Strings.StartEmptyCategory,
            Margin = new Thickness(4, 2, 4, 6),
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(header);
        panel.Children.Add(grid);
        panel.Children.Add(empty);

        var section = new PinSection(group, panel, header, title, grid, empty);
        more.Click += (_, _) => ShowCategoryMenu(section, groupCount, more, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        header.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowCategoryMenu(section, groupCount, header, e.TryGetPosition(header, out Windows.Foundation.Point at)
                ? new FlyoutShowOptions { Position = at }
                : new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        };
        title.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            BeginRename(section);
        };

        // "..." button: hover only (and while it has keyboard focus); always on touch devices.
        header.PointerEntered += (_, _) => more.Opacity = 1;
        header.PointerExited += (_, _) => more.Opacity = _hasTouch || more.FocusState != FocusState.Unfocused ? 1 : 0;
        more.GotFocus += (_, _) => more.Opacity = 1;
        more.LostFocus += (_, _) => more.Opacity = _hasTouch ? 1 : 0;
        return section;
    }

    /// <summary>A category's menu: rename, a new category below it, move it up or down, remove it (its apps move, nothing is unpinned).</summary>
    private void ShowCategoryMenu(PinSection section, int groupCount, FrameworkElement target, FlyoutShowOptions options)
    {
        try
        {
            string id = section.Group.Id;
            int index = _pinSections.IndexOf(section);
            var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
            menu.Items.Add(MenuItem(Strings.StartRenameCategory, RenameGlyph, () => BeginRename(section)));
            menu.Items.Add(MenuItem(Strings.StartNewCategory, AddGlyph, () => AddCategory(id, null)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem(Strings.StartMoveUp, UpGlyph, () => _host.StartPins.MoveGroup(id, -1), enabled: index > 0));
            menu.Items.Add(MenuItem(Strings.StartMoveDown, DownGlyph, () => _host.StartPins.MoveGroup(id, 1), enabled: index >= 0 && index < groupCount - 1));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem(Strings.StartRemoveCategory, RemoveGlyph, () =>
            {
                if (_host.StartPins.RemoveGroup(id))
                {
                    Log.Info("Start: a category was removed; its apps moved to the one next to it.");
                }
            }, enabled: groupCount > 1));
            ShowPopup(menu, target, options);
        }
        catch (Exception ex)
        {
            Log.Error("Start: a category's menu could not be opened.", ex);
        }
    }

    /// <summary>A new category below <paramref name="afterId"/>, named "New category" and ready to rename; the pin moves into it when given.</summary>
    private void AddCategory(string afterId, PinnedApp? pin)
    {
        string id = _host.StartPins.AddGroup(Strings.StartNewCategory, afterId);
        if (pin is not null)
        {
            _host.StartPins.MoveToGroup(pin.Path, id);
        }

        // The list was built again on each change; the new category's name becomes a text box.
        PinSection? section = _pinSections.Find(s => string.Equals(s.Group.Id, id, StringComparison.Ordinal));
        if (section is not null)
        {
            BeginRename(section);
        }
    }

    /// <summary>
    /// The category's name becomes a text box (all selected): Enter or clicking away keeps the
    /// new name, Escape keeps the old one; an empty name gives back the default ("Pinned").
    /// </summary>
    private void BeginRename(PinSection section)
    {
        if (_query.Length > 0 || _filesMode)
        {
            return;
        }

        string before = NameOf(section.Group);
        var box = new TextBox
        {
            Text = before,
            MaxLength = CategoryNameMax,
            FontSize = 12,
            MinHeight = 0,
            Padding = new Thickness(6, 3, 6, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, Strings.StartCategoryName);
        Grid.SetColumnSpan(box, 2);
        section.Title.Visibility = Visibility.Collapsed;
        section.Header.Children.Add(box);

        bool done = false;
        void Finish(bool keep)
        {
            if (done)
            {
                return;
            }

            done = true;
            string typed = box.Text.Trim();
            section.Header.Children.Remove(box);
            section.Title.Visibility = Visibility.Visible;
            if (keep && typed != before)
            {
                _host.StartPins.RenameGroup(section.Group.Id, typed);
            }
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Finish(e.Key == VirtualKey.Enter);
            }
        };

        // Clicking away keeps the name, once the box has had the keyboard (a menu that just
        // closed hands the focus back first, so the box takes it a moment later).
        bool focused = false;
        box.GotFocus += (_, _) => focused = true;
        box.LostFocus += (_, _) =>
        {
            if (focused)
            {
                Finish(true);
            }
        };

        _renameTimer?.Stop();
        DispatcherQueueTimer later = DispatcherQueue.CreateTimer();
        _renameTimer = later;
        later.Interval = TimeSpan.FromMilliseconds(150);
        later.IsRepeating = false;
        later.Tick += (_, _) =>
        {
            if (!done)
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }
        };
        later.Start();
    }

    /// <summary>"Move to": the other categories, and a new one for this app.</summary>
    private MenuFlyoutSubItem MoveToMenu(PinnedApp pin)
    {
        var move = new MenuFlyoutSubItem { Text = Strings.StartMoveTo, Icon = MenuGlyph(MoveGlyph) };
        foreach (PinGroup group in _host.StartPins.Groups)
        {
            string id = group.Id;
            var item = new MenuFlyoutItem
            {
                Text = NameOf(group),
                IsEnabled = !string.Equals(id, pin.Group, StringComparison.Ordinal),
            };
            item.Click += (_, _) => _host.StartPins.MoveToGroup(pin.Path, id);
            move.Items.Add(item);
        }

        move.Items.Add(new MenuFlyoutSeparator());
        move.Items.Add(MenuItem(Strings.StartNewCategory, AddGlyph, () => AddCategory(pin.Group ?? string.Empty, pin)));
        return move;
    }

    private Grid CreatePinTile(PinnedApp pin, AppEntry? app)
    {
        var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(CreateIcon(app?.Key ?? pin.Path, app, pin.AppId is null ? pin.Path : null, TileIconDip));
        content.Children.Add(new TextBlock
        {
            Text = pin.Name,
            FontSize = 12,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var main = new Button
        {
            Content = content,
            Width = TileWidthDip,
            Height = TileHeightDip,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(2, 8, 2, 2),
            CornerRadius = new CornerRadius(MediumRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(main, pin.Name);
        main.Click += (_, _) =>
        {
            if (app is not null)
            {
                LaunchApp(app, pin.LaunchTarget);
            }
            else
            {
                LaunchAndClose(pin.LaunchTarget);
            }
        };

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

        // Right-click: the same menu as in All apps (for apps still in the app list), then
        // "Move to"; for an app no longer listed, "Move to" and Unpin.
        main.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            try
            {
                MenuFlyout menu;
                if (app is not null)
                {
                    menu = _actions.AppMenu(app, () => AskUninstallFromPin(app), ListIconPixels / 2);
                    menu.Items.Insert(0, new MenuFlyoutSeparator());
                    menu.Items.Insert(0, MoveToMenu(pin));
                }
                else
                {
                    menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
                    menu.Items.Add(MoveToMenu(pin));
                    menu.Items.Add(MenuItem(Strings.UnpinFromStart, UnpinMenuGlyph, () => _host.StartPins.Unpin(pin.Path)));
                }

                ShowPopup(menu, main, e.TryGetPosition(main, out Windows.Foundation.Point at)
                    ? new FlyoutShowOptions { Position = at }
                    : new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
            }
            catch (Exception ex)
            {
                Log.Error("Start: the menu for " + pin.Name + " could not be opened.", ex);
            }
        };

        var tile = new Grid { Width = TileWidthDip, Height = TileHeightDip };
        tile.Children.Add(main);
        tile.Children.Add(unpin);
        AttachDrag(main, tile, pin, app, fromPins: true);

        // Unpin button: hover only (and while it has keyboard focus); always on touch devices.
        tile.PointerEntered += (_, _) => unpin.Opacity = 1;
        tile.PointerExited += (_, _) => unpin.Opacity = _hasTouch || unpin.FocusState != FocusState.Unfocused ? 1 : 0;
        unpin.GotFocus += (_, _) => unpin.Opacity = 1;
        unpin.LostFocus += (_, _) => unpin.Opacity = _hasTouch ? 1 : 0;

        return tile;
    }

    // ---------------------------------------------------------------- dragging into Pinned

    /// <summary>A press on a pinned tile or an All apps row that becomes a drag once the pointer moves far enough (DIP, in Root).</summary>
    private sealed record StartPress(PinnedApp Pin, AppEntry? App, Button Source, FrameworkElement Piece, bool FromPins, uint PointerId, Windows.Foundation.Point At);

    /// <summary>A running drag: what is dragged, its copy under the pointer, and the line where it would go.</summary>
    private sealed class StartDrag
    {
        internal StartDrag(StartPress press, Border ghost, Rectangle marker)
        {
            Press = press;
            Ghost = ghost;
            Marker = marker;
        }

        internal StartPress Press { get; }

        internal Border Ghost { get; }

        internal Rectangle Marker { get; }
    }

    /// <summary>
    /// Lets a tile or row be dragged with the mouse (touch and pen scroll the lists and tap as
    /// before; there, "Move to" and "Pin to Start" do the same). Buttons handle presses
    /// themselves, so their pointer events are heard even when handled.
    /// </summary>
    private void AttachDrag(Button source, FrameworkElement piece, PinnedApp pin, AppEntry? app, bool fromPins)
    {
        source.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => OnDragPressed(source, piece, pin, app, fromPins, e)), true);
        source.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => OnDragPointerMoved(source, e)), true);
        source.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => OnDragPressEnded(source, e)), true);
        source.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) => OnDragPressEnded(source, e)), true);
    }

    private void OnDragPressed(Button source, FrameworkElement piece, PinnedApp pin, AppEntry? app, bool fromPins, PointerRoutedEventArgs e)
    {
        if (_drag is not null || !_open || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            return;
        }

        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(Root);
        if (point.Properties.IsLeftButtonPressed)
        {
            _press = new StartPress(pin, app, source, piece, fromPins, e.Pointer.PointerId, point.Position);
        }
    }

    private void OnDragPointerMoved(Button source, PointerRoutedEventArgs e)
    {
        if (_press is not { } press || _drag is not null || !ReferenceEquals(press.Source, source) || press.PointerId != e.Pointer.PointerId)
        {
            return;
        }

        Windows.Foundation.Point now = e.GetCurrentPoint(Root).Position;
        if (Math.Abs(now.X - press.At.X) < DragStartDip && Math.Abs(now.Y - press.At.Y) < DragStartDip)
        {
            return;
        }

        _press = null;
        BeginDrag(press, e.Pointer);
    }

    private void OnDragPressEnded(Button source, PointerRoutedEventArgs e)
    {
        // Let go before moving far: an ordinary click.
        if (_press is { } press && ReferenceEquals(press.Source, source) && press.PointerId == e.Pointer.PointerId)
        {
            _press = null;
        }
    }

    /// <summary>
    /// The drag starts: the button lets go of the pointer (so it does not click when the mouse
    /// is let go), and from here the drag follows the mouse itself, read on a timer as the
    /// bar's drags are (Windows does not always let Start hold the pointer). Search is cleared
    /// so the categories show, the moved tile fades in its place, and a copy of it follows the
    /// pointer. The boxes' cut is lifted meanwhile, so the copy shows between them.
    /// </summary>
    private void BeginDrag(StartPress press, Pointer pointer)
    {
        try
        {
            press.Source.ReleasePointerCapture(pointer);
            _ = Root.CapturePointer(pointer);

            if (SearchInput.Text.Length > 0)
            {
                SearchInput.Text = string.Empty;
                ParseQuery();
                ApplyFilter();
            }

            // With nothing pinned yet, the first category and its note show, to drop into.
            PinnedEmpty.Visibility = Visibility.Collapsed;
            PinnedGroups.Visibility = Visibility.Visible;

            var marker = new Rectangle
            {
                Width = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(_host.Accent.Current),
                Visibility = Visibility.Collapsed,
            };
            Border ghost = MakeGhost(press);
            DragCanvas.Children.Add(marker);
            DragCanvas.Children.Add(ghost);
            _drag = new StartDrag(press, ghost, marker);
            if (press.FromPins)
            {
                press.Piece.Opacity = DimmedOpacity;
            }

            _cutter.Suspend();
            if (_dragTimer is null)
            {
                _dragTimer = DispatcherQueue.CreateTimer();
                _dragTimer.Interval = TimeSpan.FromMilliseconds(16);
                _dragTimer.IsRepeating = true;
                _dragTimer.Tick += (_, _) => OnDragTick();
            }

            _dragTimer.Start();
            UpdateDrag();
            Log.Info("Start drag: " + press.Pin.Name + (press.FromPins ? " picked up." : " picked up from All apps."));
        }
        catch (Exception ex)
        {
            Log.Error("Start drag: the drag could not start.", ex);
            EndDrag(apply: false);
        }
    }

    /// <summary>The copy under the pointer: the app's icon and name, as its tile shows them.</summary>
    private Border MakeGhost(StartPress press)
    {
        var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 8, 2, 2) };
        content.Children.Add(CreateIcon(
            press.App?.Key ?? press.Pin.Path,
            press.App,
            press.App is null && press.Pin.AppId is null ? press.Pin.Path : null,
            TileIconDip));
        content.Children.Add(new TextBlock
        {
            Text = press.Pin.Name,
            FontSize = 12,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        return new Border
        {
            Child = content,
            Width = TileWidthDip,
            Height = TileHeightDip,
            CornerRadius = new CornerRadius(MediumRadius),
            Background = new SolidColorBrush(_dark ? ColorHelper.FromArgb(0xF0, 0x2C, 0x2C, 0x2C) : ColorHelper.FromArgb(0xF0, 0xFA, 0xFA, 0xFA)),
            BorderBrush = new SolidColorBrush(_dark ? ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x20, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
    }

    /// <summary>Each tick: Escape puts it back; the mouse button let go drops it; otherwise the copy and the line follow the pointer.</summary>
    private void OnDragTick()
    {
        if (_drag is null)
        {
            _dragTimer?.Stop();
            return;
        }

        try
        {
            if ((User32.GetAsyncKeyState(User32.VK_ESCAPE) & 0x8000) != 0)
            {
                EndDrag(apply: false);
            }
            else if (!IsMouseButtonDown())
            {
                EndDrag(apply: true);
            }
            else
            {
                UpdateDrag();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Start drag: the drag failed; put back.", ex);
            EndDrag(apply: false);
        }
    }

    /// <summary>The primary mouse button, read from the hardware (with swapped buttons, the right one).</summary>
    private static bool IsMouseButtonDown()
    {
        int key = User32.GetSystemMetrics(User32.SM_SWAPBUTTON) != 0 ? User32.VK_RBUTTON : User32.VK_LBUTTON;
        return (User32.GetAsyncKeyState(key) & 0x8000) != 0;
    }

    private void OnRootDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not null)
        {
            UpdateDrag();
        }
    }

    private void OnRootDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not null)
        {
            EndDrag(apply: true);
        }
    }

    /// <summary>The mouse pointer in Start's window, in DIP from its top-left (as XAML lays out), or null.</summary>
    private Windows.Foundation.Point? CursorInWindow()
    {
        if (!User32.GetCursorPos(out POINT cursor))
        {
            return null;
        }

        double scale = Root.XamlRoot?.RasterizationScale ?? Scale();
        PointInt32 window = AppWindow.Position;
        return new Windows.Foundation.Point((cursor.X - window.X) / scale, (cursor.Y - window.Y) / scale);
    }

    /// <summary>A point in the window (DIP) as seen from <paramref name="element"/>'s top-left.</summary>
    private static Windows.Foundation.Point From(UIElement element, Windows.Foundation.Point inWindow)
    {
        Windows.Foundation.Point origin = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new Windows.Foundation.Point(inWindow.X - origin.X, inWindow.Y - origin.Y);
    }

    private void UpdateDrag()
    {
        if (_drag is not { } drag || CursorInWindow() is not Windows.Foundation.Point cursor)
        {
            return;
        }

        Windows.Foundation.Point at = From(DragCanvas, cursor);
        Canvas.SetLeft(drag.Ghost, at.X - (TileWidthDip / 2));
        Canvas.SetTop(drag.Ghost, at.Y - (TileHeightDip / 2));
        ScrollNearEdge(cursor);

        (PinSection? section, int slot, _) = FindDrop(drag, cursor);
        drag.Ghost.Opacity = section is null ? 0.6 : 0.95;
        ShowMarker(drag, section, slot);
    }

    /// <summary>While the pointer is near the pinned box's top or bottom, the box scrolls.</summary>
    private void ScrollNearEdge(Windows.Foundation.Point cursor)
    {
        Windows.Foundation.Point at = From(PinnedScroller, cursor);
        double height = PinnedScroller.ActualHeight;
        if (at.X < 0 || at.X > PinnedScroller.ActualWidth || PinnedScroller.ScrollableHeight <= 0)
        {
            return;
        }

        double step = at.Y > -DragScrollEdgeDip && at.Y < DragScrollEdgeDip ? -DragScrollStepDip
            : at.Y > height - DragScrollEdgeDip && at.Y < height + DragScrollEdgeDip ? DragScrollStepDip
            : 0;
        if (step != 0)
        {
            PinnedScroller.ChangeView(null, Math.Clamp(PinnedScroller.VerticalOffset + step, 0, PinnedScroller.ScrollableHeight), null, true);
        }
    }

    /// <summary>
    /// Where a drop at the pointer would go: the category under it (above the first: the
    /// first; below the last: the last), the slot among its tiles as laid out now (four a row;
    /// below the last row: the end), and the place among its other pins (an app already in it
    /// leaves its own place). Nothing outside the pinned box.
    /// </summary>
    private (PinSection? Section, int Slot, int Index) FindDrop(StartDrag drag, Windows.Foundation.Point cursor)
    {
        if (_pinSections.Count == 0 || PinnedContent.Visibility != Visibility.Visible || PinnedGroups.Visibility != Visibility.Visible)
        {
            return (null, 0, 0);
        }

        Windows.Foundation.Point inBox = From(PinnedBox, cursor);
        if (inBox.X < 0 || inBox.Y < 0 || inBox.X > PinnedBox.ActualWidth || inBox.Y > PinnedBox.ActualHeight)
        {
            return (null, 0, 0);
        }

        PinSection section = _pinSections[^1];
        foreach (PinSection candidate in _pinSections)
        {
            if (From(candidate.Panel, cursor).Y < candidate.Panel.ActualHeight + 5)
            {
                section = candidate;
                break;
            }
        }

        const double ItemWidth = TileWidthDip + 4;
        const double ItemHeight = TileHeightDip + 4;
        int count = section.Tiles.Count;
        int rows = (count + 3) / 4;
        Windows.Foundation.Point inGrid = From(section.TileGrid, cursor);
        int slot;
        if (count == 0 || inGrid.Y >= rows * ItemHeight)
        {
            slot = count;
        }
        else
        {
            int row = Math.Clamp((int)Math.Floor(inGrid.Y / ItemHeight), 0, rows - 1);
            int column = Math.Clamp((int)Math.Round(inGrid.X / ItemWidth), 0, 4);
            slot = Math.Min((row * 4) + column, count);
        }

        int own = section.Tiles.FindIndex(i => string.Equals(_pinTiles[i].Pin.Path, drag.Press.Pin.Path, StringComparison.OrdinalIgnoreCase));
        int index = own >= 0 && slot > own ? slot - 1 : slot;
        return (section, slot, index);
    }

    /// <summary>The accent line at the slot (after the last tile of a full row, at the end; on the note of an empty category).</summary>
    private void ShowMarker(StartDrag drag, PinSection? section, int slot)
    {
        if (section is null)
        {
            drag.Marker.Visibility = Visibility.Collapsed;
            return;
        }

        const double ItemWidth = TileWidthDip + 4;
        const double ItemHeight = TileHeightDip + 4;
        int count = section.Tiles.Count;
        int row;
        int column;
        if (count == 0)
        {
            row = 0;
            column = 0;
        }
        else if (slot == count && slot % 4 == 0)
        {
            row = (slot / 4) - 1;
            column = 4;
        }
        else
        {
            row = slot / 4;
            column = slot % 4;
        }

        FrameworkElement origin = count == 0 ? section.Empty : section.TileGrid;
        Windows.Foundation.Point corner = origin.TransformToVisual(DragCanvas)
            .TransformPoint(new Windows.Foundation.Point(column * ItemWidth, row * ItemHeight));
        drag.Marker.Height = count == 0 ? Math.Max(section.Empty.ActualHeight, 20) : ItemHeight - 16;
        Canvas.SetLeft(drag.Marker, corner.X - 1.5);
        Canvas.SetTop(drag.Marker, corner.Y + (count == 0 ? 0 : 8));
        drag.Marker.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The drag ends: the copy and line go, the tile comes back, the cut returns. Let go over
    /// the pinned box (<paramref name="apply"/>), the app goes to that place (a pinned app
    /// moves; an app from All apps is pinned there and stays in All apps); otherwise, or with
    /// Escape, nothing changes.
    /// </summary>
    private void EndDrag(bool apply)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        PinSection? section = null;
        int index = 0;
        if (apply && CursorInWindow() is Windows.Foundation.Point cursor)
        {
            (section, _, index) = FindDrop(drag, cursor);
        }

        _drag = null;
        _dragTimer?.Stop();
        try
        {
            DragCanvas.Children.Clear();
            drag.Press.Piece.Opacity = 1;
            Root.ReleasePointerCaptures();
            if (_open)
            {
                _cutter.Resume();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Start drag: Start could not be put back as it was.", ex);
        }

        if (section is not null && _host.StartPins.PlaceInGroup(drag.Press.Pin, section.Group.Id, index))
        {
            Log.Info("Start drag: " + drag.Press.Pin.Name + (drag.Press.FromPins ? " moved." : " pinned."));
            return;
        }

        Log.Info("Start drag: put back.");
        ApplyFilter();
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

    /// <summary>An app from the app list (or a pin of one): counted for Most used (only while it is on), then started.</summary>
    private void LaunchApp(AppEntry app, string target)
    {
        _host.Usage.Note(app.Key);
        LaunchAndClose(target);
    }
}
