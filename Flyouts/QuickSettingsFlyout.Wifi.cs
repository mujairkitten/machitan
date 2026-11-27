using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Flyouts;

/// <summary>
/// Win+A's Wi-Fi list (Sam, 2026-11-26: "2nd option. but with arrow/chevron to access it"), as in
/// Windows 11's own quick settings: the chevron beside the Wi-Fi switch opens it in place of the
/// tiles and sliders. At the top: back, "Wi-Fi", look again, the Wi-Fi switch. Then the networks
/// Windows reports nearby (the connected one first, then the strongest), each with its signal
/// bars, a lock when it needs a key, and "Connected, secured" and the like. A network opens with
/// "Connect automatically" and Connect (Disconnect for the connected one); Windows uses its saved
/// key, else the key is asked for (a user name and password for a work or school network; the name
/// for a hidden network). At the bottom: "More Wi-Fi settings" (Windows' own). Since Windows 11
/// 24H2 apps may only list Wi-Fi networks while location access for desktop apps is on; without
/// it the list says so and offers Windows' location settings. Windows' list is followed while the
/// list is open, and nothing is looked at once it closes (see Shell/WifiList.cs).
/// </summary>
public sealed partial class QuickSettingsFlyout
{
    private const double WifiPageMaxDip = 432;
    private const double WifiPageMinDip = 260;
    private const double WifiHeaderDip = 40;
    private const double WifiRowMinDip = 52;
    private const double ChevronDip = 40;

    private const int ChevronRightGlyph = 0xE76C;
    private const int ChevronLeftGlyph = 0xE76B;
    private const int RefreshGlyph = 0xE72C;
    private const int LockGlyph = 0xE72E;
    private const int Wifi1Glyph = 0xE872;
    private const int Wifi2Glyph = 0xE873;
    private const int Wifi3Glyph = 0xE874;

    /// <summary>While the list is open (1 s ticks): Windows' list is read again every few seconds, and Windows looks again every half minute.</summary>
    private const int WifiReadTicks = 4;
    private const int WifiScanTicks = 30;

    private readonly WifiList _wifiList = new();
    private readonly List<WifiRow> _wifiRows = new();
    private readonly SolidColorBrush _wifiOpenBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _wifiErrorBrush = new(Colors.White);

    /// <summary>The UI thread's queue (Windows' list changes arrive on a worker thread).</summary>
    private readonly DispatcherQueue _uiQueue;

    /// <summary>The Wi-Fi list is shown in place of the tiles and sliders.</summary>
    private bool _wifiPage;

    /// <summary>Counts the list's showings, so that an answer meant for an earlier one is dropped.</summary>
    private int _wifiGeneration;

    private Grid? _wifiPageRoot;
    private double _wifiPageHeight = WifiPageMaxDip;
    private WifiListState _wifiState;
    private bool _wifiOpened;

    /// <summary>Windows has finished looking for networks at least once since the list opened (or Wi-Fi came on).</summary>
    private bool _wifiLooked;
    private bool _wifiCounted;
    private bool _wifiScanning;
    private bool _wifiReading;
    private bool _wifiReadAgain;
    private bool _wifiConnecting;
    private bool _wifiSyncing;
    private int _wifiTicks;
    private SwitchState _wifiRadio;
    private WifiRow? _wifiOpenRow;

    private Button? _wifiBack;
    private ToggleSwitch? _wifiSwitch;
    private ProgressBar? _wifiProgress;
    private StackPanel? _wifiListPanel;
    private StackPanel? _wifiNotePanel;
    private TextBlock? _wifiNoteText;
    private Button? _wifiNoteButton;

    /// <summary>The chevron on the Wi-Fi tile, and whether it shows the accent (null: not set yet).</summary>
    private Button? _wifiChevron;
    private bool? _wifiChevronOn;

    /// <summary>What an open network shows.</summary>
    private enum WifiStep
    {
        Closed,

        /// <summary>"Connect automatically" and Connect, or Disconnect.</summary>
        Choose,

        /// <summary>A hidden network's name.</summary>
        Ssid,

        /// <summary>The key, or a user name and password.</summary>
        Key,

        Connecting,
    }

