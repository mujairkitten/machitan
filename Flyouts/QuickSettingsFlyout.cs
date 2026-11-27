using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Machitan.Flyouts;

/// <summary>
/// Win+A, in Windows 11's own quick settings look (Sam, 2026-11-26): tiles four to a row, the
/// icon and name inside each, filled with the accent while on; Wi-Fi is a wide tile with the
/// network's name and whether it is connected. The tiles are Windows' switches as Windows
/// reports them (Wi-Fi, Bluetooth, live captions, mobile hotspot, airplane mode; see
/// Shell/QuickToggles.cs) and Cast and Project, which open Windows' own Win+K and Win+P flyouts.
/// Then volume (Windows' default speakers, through Core Audio) and screen brightness (the
/// laptop's own screen through WMI, external monitors over DDC/CI). At the bottom: the battery
/// (when the PC has one; opens Windows' battery settings), a pencil for the edit mode, and the
/// cogwheel for Windows' Settings. In the edit mode a tile can be taken off (its pin), brought
/// back (Add) and moved by dragging it onto another, and the volume and brightness sliders can
/// be moved the same way (onto each other); the orders and the tiles taken off are kept in
/// settings.json. Next to the status ring while it is shown (below it, in a top corner),
/// else lined up with the bar like Start. Each control is there only when Windows can really do
/// it: no speakers, no volume; no screen whose brightness can be set, no brightness; no Bluetooth
/// radio, no Bluetooth tile; no battery, no battery. A switch shows what Windows says, never what
/// was asked: after a click it waits (greyed) until Windows reports the new state, or gives up
/// after a few seconds. The chevron beside the Wi-Fi switch opens the list of Wi-Fi networks in
/// place of the tiles and sliders (QuickSettingsFlyout.Wifi.cs).
/// </summary>
public sealed partial class QuickSettingsFlyout : FlyoutWindow
{
    private const double WidthDip = 392;
    private const double InnerPadDip = 16;
    private const double RowHeightDip = 68;
    private const double NoteHeightDip = 40;
    private const double SpacingDip = 12;

    private const int TileColumns = 4;
    private const double TileHeightDip = 72;
    private const double TileGapDip = 9;
    private const double BadgeDip = 22;
    private const double FooterHeightDip = 48;
    private const double FooterButtonDip = 36;

    /// <summary>After a switch is clicked, how long it waits for Windows to report the new state.</summary>
    private const long SwitchWaitMs = 6000;

    private const string WifiId = "WiFi";
    private const string CastId = "Cast";
    private const string ProjectId = "Project";
    private const string VolumeId = "Volume";
    private const string BrightnessId = "Brightness";

    private const int WifiGlyph = 0xE701;
    private const int BluetoothGlyph = 0xE702;
    private const int AirplaneGlyph = 0xE709;
    private const int HotspotGlyph = 0xE88A;
    private const int CaptionsGlyph = 0xE7F0;
    private const int ProjectGlyph = 0xEBC6;
    private const int CastGlyph = 0xE703;
    private const int EditGlyph = 0xE70F;
    private const int SettingsGlyph = 0xE713;
    private const int UnpinGlyph = 0xE77A;
    private const int AddGlyph = 0xE710;
    private const int BoltGlyph = 0xE945;
    private const int GripperGlyph = 0xE76F;

    private const int VolumeGlyph = 0xE767;
    private const int MutedGlyph = 0xE74F;
    private const int BrightnessGlyph = 0xE706;

    /// <summary>After the user moves a control, Windows' value is not read back for this long.</summary>
    private const long UserTouchQuietMs = 800;

    private static readonly Windows.UI.Color LowRed = ColorHelper.FromArgb(0xFF, 0xFF, 0x3B, 0x30);

    private readonly VolumeService _volume = new();
    private readonly BrightnessService _brightness = new();
    private readonly StackPanel _rows;
    private readonly Border _footer;
    private readonly Grid _footerLeft;
    private readonly StackPanel _footerRight;
    private readonly SolidColorBrush _inkBrush = new(Colors.White);
    private readonly SolidColorBrush _batteryFillBrush = new(Colors.White);
    private readonly DispatcherQueueTimer _poll;
    private readonly DispatcherQueueTimer _brightnessPoll;
    private readonly List<(BrightnessDisplay Display, Slider Slider, TextBlock Value)> _brightnessRows = new();
    private readonly QuickToggles _toggles = new();
    private readonly DispatcherQueueTimer _togglePoll;
    private readonly Dictionary<QuickToggle, (ToggleButton Button, TextBlock Label)> _tiles = new();
    private readonly HashSet<QuickToggle> _busy = new();

    /// <summary>Switches clicked whose new state Windows has not reported yet: the state asked for, and until when to wait.</summary>
    private readonly Dictionary<QuickToggle, (bool On, long Until)> _waiting = new();

    private Grid? _tileGrid;
    private int _tileRows;
    private bool _checkingToggles;
    private TextBlock? _wifiStatus;

    /// <summary>The edit mode: tiles can be taken off, brought back and moved; they do not switch.</summary>
    private bool _editing;