    // ---------------------------------------------------------------- the chevron on the tile

    /// <summary>The chevron beside the Wi-Fi switch: filled with the accent while Wi-Fi is on, as Windows' own; it opens the list.</summary>
    private Button MakeWifiChevron()
    {
        var chevron = new Button
        {
            Content = MakeGlyph(ChevronRightGlyph, 12),
            Height = TileHeightDip,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(0, SmallRadius, SmallRadius, 0),
            IsTabStop = !_editing,
        };
        AutomationProperties.SetName(chevron, Strings.QsWifiNetworks);
        ToolTipService.SetToolTip(chevron, Strings.QsWifiNetworks);
        chevron.Click += (_, _) => ShowWifiPage();
        _wifiChevron = chevron;
        _wifiChevronOn = null;
        return chevron;
    }

    /// <summary>The chevron takes WinUI's accent button look while Wi-Fi is on, its plain look while it is off.</summary>
    private void ShowWifiChevron(bool on)
    {
        if (_wifiChevron is null || _wifiChevronOn == on)
        {
            return;
        }

        _wifiChevronOn = on;
        string key = on ? "AccentButtonStyle" : "DefaultButtonStyle";
        if (Application.Current.Resources.TryGetValue(key, out object? style) && style is Style found)
        {
            _wifiChevron.Style = found;
        }
    }

    // ---------------------------------------------------------------- opening and leaving the list

    private void ShowWifiPage()
    {
        if (_wifiPage || _editing || !IsOpen)
        {
            return;
        }

        _wifiPage = true;
        _wifiGeneration++;
        _wifiState = WifiListState.Ready;
        _wifiOpened = false;
        _wifiLooked = false;
        _wifiCounted = false;
        _wifiTicks = 0;
        _wifiOpenRow = null;
        _wifiRows.Clear();
        _wifiRadio = _toggles.StateOf(QuickToggle.WiFi);
        _wifiPageHeight = Math.Clamp(ScreenHeightDip(0.75) - (2 * InnerPadDip) - FooterHeightDip, WifiPageMinDip, WifiPageMaxDip);
        Log.Info("Quick settings: the Wi-Fi list.");
        Rebuild();
        ShowSized();
        _uiQueue.TryEnqueue(() => _wifiBack?.Focus(FocusState.Programmatic));
        _ = OpenWifiAsync();
    }

    /// <summary>Back to the tiles and sliders (<paramref name="show"/>), or quietly as the flyout closes or the edit mode starts.</summary>
    private void LeaveWifiPage(bool show)
    {
        if (!_wifiPage)
        {
            return;
        }

        _wifiPage = false;
        _wifiGeneration++;
        _wifiOpened = false;
        _wifiOpenRow = null;
        _wifiRows.Clear();
        _wifiPageRoot = null;
        _wifiBack = null;
        _wifiSwitch = null;
        _wifiProgress = null;
        _wifiListPanel = null;
        _wifiNotePanel = null;
        _wifiNoteText = null;
        _wifiNoteButton = null;
        ClearSavedRows();
        _wifiList.Close();
        if (!show)
        {
            return;
        }

        Rebuild();
        ReadVolume();
        ShowSized();
        _uiQueue.TryEnqueue(() => _wifiChevron?.Focus(FocusState.Programmatic));
    }

    /// <summary>Escape on the list: a key or name being typed is dropped first; then back to the tiles.</summary>
    private void WifiEscape()
    {
        if (_wifiOpenRow is WifiRow open && open.Step is WifiStep.Ssid or WifiStep.Key)
        {
            BackToChoose(open);
            return;
        }

        LeaveWifiPage(show: true);
    }

    private async Task OpenWifiAsync()
    {
        int generation = _wifiGeneration;
        WifiListState state = await _wifiList.OpenAsync();

        // Back on the UI thread. The list may have closed meanwhile (Windows' location question
        // takes the focus, and the flyout closes with it).
        if (!_wifiPage)
        {
            _wifiList.Close();
            return;
        }

        if (generation != _wifiGeneration)
        {
            return;
        }

        _wifiState = state;
        _wifiOpened = state == WifiListState.Ready;
        ShowWifiNote();
        if (state == WifiListState.NoLocation && _wifiRadio != SwitchState.Off)
        {
            // Windows' saved networks instead: they need no location access.
            ShowSavedNetworks();
        }

        if (!_wifiOpened)
        {
            return;
        }

        // Windows' last list at once, then a fresh look.
        await ReadWifiAsync();
        await ScanWifiAsync();
    }

    /// <summary>Asks Windows to look for networks (the thin bar moves meanwhile), then shows what it found.</summary>
    private async Task ScanWifiAsync()
    {
        // Not while connecting: a look then could disturb it.
        if (!_wifiPage || !_wifiOpened || _wifiScanning || _wifiConnecting || _toggles.StateOf(QuickToggle.WiFi) != SwitchState.On)
        {
            return;
        }

        _wifiScanning = true;
        _wifiTicks = 0;
        ShowWifiBusy();
        try
        {
            await _wifiList.ScanAsync();
        }
        finally
        {
            _wifiScanning = false;
            _wifiLooked = true;
        }

        ShowWifiBusy();
        await ReadWifiAsync();
    }

    /// <summary>Reads Windows' list and shows it; a read asked for meanwhile runs once more after it.</summary>
    private async Task ReadWifiAsync()
    {
        if (!_wifiPage || !_wifiOpened)
        {
            return;
        }

        if (_wifiReading)
        {
            _wifiReadAgain = true;
            return;
        }

        _wifiReading = true;
        try
        {
            do
            {
                _wifiReadAgain = false;
                int generation = _wifiGeneration;
                List<WifiEntry> found = await _wifiList.ReadAsync();
                if (_wifiPage && generation == _wifiGeneration)
                {
                    ShowWifiNetworks(found);
                }
            }
            while (_wifiReadAgain && _wifiPage && _wifiOpened);
        }
        finally
        {
            _wifiReading = false;
        }
    }

    /// <summary>On a worker thread: Windows' list changed.</summary>
    private void OnWifiListChanged()
    {
        _uiQueue.TryEnqueue(() => _ = ReadWifiAsync());
    }

    /// <summary>Each second while the flyout is open.</summary>
    private void TickWifiPage()
    {
        if (_wifiPage && _savedMode)
        {
            // Saved networks: which one is connected, every two seconds.
            if (++_wifiTicks % 2 == 0)
            {
                ShowSavedStates();
            }

            return;
        }

        if (!_wifiPage || !_wifiOpened)
        {
            return;
        }

        _wifiTicks++;
        if (_wifiTicks >= WifiScanTicks)
        {
            _ = ScanWifiAsync();
        }
        else if (_wifiTicks % WifiReadTicks == 0)
        {
            _ = ReadWifiAsync();
        }
    }

    // ---------------------------------------------------------------- the page

    private Grid WifiPageRoot()
    {
        return _wifiPageRoot ??= BuildWifiPage();
    }