    /// <summary>The tile being dragged in the edit mode.</summary>
    private string? _dragId;

    /// <summary>The slider (volume, or brightness with every screen's) being dragged in the edit mode.</summary>
    private string? _dragSliderId;

    private Button? _batteryButton;
    private Border? _batteryFill;
    private TextBlock? _batteryText;
    private FontIcon? _batteryBolt;
    private string _batteryShown = string.Empty;

    private Slider? _volumeSlider;
    private FontIcon? _volumeIcon;
    private TextBlock? _volumeValue;
    private bool _muted;
    private bool _updating;
    private bool _checkingScreens;
    private bool _screensCheckedOnce;
    private long _touchedAt;
    private long _brightnessTouchedAt;
    private bool _following;

    internal QuickSettingsFlyout(ShellHost host)
        : base(host, "Quick settings")
    {
        _rows = new StackPanel
        {
            Padding = new Thickness(InnerPadDip),
            Spacing = SpacingDip,
        };

        // The bottom band: the battery on the left; the pencil and the cogwheel (in the edit mode: Add and Done) on the right.
        _footerLeft = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _footerRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_footerRight, 1);
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.Children.Add(_footerLeft);
        footerGrid.Children.Add(_footerRight);
        _footer = new Border
        {
            Height = FooterHeightDip,
            Padding = new Thickness(InnerPadDip, 0, 8, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerGrid,
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(_rows);
        Grid.SetRow(_footer, 1);
        content.Children.Add(_footer);
        Root.Children.Add(MakeBox(content, BoxRadius));
        ApplyFooterLook();
        _uiQueue = DispatcherQueue;
        _wifiList.Changed += OnWifiListChanged;

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(300);
        _poll.IsRepeating = true;
        _poll.Tick += (_, _) => ReadVolume();

        // The laptop's brightness keys change the level outside Machitan; follow them.
        _brightnessPoll = DispatcherQueue.CreateTimer();
        _brightnessPoll.Interval = TimeSpan.FromSeconds(1);
        _brightnessPoll.IsRepeating = true;
        _brightnessPoll.Tick += (_, _) => _ = FollowBrightnessAsync();

        // The switches (and the battery) follow changes made elsewhere too (a keyboard key, Windows' own quick settings).
        _togglePoll = DispatcherQueue.CreateTimer();
        _togglePoll.Interval = TimeSpan.FromSeconds(1);
        _togglePoll.IsRepeating = true;
        _togglePoll.Tick += (_, _) =>
        {
            ShowToggles();
            ShowBattery();
            TickWifiPage();
        };
    }

    /// <summary>Win+A: open, or close when already open.</summary>
    internal void Toggle()
    {
        if (IsOpen)
        {
            HideFlyout();
            return;
        }

        _editing = false;
        _volume.Connect();
        _checkingScreens = true;
        Rebuild();
        ReadVolume();
        _poll.Start();
        _brightnessPoll.Start();
        _togglePoll.Start();
        ShowSized();
        _ = CheckScreensAsync();
        _ = CheckTogglesAsync();
    }

    /// <summary>Opens by the status ring while it is shown (the ring's own click still goes to Windows).</summary>
    private protected override Machitan.Interop.RECT? WidgetAnchor => Host.StatusRingRect();

    private protected override void OnHiding()
    {
        _poll.Stop();
        _brightnessPoll.Stop();
        _togglePoll.Stop();
        LeaveWifiPage(show: false);
        _editing = false;
        _dragId = null;
        _dragSliderId = null;
    }

    /// <summary>Sam (2026-11-26): "close Action Center if other elements are clicked": a press on the bar, Dynaland or an indicator closes it too.</summary>
    private protected override bool CloseOnMachitanPress => true;

    private protected override void OnPreviewKey(KeyRoutedEventArgs e)
    {
        // Escape leaves the edit mode (or the Wi-Fi list) first; a second Escape closes the flyout.
        if (e.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        if (_wifiPage)
        {
            e.Handled = true;
            WifiEscape();
        }
        else if (_editing)
        {
            e.Handled = true;
            SetEditing(false);
        }
    }

    private protected override void OnSettingsApplied(ShellSettings settings)
    {
        ApplyFooterLook();
        RestyleWifi();
        _batteryShown = string.Empty;
        ShowBattery();
    }

    private void ShowSized()
    {
        if (_wifiPage)
        {
            Present(WidthDip, (2 * InnerPadDip) + _wifiPageHeight + FooterHeightDip, FlyoutPlacement.BesideBar);
            return;
        }

        int rows = _rows.Children.Count;
        double height = 2 * InnerPadDip;
        foreach (UIElement row in _rows.Children)
        {
            height += ReferenceEquals(row, _tileGrid) ? TilesHeightDip()
                : row is TextBlock ? NoteHeightDip
                : RowHeightDip;
        }

        height += Math.Max(0, rows - 1) * SpacingDip;
        height += FooterHeightDip;
        Present(WidthDip, height, FlyoutPlacement.BesideBar);
    }

    private async Task CheckScreensAsync()
    {
        IReadOnlyList<BrightnessDisplay> displays;
        try
        {
            displays = await _brightness.CheckAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Quick settings: checking the screens failed.", ex);
            displays = Array.Empty<BrightnessDisplay>();
        }

        // Back on the UI thread (the await resumes where it started).
        _checkingScreens = false;
        _screensCheckedOnce = true;
        if (!IsOpen)
        {
            return;
        }

        Rebuild();
        ReadVolume();
        ShowSized();
    }

    private void Rebuild()
    {
        _brightnessRows.Clear();
        _volumeSlider = null;
        _volumeIcon = null;
        _volumeValue = null;

        if (_wifiPage)
        {
            // The Wi-Fi list stays as it is (a key may be half typed); only the band is made again.
            _tiles.Clear();
            _tileGrid = null;
            _wifiStatus = null;
            _wifiChevron = null;
            Grid page = WifiPageRoot();
            if (_rows.Children.Count != 1 || !ReferenceEquals(_rows.Children[0], page))
            {
                _rows.Children.Clear();
                _rows.Children.Add(page);
            }

            FillFooter();
            return;
        }

        _rows.Children.Clear();
        _tileGrid = MakeTiles();
        _rows.Children.Add(_tileGrid);

        FrameworkElement? volumeRow = _volume.IsAvailable ? MakeVolumeRow() : null;
        IReadOnlyList<BrightnessDisplay> displays = _brightness.Displays;
        var brightnessRows = new List<FrameworkElement>();
        foreach (BrightnessDisplay display in displays)
        {
            brightnessRows.Add(MakeBrightnessRow(display, displays.Count > 1));
        }

        // The sliders in their order (every screen's brightness together).
        foreach (string id in ShellSettings.ParseQuickSliderOrder(Host.Settings.Current.QuickSliderOrder))
        {
            if (id == VolumeId && volumeRow is not null)
            {
                _rows.Children.Add(MovableSlider(VolumeId, volumeRow));
            }
            else if (id == BrightnessId)
            {
                foreach (FrameworkElement row in brightnessRows)
                {
                    _rows.Children.Add(MovableSlider(BrightnessId, row));
                }
            }
        }

        // Only the very first check is shown; later ones refresh the rows quietly.
        if (_checkingScreens && !_screensCheckedOnce && displays.Count == 0)
        {
            _rows.Children.Add(MakeNote(Strings.QsChecking));
        }
        else if (!_volume.IsAvailable && displays.Count == 0)
        {
            _rows.Children.Add(MakeNote(Strings.QsNothing));
        }

        FillFooter();
    }

    // ---------------------------------------------------------------- the tiles

    private static QuickToggle? ToggleOf(string id)
    {
        return id switch
        {
            WifiId => QuickToggle.WiFi,
            "Bluetooth" => QuickToggle.Bluetooth,
            "AirplaneMode" => QuickToggle.AirplaneMode,
            "MobileHotspot" => QuickToggle.MobileHotspot,
            "LiveCaptions" => QuickToggle.LiveCaptions,
            _ => null,
        };
    }

    private static (int Glyph, string Label) LookOf(string id)
    {
        return id switch
        {
            WifiId => (WifiGlyph, Strings.QsWifi),
            "Bluetooth" => (BluetoothGlyph, Strings.QsBluetooth),
            "AirplaneMode" => (AirplaneGlyph, Strings.QsAirplaneMode),
            "MobileHotspot" => (HotspotGlyph, Strings.QsHotspot),
            "LiveCaptions" => (CaptionsGlyph, Strings.QsLiveCaptions),
            CastId => (CastGlyph, Strings.QsCast),
            _ => (ProjectGlyph, Strings.QsProject),
        };
    }

    /// <summary>A tile this PC has: Cast and Project always; a switch when Windows offers it here.</summary>
    private bool IsAvailable(string id)
    {
        if (ToggleOf(id) is not QuickToggle toggle)
        {
            return true;
        }

        foreach (QuickToggle available in _toggles.Available)
        {
            if (available == toggle)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The tiles shown, in their order; or, with <paramref name="hidden"/>, the ones taken off that this PC has.</summary>
    private List<string> TileIds(bool hidden)
    {
        ShellSettings settings = Host.Settings.Current;
        List<string> off = ShellSettings.ParseQuickTilesHidden(settings.QuickTilesHidden);
        var ids = new List<string>();
        foreach (string id in ShellSettings.ParseQuickTileOrder(settings.QuickTileOrder))
        {
            if (IsAvailable(id) && off.Contains(id) == hidden)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>Four to a row; Wi-Fi takes two (it moves to the next row when only one is left).</summary>
    private Grid MakeTiles()
    {
        _tiles.Clear();
        _wifiStatus = null;
        _wifiChevron = null;
        var grid = new Grid
        {
            ColumnSpacing = TileGapDip,
            RowSpacing = TileGapDip,
        };

        for (int column = 0; column < TileColumns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int row = 0;
        int at = 0;
        foreach (string id in TileIds(hidden: false))
        {
            int span = id == WifiId ? 2 : 1;
            if (at + span > TileColumns)
            {
                row++;
                at = 0;
            }

            FrameworkElement tile = MakeTile(id);
            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, at);
            Grid.SetColumnSpan(tile, span);
            grid.Children.Add(tile);
            at += span;
            if (at >= TileColumns)
            {
                row++;
                at = 0;
            }
        }

        _tileRows = at == 0 ? row : row + 1;
        for (int i = 0; i < _tileRows; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileHeightDip) });
        }

        return grid;
    }

    private double TilesHeightDip()
    {
        return (_tileRows * TileHeightDip) + (Math.Max(0, _tileRows - 1) * TileGapDip);
    }

    /// <summary>
    /// A tile: a switch (toggle button) or Cast and Project (a button); Wi-Fi with the chevron to
    /// its list beside it, as Windows' own; in the edit mode, with its pin and draggable.
    /// </summary>
    private FrameworkElement MakeTile(string id)
    {
        (int glyph, string label) = LookOf(id);
        QuickToggle? toggle = ToggleOf(id);
        ButtonBase button = toggle is not null ? new ToggleButton() : new Button();
        button.Height = TileHeightDip;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.CornerRadius = new CornerRadius(SmallRadius);
        AutomationProperties.SetName(button, label);

        // What the tile shows: the button, or for Wi-Fi the switch and the chevron side by side.
        FrameworkElement shown = button;
        TextBlock title;
        if (id == WifiId)
        {
            // Wide: the icon on the left, the network's name and whether it is connected beside it.
            title = new TextBlock
            {
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _wifiStatus = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.85,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(title);
            words.Children.Add(_wifiStatus);
            var wide = new Grid { ColumnSpacing = 16 };
            wide.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            wide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FontIcon icon = MakeGlyph(glyph, 18);
            icon.VerticalAlignment = VerticalAlignment.Center;
            wide.Children.Add(icon);
            Grid.SetColumn(words, 1);
            wide.Children.Add(words);
            button.Content = wide;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(22, 0, 8, 0);
            button.CornerRadius = new CornerRadius(SmallRadius, 0, 0, SmallRadius);

            var split = new Grid { ColumnSpacing = 2 };
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ChevronDip) });
            split.Children.Add(button);
            Button chevron = MakeWifiChevron();
            Grid.SetColumn(chevron, 1);
            split.Children.Add(chevron);
            shown = split;
        }
        else
        {
            // The icon above the name (two lines at most), both centred.
            title = new TextBlock
            {
                Text = label,
                FontSize = 12,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var stack = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            FontIcon icon = MakeGlyph(glyph, 16);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(icon);
            stack.Children.Add(title);
            button.Content = stack;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Padding = new Thickness(4);
        }

        if (toggle is QuickToggle quick && button is ToggleButton switchButton)
        {
            switchButton.Click += (_, _) => _ = SwitchAsync(quick, switchButton.IsChecked == true);
            _tiles[quick] = (switchButton, title);
            ShowToggle(quick);
        }
        else
        {
            char key = id == CastId ? 'K' : 'P';
            button.Click += (_, _) =>
            {
                // Windows' own flyout opens in the same corner, so this one goes first.
                HideFlyout();
                if (!KeyChord.SendWin(key))
                {
                    Log.Warn("Quick settings: Windows did not accept the Win+" + key + " key press.");
                }
            };
        }

        if (_editing)
        {
            button.IsTabStop = false;
            return MakeEditTile(id, label, shown);
        }

        return shown;
    }

    /// <summary>
    /// A tile in the edit mode: it does not switch; its pin (top right) takes it off, and it can
    /// be dragged onto another tile to take that one's place.
    /// </summary>
    private Grid MakeEditTile(string id, string label, FrameworkElement content)
    {
        content.IsHitTestVisible = false;

        var unpin = new Button
        {
            Width = BadgeDip,
            Height = BadgeDip,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(BadgeDip / 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -6, 0),
            Content = MakeGlyph(UnpinGlyph, 10),
        };
        string remove = Fmt.F(Strings.QsRemoveTile, label);
        AutomationProperties.SetName(unpin, remove);
        ToolTipService.SetToolTip(unpin, remove);
        unpin.Click += (_, _) => SetTileHidden(id, true);

        var tile = new Grid
        {
            // A background, so the whole tile can be picked up (the button under it takes no pointer now).
            Background = new SolidColorBrush(Colors.Transparent),
            CanDrag = true,
            AllowDrop = true,
        };
        ToolTipService.SetToolTip(tile, Strings.QsDragToMove);
        tile.Children.Add(content);
        tile.Children.Add(unpin);
        tile.DragStarting += (_, e) =>
        {
            _dragId = id;
            e.Data.SetText(id);
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        tile.DragOver += (_, e) =>
        {
            if (_dragId is not null && _dragId != id)
            {
                e.AcceptedOperation = DataPackageOperation.Move;
            }
        };
        tile.Drop += (_, _) =>
        {
            if (_dragId is string moved && moved != id)
            {
                MoveTile(moved, id);
            }

            _dragId = null;
        };
        tile.DropCompleted += (_, _) => _dragId = null;
        return tile;
    }

    /// <summary>
    /// A slider row; in the edit mode it does not slide, shows a gripper on the right, and can be
    /// dragged onto the other slider to take its place (brightness moves with every screen's row).
    /// </summary>
    private FrameworkElement MovableSlider(string id, FrameworkElement row)
    {
        if (!_editing)
        {
            return row;
        }

        row.IsHitTestVisible = false;
        row.Margin = new Thickness(0, 0, 28, 0);
        FontIcon gripper = MakeGlyph(GripperGlyph, 14);
        gripper.HorizontalAlignment = HorizontalAlignment.Right;
        gripper.VerticalAlignment = VerticalAlignment.Center;
        gripper.Opacity = 0.8;

        var holder = new Grid
        {
            Height = RowHeightDip,

            // A background, so the whole row can be picked up (the slider under it takes no pointer now).
            Background = new SolidColorBrush(Colors.Transparent),
            CanDrag = true,
            AllowDrop = true,
        };
        ToolTipService.SetToolTip(holder, Strings.QsDragSliderToMove);
        holder.Children.Add(row);
        holder.Children.Add(gripper);
        holder.DragStarting += (_, e) =>
        {
            _dragSliderId = id;
            e.Data.SetText(id);
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        holder.DragOver += (_, e) =>
        {
            if (_dragSliderId is not null && _dragSliderId != id)
            {
                e.AcceptedOperation = DataPackageOperation.Move;
            }
        };
        holder.Drop += (_, _) =>
        {
            if (_dragSliderId is string moved && moved != id)
            {
                MoveSlider(moved, id);
            }

            _dragSliderId = null;
        };
        holder.DropCompleted += (_, _) => _dragSliderId = null;
        return holder;
    }

    /// <summary>A slider dropped on the other takes its place; kept in settings.json.</summary>
    private void MoveSlider(string moved, string onto)
    {
        Host.Settings.Update(
            settings =>
            {
                List<string> order = ShellSettings.ParseQuickSliderOrder(settings.QuickSliderOrder);
                int place = order.IndexOf(onto);
                if (place < 0 || !order.Remove(moved))
                {
                    return;
                }

                order.Insert(Math.Min(place, order.Count), moved);
                settings.QuickSliderOrder = string.Join(",", order);
            },
            "quick settings");
        Rebuild();
        ReadVolume();
        ShowSized();
    }

    private void SetEditing(bool editing)
    {
        if (editing == _editing)
        {
            return;
        }

        LeaveWifiPage(show: false);
        _editing = editing;
        _dragId = null;
        _dragSliderId = null;
        Log.Info(editing ? "Quick settings: edit mode." : "Quick settings: edit mode done.");
        Rebuild();
        ReadVolume();
        ShowSized();
    }

    /// <summary>Takes a tile off (its pin) or brings it back (Add); kept in settings.json.</summary>
    private void SetTileHidden(string id, bool hide)
    {
        Host.Settings.Update(
            settings =>
            {
                List<string> hidden = ShellSettings.ParseQuickTilesHidden(settings.QuickTilesHidden);
                if (hide && !hidden.Contains(id))
                {
                    hidden.Add(id);
                }
                else if (!hide)
                {
                    hidden.Remove(id);
                }

                settings.QuickTilesHidden = string.Join(",", hidden);
            },
            "quick settings");
        Rebuild();
        ReadVolume();
        ShowSized();
    }

    /// <summary>A tile dropped on another takes its place; the ones between move over by one.</summary>
    private void MoveTile(string moved, string onto)
    {
        Host.Settings.Update(
            settings =>
            {
                List<string> order = ShellSettings.ParseQuickTileOrder(settings.QuickTileOrder);
                int place = order.IndexOf(onto);
                if (place < 0 || !order.Remove(moved))
                {
                    return;
                }

                order.Insert(Math.Min(place, order.Count), moved);
                settings.QuickTileOrder = string.Join(",", order);
            },
            "quick settings");
        Rebuild();
        ReadVolume();
        ShowSized();
    }

    /// <summary>
    /// A switch was clicked (a tile, or the Wi-Fi list's switch) and asks for <paramref name="on"/>.
    /// The click already flipped it; it goes back to what Windows says at once, greyed, and
    /// follows Windows from there (see ShowToggle).
    /// </summary>
    private async Task SwitchAsync(QuickToggle toggle, bool on)
    {
        if (!_busy.Add(toggle))
        {
            ShowToggle(toggle);
            return;
        }

        ShowToggle(toggle);
        bool done = false;
        try
        {
            done = await _toggles.SetAsync(toggle, on);
            Log.Info("Quick settings: " + toggle + (on ? " on" : " off") + (done ? " asked of Windows." : " not done."));
        }
        finally
        {
            _busy.Remove(toggle);
            if (done)
            {
                _waiting[toggle] = (on, Environment.TickCount64 + SwitchWaitMs);
            }

            ShowToggle(toggle);
        }
    }

    /// <summary>Asks Windows which switches this PC has; rebuilds the flyout when that changed.</summary>
    private async Task CheckTogglesAsync()
    {
        if (_checkingToggles)
        {
            return;
        }

        _checkingToggles = true;
        try
        {
            var before = new List<QuickToggle>(_toggles.Available);
            await _toggles.CheckAsync();

            // Back on the UI thread.
            if (!IsOpen)
            {
                return;
            }

            bool same = before.Count == _toggles.Available.Count;
            for (int i = 0; same && i < before.Count; i++)
            {
                same = before[i] == _toggles.Available[i];
            }

            if (same)
            {
                ShowToggles();
            }
            else
            {
                Rebuild();
                ReadVolume();
                ShowSized();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Quick settings: the switches could not be checked.", ex);
        }
        finally
        {
            _checkingToggles = false;
        }
    }

    private void ShowToggles()
    {
        foreach (QuickToggle toggle in _tiles.Keys)
        {
            ShowToggle(toggle);
        }

        ShowWifiSwitch();
    }

    /// <summary>A switch's state as Windows has it now, and whether what was asked of Windows is still being done.</summary>
    private (SwitchState State, bool Waiting) ReadSwitch(QuickToggle toggle)
    {
        SwitchState state = _toggles.StateOf(toggle);
        bool waiting = _busy.Contains(toggle);
        if (_waiting.TryGetValue(toggle, out (bool On, long Until) asked))
        {
            if ((state == SwitchState.On) == asked.On || Environment.TickCount64 > asked.Until)
            {
                _waiting.Remove(toggle);
            }
            else
            {
                waiting = true;
            }
        }

        return (state, waiting);
    }

    /// <summary>A switch as Windows has it now; greyed while Windows cannot do it, or is still doing what was asked.</summary>
    private void ShowToggle(QuickToggle toggle)
    {
        if (toggle == QuickToggle.WiFi)
        {
            ShowWifiSwitch();
        }

        if (!_tiles.TryGetValue(toggle, out (ToggleButton Button, TextBlock Label) tile))
        {
            return;
        }

        (SwitchState state, bool waiting) = ReadSwitch(toggle);
        bool on = state == SwitchState.On;
        if (tile.Button.IsChecked != on)
        {
            tile.Button.IsChecked = on;
        }

        tile.Button.IsEnabled = state != SwitchState.Unavailable && !waiting;
        if (toggle == QuickToggle.WiFi)
        {
            ShowWifiChevron(on);

            // The network's name and "Connected"; else Wi-Fi and what it is doing.
            string? network = on ? QuickToggles.WifiName() : null;
            string title = network ?? Strings.QsWifi;
            string status = state switch
            {
                SwitchState.On => network is null ? Strings.QsNotConnected : Strings.QsConnected,
                SwitchState.Off => Strings.Off,
                _ => Strings.QsUnavailable,
            };
            if (tile.Label.Text != title)
            {
                tile.Label.Text = title;
                ToolTipService.SetToolTip(tile.Button, title);
            }

            if (_wifiStatus is not null && _wifiStatus.Text != status)
            {
                _wifiStatus.Text = status;
            }

            AutomationProperties.SetName(tile.Button, title + ", " + status);
        }
    }

    // ---------------------------------------------------------------- the bottom band

    /// <summary>A band a shade darker than the flyout, with a hairline above it, as Windows' own.</summary>
    private void ApplyFooterLook()
    {
        _footer.Background = new SolidColorBrush(IsDark
            ? ColorHelper.FromArgb(0x40, 0x00, 0x00, 0x00)
            : ColorHelper.FromArgb(0x0D, 0x00, 0x00, 0x00));
        _footer.BorderBrush = new SolidColorBrush(IsDark
            ? ColorHelper.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00));
        _inkBrush.Color = IsDark ? Colors.White : ColorHelper.FromArgb(0xFF, 0x1C, 0x1C, 0x1C);
    }

    /// <summary>The battery on the left, the pencil and the cogwheel on the right; in the edit mode, Add and Done.</summary>
    private void FillFooter()
    {
        _footerLeft.Children.Clear();
        _footerRight.Children.Clear();
        _batteryButton = null;
        _batteryFill = null;
        _batteryText = null;
        _batteryBolt = null;
        _batteryShown = string.Empty;

        if (_editing)
        {
            _footerLeft.Children.Add(MakeAddButton());
            var done = new Button
            {
                Content = Strings.QsDone,
                Height = 32,
                MinWidth = 80,
                CornerRadius = new CornerRadius(SmallRadius),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style) && style is Style accent)
            {
                done.Style = accent;
            }

            AutomationProperties.SetName(done, Strings.QsDone);
            done.Click += (_, _) => SetEditing(false);
            _footerRight.Children.Add(done);
            return;
        }

        _batteryButton = MakeBatteryButton();
        _footerLeft.Children.Add(_batteryButton);
        ShowBattery();

        _footerRight.Children.Add(MakeFooterButton(EditGlyph, Strings.QsEdit, () => SetEditing(true)));
        _footerRight.Children.Add(MakeFooterButton(SettingsGlyph, Strings.QsAllSettings, () =>
        {
            HideFlyout();
            if (Launcher.Start("ms-settings:"))
            {
                Log.Info("Quick settings: Windows' Settings.");
            }
        }));
    }

    private Button MakeFooterButton(int glyph, string name, Action onClick)
    {
        var button = new Button
        {
            Content = MakeGlyph(glyph, 16),
            Width = FooterButtonDip,
            Height = FooterButtonDip,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Add: the tiles taken off that this PC has; none, and it is greyed.</summary>
    private Button MakeAddButton()
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(MakeGlyph(AddGlyph, 12));
        label.Children.Add(new TextBlock { Text = Strings.QsAdd, VerticalAlignment = VerticalAlignment.Center });
        var add = new Button
        {
            Content = label,
            Height = 32,
            CornerRadius = new CornerRadius(SmallRadius),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(add, Strings.QsAdd);

        List<string> hidden = TileIds(hidden: true);
        if (hidden.Count == 0)
        {
            add.IsEnabled = false;
            ToolTipService.SetToolTip(add, Strings.QsNothingToAdd);
            return add;
        }

        var menu = new MenuFlyout();
        foreach (string id in hidden)
        {
            (int glyph, string name) = LookOf(id);
            var item = new MenuFlyoutItem { Text = name, Icon = MakeGlyph(glyph, 16) };
            item.Click += (_, _) => SetTileHidden(id, false);
            menu.Items.Add(item);
        }

        add.Flyout = menu;
        return add;
    }

    /// <summary>The battery: drawn (Windows' outline with the level filled in; red when low, as the status ring), the level, a bolt while charging.</summary>
    private Button MakeBatteryButton()
    {
        _batteryFill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(1),
            Background = _batteryFillBrush,
        };
        var body = new Border
        {
            Width = 22,
            Height = 12,
            BorderThickness = new Thickness(1.2),
            BorderBrush = _inkBrush,
            CornerRadius = new CornerRadius(3),
            Child = _batteryFill,
        };
        var nub = new Border
        {
            Width = 2,
            Height = 5,
            Background = _inkBrush,
            CornerRadius = new CornerRadius(0, 1, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(body);
        icon.Children.Add(nub);

        _batteryBolt = MakeGlyph(BoltGlyph, 12);
        _batteryBolt.VerticalAlignment = VerticalAlignment.Center;
        _batteryText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(icon);
        content.Children.Add(_batteryBolt);
        content.Children.Add(_batteryText);

        var button = new Button
        {
            Content = content,
            Height = FooterButtonDip,
            Padding = new Thickness(8, 0, 10, 0),
            Margin = new Thickness(-8, 0, 0, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        button.Click += (_, _) =>
        {
            HideFlyout();
            if (Launcher.Start("ms-settings:batterysaver"))
            {
                Log.Info("Quick settings: Windows' battery settings.");
            }
        };
        return button;
    }

    /// <summary>The battery as Windows reports it; not shown when the PC has none (or Windows cannot read it).</summary>
    private void ShowBattery()
    {
        if (_batteryButton is null || _batteryFill is null || _batteryText is null || _batteryBolt is null)
        {
            return;
        }

        int percent = -1;
        bool charging = false;
        if (Kernel32.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status)
            && status.BatteryFlag != Kernel32.BATTERY_FLAG_UNKNOWN
            && (status.BatteryFlag & Kernel32.BATTERY_FLAG_NO_BATTERY) == 0
            && status.BatteryLifePercent != Kernel32.BATTERY_PERCENT_UNKNOWN)
        {
            percent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
            charging = (status.BatteryFlag & Kernel32.BATTERY_FLAG_CHARGING) != 0;
        }

        bool low = percent >= 0 && percent < Host.Settings.Current.DuoRingLowBattery;
        string shown = percent.ToString(CultureInfo.InvariantCulture) + (charging ? "c" : string.Empty) + (low ? "l" : string.Empty) + IsDark;
        if (shown == _batteryShown)
        {
            return;
        }

        _batteryShown = shown;
        if (percent < 0)
        {
            _batteryButton.Visibility = Visibility.Collapsed;
            return;
        }

        _batteryButton.Visibility = Visibility.Visible;
        _batteryFill.Width = Math.Max(1, (22 - 2.4 - 4) * percent / 100.0);
        _batteryFillBrush.Color = low && !charging ? LowRed : _inkBrush.Color;
        _batteryBolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        _batteryText.Text = Fmt.F(Strings.QsPercent, percent);
        string name = Fmt.F(charging ? Strings.RingBatteryCharging : Strings.RingBattery, percent);
        AutomationProperties.SetName(_batteryButton, name);
        ToolTipService.SetToolTip(_batteryButton, name);
    }

    // ---------------------------------------------------------------- volume and brightness

    private FrameworkElement MakeVolumeRow()
    {
        string label = _volume.DeviceName.Length > 0 ? Fmt.F(Strings.QsVolumeOf, _volume.DeviceName) : Strings.QsVolume;

        _volumeIcon = MakeGlyph(VolumeGlyph, 16);
        var mute = new Button
        {
            Content = _volumeIcon,
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
        };
        AutomationProperties.SetName(mute, Strings.QsMute);
        mute.Click += (_, _) =>
        {
            _touchedAt = Environment.TickCount64;
            _muted = !_muted;
            _volume.SetMute(_muted);
            ShowMute();
        };

        Slider slider = MakeSlider(Strings.QsVolume);
        TextBlock value = MakeValueText();
        slider.ValueChanged += (_, e) =>
        {
            if (_updating)
            {
                return;
            }

            _touchedAt = Environment.TickCount64;
            _volume.SetLevel((float)(e.NewValue / 100.0));
            ShowValue(value, e.NewValue);
        };

        _volumeSlider = slider;
        _volumeValue = value;
        return MakeRow(label, mute, slider, value);
    }

    private FrameworkElement MakeBrightnessRow(BrightnessDisplay display, bool several)
    {
        string label = !several
            ? Strings.QsBrightness
            : display.Kind == BrightnessKind.BuiltIn ? Strings.QsBrightnessBuiltIn : Fmt.F(Strings.QsBrightnessDisplay, display.Number);
        var icon = new Border
        {
            Width = 40,
            Height = 36,
            Child = MakeGlyph(BrightnessGlyph, 16),
        };

        Slider slider = MakeSlider(label);
        TextBlock value = MakeValueText();
        _updating = true;
        slider.Value = Math.Round(display.CurrentPercent);
        _updating = false;
        ShowValue(value, slider.Value);

        slider.ValueChanged += (_, e) =>
        {
            if (_updating)
            {
                return;
            }

            _brightnessTouchedAt = Environment.TickCount64;
            _brightness.Set(display, e.NewValue);
            ShowValue(value, e.NewValue);
        };

        _brightnessRows.Add((display, slider, value));
        return MakeRow(label, icon, slider, value);
    }

    private static Grid MakeRow(string label, FrameworkElement lead, Slider slider, TextBlock value)
    {
        var title = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        controls.Children.Add(lead);
        Grid.SetColumn(slider, 1);
        controls.Children.Add(slider);
        Grid.SetColumn(value, 2);
        controls.Children.Add(value);

        var row = new Grid { Height = RowHeightDip, RowSpacing = 4 };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(title);
        Grid.SetRow(controls, 1);
        row.Children.Add(controls);
        return row;
    }

    private static Slider MakeSlider(string name)
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            SmallChange = 1,
            LargeChange = 10,
            IsThumbToolTipEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, name);
        return slider;
    }

    private static TextBlock MakeValueText()
    {
        return new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 13,
        };
    }

    private static TextBlock MakeNote(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            Height = NoteHeightDip,
        };
    }

    private static void ShowValue(TextBlock? text, double value)
    {
        if (text is not null)
        {
            text.Text = Math.Round(value).ToString(CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Follows the built-in screen's level when it changes elsewhere (brightness keys, Windows' slider).</summary>
    private async Task FollowBrightnessAsync()
    {
        if (_following || !IsOpen || _brightnessRows.Count == 0)
        {
            return;
        }

        _following = true;
        try
        {
            IReadOnlyList<(BrightnessDisplay Display, double Percent)> levels = await _brightness.ReadBuiltInAsync();

            // Back on the UI thread. A level the user is setting right now wins.
            if (!IsOpen || Environment.TickCount64 - _brightnessTouchedAt < 2 * UserTouchQuietMs)
            {
                return;
            }

            foreach ((BrightnessDisplay display, double percent) in levels)
            {
                foreach ((BrightnessDisplay rowDisplay, Slider slider, TextBlock value) in _brightnessRows)
                {
                    if (!ReferenceEquals(rowDisplay, display) || Math.Abs(slider.Value - Math.Round(percent)) < 1)
                    {
                        continue;
                    }

                    _updating = true;
                    slider.Value = Math.Round(percent);
                    _updating = false;
                    ShowValue(value, slider.Value);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Quick settings: the screen's brightness could not be read back.", ex);
        }
        finally
        {
            _following = false;
        }
    }

    /// <summary>Follows changes made elsewhere (keyboard volume keys, Windows' own slider).</summary>
    private void ReadVolume()
    {
        if (_volumeSlider is null || Environment.TickCount64 - _touchedAt < UserTouchQuietMs)
        {
            return;
        }

        if (!_volume.TryRead(out float level, out bool muted))
        {
            return;
        }

        double percent = Math.Round(level * 100);
        if (Math.Abs(_volumeSlider.Value - percent) >= 1)
        {
            _updating = true;
            _volumeSlider.Value = percent;
            _updating = false;
        }

        ShowValue(_volumeValue, _volumeSlider.Value);
        if (muted != _muted)
        {
            _muted = muted;
        }

        ShowMute();
    }

    private void ShowMute()
    {
        if (_volumeIcon is not null)
        {
            _volumeIcon.Glyph = ((char)(_muted ? MutedGlyph : VolumeGlyph)).ToString();
        }

        if (_volumeSlider is not null)
        {
            _volumeSlider.Opacity = _muted ? 0.5 : 1.0;
        }
    }
}