    private Grid BuildWifiPage()
    {
        RestyleWifi();

        // The top: back, "Wi-Fi", look again, the switch.
        _wifiBack = MakeFooterButton(ChevronLeftGlyph, Strings.QsBack, () => LeaveWifiPage(show: true));
        var title = new TextBlock
        {
            Text = Strings.QsWifi,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Button refresh = MakeFooterButton(RefreshGlyph, Strings.WifiRefresh, () => _ = ScanWifiAsync());
        var wifiSwitch = new ToggleSwitch
        {
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            IsOn = _wifiRadio == SwitchState.On,
            IsEnabled = _wifiRadio != SwitchState.Unavailable,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetName(wifiSwitch, Strings.QsWifi);
        wifiSwitch.Toggled += (_, _) =>
        {
            if (!_wifiSyncing)
            {
                _ = SwitchAsync(QuickToggle.WiFi, wifiSwitch.IsOn);
            }
        };
        _wifiSwitch = wifiSwitch;

        var header = new Grid { Height = WifiHeaderDip };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _wifiBack.Margin = new Thickness(-8, 0, 0, 0);
        header.Children.Add(_wifiBack);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);
        Grid.SetColumn(wifiSwitch, 3);
        header.Children.Add(wifiSwitch);

        // A thin moving bar while Windows looks (its place is kept, so nothing jumps).
        _wifiProgress = new ProgressBar { IsIndeterminate = false, Opacity = 0 };
        AutomationProperties.SetName(_wifiProgress, Strings.WifiLooking);

        // The networks; and in the same place, what to say when there are none.
        _wifiListPanel = new StackPanel { Spacing = 2 };
        var scroller = new ScrollViewer
        {
            Content = _wifiListPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(-8, 0, -8, 0),
        };

        _wifiNoteText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.8,
        };
        _wifiNoteButton = MakeTextButton(Strings.WifiLocationSettings, () =>
        {
            HideFlyout();
            if (Launcher.Start("ms-settings:privacy-location"))
            {
                Log.Info("Quick settings: Windows' location settings.");
            }
        });
        _wifiNoteButton.HorizontalAlignment = HorizontalAlignment.Center;
        _wifiNoteButton.Visibility = Visibility.Collapsed;
        _wifiNotePanel = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(12, 28, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        _wifiNotePanel.Children.Add(_wifiNoteText);
        _wifiNotePanel.Children.Add(_wifiNoteButton);

        // The bottom: Windows' own Wi-Fi settings.
        var more = new Button
        {
            Content = new TextBlock { Text = Strings.WifiMoreSettings, FontSize = 13 },
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(-8, 0, 0, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(more, Strings.WifiMoreSettings);
        more.Click += (_, _) =>
        {
            HideFlyout();
            if (Launcher.Start("ms-settings:network-wifi"))
            {
                Log.Info("Quick settings: Windows' Wi-Fi settings.");
            }
        };

        var page = new Grid { Height = _wifiPageHeight, RowSpacing = 4 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(header);
        Grid.SetRow(_wifiProgress, 1);
        page.Children.Add(_wifiProgress);
        Grid.SetRow(scroller, 2);
        page.Children.Add(scroller);
        Grid.SetRow(_wifiNotePanel, 2);
        page.Children.Add(_wifiNotePanel);
        Grid.SetRow(more, 3);
        page.Children.Add(more);

        ShowWifiBusy();
        return page;
    }

    /// <summary>The open network's fill and the error text, for the dark or light look.</summary>
    private void RestyleWifi()
    {
        _wifiOpenBrush.Color = IsDark ? ColorHelper.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x0A, 0x00, 0x00, 0x00);
        _wifiErrorBrush.Color = IsDark ? ColorHelper.FromArgb(0xFF, 0xFF, 0x99, 0xA4) : ColorHelper.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);
    }

    /// <summary>The switch at the top as Windows has Wi-Fi now; the list empties when Wi-Fi goes off and looks again when it comes on.</summary>
    private void ShowWifiSwitch()
    {
        if (!_wifiPage || _wifiSwitch is null)
        {
            return;
        }

        (SwitchState state, bool waiting) = ReadSwitch(QuickToggle.WiFi);
        bool on = state == SwitchState.On;
        if (_wifiSwitch.IsOn != on)
        {
            _wifiSyncing = true;
            _wifiSwitch.IsOn = on;
            _wifiSyncing = false;
        }

        _wifiSwitch.IsEnabled = state != SwitchState.Unavailable && !waiting;
        if (state == _wifiRadio)
        {
            return;
        }

        SwitchState was = _wifiRadio;
        _wifiRadio = state;
        if (state == SwitchState.Off)
        {
            ClearWifiRows();
        }
        else if (on && was != SwitchState.On)
        {
            _wifiLooked = false;
            if (_wifiState == WifiListState.NoLocation)
            {
                ShowSavedNetworks();
            }
            else
            {
                _ = ScanWifiAsync();
            }
        }

        ShowWifiNote();
    }

    private void ShowWifiBusy()
    {
        if (_wifiProgress is not null)
        {
            _wifiProgress.IsIndeterminate = _wifiScanning;
            _wifiProgress.Opacity = _wifiScanning ? 1 : 0;
        }

        ShowWifiNote();
    }

    /// <summary>What the list says when it has no networks to show, and why.</summary>
    private void ShowWifiNote()
    {
        if (_wifiNotePanel is null || _wifiNoteText is null || _wifiNoteButton is null)
        {
            return;
        }

        string? note = null;
        bool toLocation = false;
        if (_wifiRadio == SwitchState.Off)
        {
            note = Strings.WifiOffNote;
        }
        else if (_wifiState == WifiListState.NoLocation)
        {
            // With saved networks shown, they say it themselves (MakeSavedIntro).
            if (!_savedMode)
            {
                note = Strings.WifiNoLocation;
                toLocation = true;
            }
        }
        else if (_wifiState == WifiListState.NoAdapter)
        {
            note = Strings.WifiNoAdapter;
        }
        else if (_wifiState == WifiListState.Failed)
        {
            note = Strings.WifiFailed;
        }
        else if (_wifiRows.Count == 0 && _wifiRadio == SwitchState.Unavailable)
        {
            note = Strings.WifiUnavailableNote;
        }
        else if (_wifiRows.Count == 0)
        {
            note = _wifiScanning || !_wifiLooked ? Strings.WifiLooking : Strings.WifiNone;
        }

        _wifiNotePanel.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        _wifiNoteText.Text = note ?? string.Empty;
        _wifiNoteButton.Visibility = toLocation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearWifiRows()
    {
        _wifiRows.Clear();
        _wifiListPanel?.Children.Clear();
        _wifiOpenRow = null;
        ClearSavedRows();
    }

    /// <summary>
    /// Shows Windows' list. While a network is open nothing moves (its key may be half typed): the
    /// rows are updated where they are and new networks are added at the end; otherwise the rows
    /// follow Windows' order.
    /// </summary>
    private void ShowWifiNetworks(List<WifiEntry> found)
    {
        if (_wifiListPanel is null)
        {
            return;
        }

        if (_toggles.StateOf(QuickToggle.WiFi) == SwitchState.Off)
        {
            ClearWifiRows();
            ShowWifiNote();
            return;
        }

        if (_wifiLooked && !_wifiCounted)
        {
            _wifiCounted = true;
            Log.Info("Wi-Fi list: " + found.Count.ToString(CultureInfo.InvariantCulture) + " networks.");
        }

        var byId = new Dictionary<string, WifiRow>(StringComparer.Ordinal);
        foreach (WifiRow shown in _wifiRows)
        {
            byId[shown.Id] = shown;
        }

        bool sameOrder = found.Count == _wifiRows.Count;
        for (int i = 0; sameOrder && i < found.Count; i++)
        {
            sameOrder = string.Equals(found[i].Ssid, _wifiRows[i].Id, StringComparison.Ordinal);
        }

        if (sameOrder || _wifiOpenRow is not null)
        {
            foreach (WifiEntry entry in found)
            {
                if (byId.TryGetValue(entry.Ssid, out WifiRow? kept))
                {
                    UpdateWifiRow(kept, entry);
                }
                else
                {
                    WifiRow added = MakeWifiRow(entry);
                    _wifiRows.Add(added);
                    _wifiListPanel.Children.Add(added.Box);
                }
            }

            ShowWifiNote();
            return;
        }

        var rows = new List<WifiRow>(found.Count);
        foreach (WifiEntry entry in found)
        {
            if (byId.TryGetValue(entry.Ssid, out WifiRow? kept))
            {
                UpdateWifiRow(kept, entry);
                rows.Add(kept);
            }
            else
            {
                rows.Add(MakeWifiRow(entry));
            }
        }

        _wifiRows.Clear();
        _wifiListPanel.Children.Clear();
        foreach (WifiRow row in rows)
        {
            _wifiRows.Add(row);
            _wifiListPanel.Children.Add(row.Box);
        }

        ShowWifiNote();
    }

    // ---------------------------------------------------------------- a network

    /// <summary>A network: a button with the signal bars (a lock when it needs a key), the name and the state; below it what it offers while open.</summary>
    private WifiRow MakeWifiRow(WifiEntry entry)
    {
        FontIcon faint = MakeGlyph(WifiGlyph, 18);
        faint.Opacity = 0.3;
        FontIcon bars = MakeGlyph(WifiGlyph, 18);
        FontIcon lockIcon = MakeGlyph(LockGlyph, 9);
        lockIcon.HorizontalAlignment = HorizontalAlignment.Right;
        lockIcon.VerticalAlignment = VerticalAlignment.Bottom;
        var icon = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(faint);
        icon.Children.Add(bars);
        icon.Children.Add(lockIcon);

        var name = new TextBlock { FontSize = 14, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { FontSize = 12, Opacity = 0.75, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(name);
        words.Children.Add(status);
        var line = new Grid { ColumnSpacing = 14 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(icon);
        Grid.SetColumn(words, 1);
        line.Children.Add(words);

        var head = new Button
        {
            Content = line,
            MinHeight = WifiRowMinDip,
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
        };
        var details = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(46, 2, 10, 10),
            Visibility = Visibility.Collapsed,
        };
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(details);
        var box = new Border { Child = body, CornerRadius = new CornerRadius(SmallRadius) };

        var row = new WifiRow(entry, box, head, bars, lockIcon, name, status, details);
        head.Click += (_, _) => ToggleWifiRow(row);
        ShowWifiRow(row);
        return row;
    }

    private void UpdateWifiRow(WifiRow row, WifiEntry entry)
    {
        bool connectedChanged = row.Entry.Connected != entry.Connected;
        row.Entry = entry;
        ShowWifiRow(row);

        // Connect becomes Disconnect (or back) when Windows connected or disconnected meanwhile.
        if (connectedChanged && row.Step == WifiStep.Choose)
        {
            FillWifiDetails(row);
        }
    }

    private static void ShowWifiRow(WifiRow row)
    {
        WifiEntry entry = row.Entry;
        string glyph = ((char)BarsGlyph(entry.Bars)).ToString();
        if (row.Bars.Glyph != glyph)
        {
            row.Bars.Glyph = glyph;
        }

        row.Lock.Visibility = entry.Secured ? Visibility.Visible : Visibility.Collapsed;
        string name = entry.Hidden ? Strings.WifiHidden : entry.Ssid;
        string status = StatusOf(entry);
        if (row.Name.Text != name)
        {
            row.Name.Text = name;
        }

        if (row.Status.Text != status)
        {
            row.Status.Text = status;
        }

        AutomationProperties.SetName(row.Head, name + ", " + status);
    }

    private static int BarsGlyph(int bars)
    {
        return bars switch
        {
            >= 4 => WifiGlyph,
            3 => Wifi3Glyph,
            2 => Wifi2Glyph,
            _ => Wifi1Glyph,
        };
    }

    private static string StatusOf(WifiEntry entry)
    {
        if (entry.Connected)
        {
            return entry.Internet
                ? (entry.Secured ? Strings.WifiConnectedSecured : Strings.WifiConnectedOpen)
                : (entry.Secured ? Strings.WifiNoInternetSecured : Strings.WifiNoInternetOpen);
        }

        return entry.Secured ? Strings.WifiSecured : Strings.WifiOpen;
    }

    /// <summary>A click on a network opens it (closing the one open before) or closes it.</summary>
    private void ToggleWifiRow(WifiRow row)
    {
        if (_wifiConnecting)
        {
            return;
        }

        if (row.Step != WifiStep.Closed)
        {
            CloseWifiRow(row);
            return;
        }

        if (_wifiOpenRow is WifiRow open)
        {
            CloseWifiRow(open);
        }

        row.Step = WifiStep.Choose;
        row.Error = null;
        _wifiOpenRow = row;
        FillWifiDetails(row);
    }

    private void CloseWifiRow(WifiRow row)
    {
        row.Step = WifiStep.Closed;
        row.Error = null;
        if (ReferenceEquals(_wifiOpenRow, row))
        {
            _wifiOpenRow = null;
        }

        FillWifiDetails(row);
    }

    private void BackToChoose(WifiRow row)
    {
        row.Step = WifiStep.Choose;
        row.Error = null;
        FillWifiDetails(row);
    }

    /// <summary>What an open network shows now (rebuilt at each step, so a typed key never outlives its step).</summary>
    private void FillWifiDetails(WifiRow row)
    {
        StackPanel details = row.Details;
        details.Children.Clear();
        bool open = row.Step != WifiStep.Closed;
        details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        row.Box.Background = open ? _wifiOpenBrush : null;
        if (!open)
        {
            return;
        }

        if (row.Error is string error)
        {
            details.Children.Add(new TextBlock
            {
                Text = error,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _wifiErrorBrush,
            });
        }

        switch (row.Step)
        {
            case WifiStep.Choose:
                FillWifiChoose(row);
                break;

            case WifiStep.Ssid:
                FillWifiSsid(row);
                break;

            case WifiStep.Key:
                FillWifiKey(row);
                break;

            default:
                FillWifiConnecting(row);
                break;
        }
    }

    private void FillWifiChoose(WifiRow row)
    {
        if (row.Entry.Connected)
        {
            row.Details.Children.Add(MakeWifiButtons(MakeWifiButton(Strings.WifiDisconnect, accent: false, () => DisconnectWifi(row))));
            return;
        }

        var automatic = new CheckBox { Content = Strings.WifiConnectAuto, IsChecked = row.Auto };
        automatic.Checked += (_, _) => row.Auto = true;
        automatic.Unchecked += (_, _) => row.Auto = false;
        row.Details.Children.Add(automatic);
        row.Details.Children.Add(MakeWifiButtons(MakeWifiButton(Strings.WifiConnect, accent: true, () =>
        {
            if (row.Entry.Hidden)
            {
                row.Step = WifiStep.Ssid;
                row.Error = null;
                FillWifiDetails(row);
            }
            else
            {
                _ = ConnectWifiAsync(row, null, null);
            }
        })));
    }

    private void FillWifiSsid(WifiRow row)
    {
        var ssid = new TextBox { Header = Strings.WifiEnterSsid, Text = row.HiddenSsid, MaxLength = 32 };

        void Next()
        {
            if (ssid.Text.Length > 0)
            {
                row.HiddenSsid = ssid.Text;
                _ = ConnectWifiAsync(row, null, null);
            }
        }

        Button next = MakeWifiButton(Strings.WifiNext, accent: true, Next);
        next.IsEnabled = ssid.Text.Length > 0;
        ssid.TextChanged += (_, _) => next.IsEnabled = ssid.Text.Length > 0;
        ssid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Next();
            }
        };
        row.Details.Children.Add(ssid);
        row.Details.Children.Add(MakeWifiButtons(next, MakeWifiButton(Strings.Cancel, accent: false, () => BackToChoose(row))));
        FocusWhenLoaded(ssid);
    }

    private void FillWifiKey(WifiRow row)
    {
        TextBox? user = null;
        if (row.Entry.Enterprise)
        {
            var userBox = new TextBox { Header = Strings.WifiUserName, Text = row.User };
            userBox.TextChanged += (_, _) => row.User = userBox.Text;
            row.Details.Children.Add(userBox);
            user = userBox;
        }

        var key = new PasswordBox { Header = row.Entry.Enterprise ? Strings.WifiPassword : Strings.WifiEnterKey };

        void Next()
        {
            if (key.Password.Length > 0)
            {
                _ = ConnectWifiAsync(row, row.Entry.Enterprise ? row.User : null, key.Password);
            }
        }

        Button next = MakeWifiButton(Strings.WifiNext, accent: true, Next);
        next.IsEnabled = false;
        key.PasswordChanged += (_, _) => next.IsEnabled = key.Password.Length > 0;
        key.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Next();
            }
        };
        row.Details.Children.Add(key);
        row.Details.Children.Add(MakeWifiButtons(next, MakeWifiButton(Strings.Cancel, accent: false, () => BackToChoose(row))));
        FocusWhenLoaded(user is not null && row.User.Length == 0 ? (Control)user : key);
    }

    private static void FillWifiConnecting(WifiRow row)
    {
        var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16 };
        AutomationProperties.SetName(ring, Strings.WifiConnecting);
        var text = new TextBlock { Text = Strings.WifiConnecting, VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        line.Children.Add(ring);
        line.Children.Add(text);
        row.Details.Children.Add(line);
    }

    private static StackPanel MakeWifiButtons(params Button[] buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (Button button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    private static Button MakeWifiButton(string text, bool accent, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Height = 32,
            MinWidth = 96,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(SmallRadius),
        };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style) && style is Style accentStyle)
        {
            button.Style = accentStyle;
        }

        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void FocusWhenLoaded(Control control)
    {
        control.Loaded += (_, _) => control.Focus(FocusState.Programmatic);
    }

    // ---------------------------------------------------------------- connecting

    /// <summary>
    /// Asks Windows to connect: first with the key Windows has saved (none typed); when that does
    /// not work for a network that needs one, the key is asked for, as Windows' own list does.
    /// </summary>
    private async Task ConnectWifiAsync(WifiRow row, string? user, string? key)
    {
        if (_wifiConnecting || !_wifiOpened)
        {
            return;
        }

        _wifiConnecting = true;
        row.Step = WifiStep.Connecting;
        row.Error = null;
        FillWifiDetails(row);
        WifiConnectResult result;
        try
        {
            result = await _wifiList.ConnectAsync(row.Entry, row.Auto, user, key, row.Entry.Hidden ? row.HiddenSsid : null);
        }
        finally
        {
            _wifiConnecting = false;
        }

        // Back on the UI thread; the list may have closed meanwhile.
        if (!_wifiPage || !_wifiRows.Contains(row))
        {
            return;
        }

        if (result == WifiConnectResult.Connected)
        {
            CloseWifiRow(row);
            ShowToggles();
            _ = ReadWifiAsync();
            return;
        }

        if (key is null && row.Entry.Secured && result is not (WifiConnectResult.Gone or WifiConnectResult.NoAccess))
        {
            // Windows has no key saved for it, or the saved one no longer works.
            row.Step = WifiStep.Key;
            row.Error = null;
        }
        else
        {
            row.Step = key is not null ? WifiStep.Key : row.Entry.Hidden ? WifiStep.Ssid : WifiStep.Choose;
            row.Error = ErrorOf(result, row.Entry.Enterprise);
        }

        FillWifiDetails(row);
    }

    private static string ErrorOf(WifiConnectResult result, bool enterprise)
    {
        return result switch
        {
            WifiConnectResult.WrongKey => enterprise ? Strings.WifiErrSignIn : Strings.WifiErrKey,
            WifiConnectResult.Gone => Strings.WifiErrGone,
            WifiConnectResult.Timeout => Strings.WifiErrTimeout,
            WifiConnectResult.Unsupported => Strings.WifiErrUnsupported,
            WifiConnectResult.NoAccess => Strings.WifiErrNoAccess,
            _ => Strings.WifiErrFailed,
        };
    }

    private void DisconnectWifi(WifiRow row)
    {
        if (!_wifiList.Disconnect())
        {
            row.Error = Strings.WifiErrDisconnect;
            FillWifiDetails(row);
            return;
        }

        CloseWifiRow(row);
        _ = ReadWifiLaterAsync();
    }

    /// <summary>A second after disconnecting (Windows reports it a moment later).</summary>
    private async Task ReadWifiLaterAsync()
    {
        await Task.Delay(1000);
        ShowToggles();
        await ReadWifiAsync();
    }

    /// <summary>A network shown in the list, and what is open in it.</summary>
    private sealed class WifiRow
    {
        internal WifiRow(WifiEntry entry, Border box, Button head, FontIcon bars, FontIcon lockIcon, TextBlock name, TextBlock status, StackPanel details)
        {
            Entry = entry;
            Id = entry.Ssid;
            Box = box;
            Head = head;
            Bars = bars;
            Lock = lockIcon;
            Name = name;
            Status = status;
            Details = details;
        }

        internal WifiEntry Entry { get; set; }

        /// <summary>The network's name (empty for the hidden one); it never changes for a row.</summary>
        internal string Id { get; }

        internal WifiStep Step { get; set; }

        internal bool Auto { get; set; } = true;

        internal string? Error { get; set; }

        internal string User { get; set; } = string.Empty;

        internal string HiddenSsid { get; set; } = string.Empty;

        internal Border Box { get; }

        internal Button Head { get; }

        internal FontIcon Bars { get; }

        internal FontIcon Lock { get; }

        internal TextBlock Name { get; }

        internal TextBlock Status { get; }

        internal StackPanel Details { get; }
    }
}
