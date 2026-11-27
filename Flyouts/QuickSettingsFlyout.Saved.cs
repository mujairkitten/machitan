using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Flyouts;

/// <summary>
/// The Wi-Fi list without location access (Sam, 2026-11-26: "i hope there's a way to bypass
/// location requirement to see wifi networks"; then "do your roundabout"): Windows' saved
/// networks, from Native Wifi, which needs no location access (WifiSaved). Nothing nearby is
/// looked at, so there are no signal bars and nothing says which are in reach: each says
/// "Saved" (or "Connected"), opens with Connect (Disconnect for the connected one), and Windows
/// connects when that network is in reach. Above them: why, and "Open location settings".
/// </summary>
public sealed partial class QuickSettingsFlyout
{
    /// <summary>How long Connect waits for Windows to report the network (it connects in the background).</summary>
    private const int SavedWaitMs = 15000;
    private const int SavedStepMs = 500;

    private readonly List<SavedRow> _savedRows = new();

    /// <summary>The list shows Windows' saved networks (no location access).</summary>
    private bool _savedMode;
    private SavedRow? _savedOpen;
    private bool _savedConnecting;

    /// <summary>Shows Windows' saved networks in the list; with none saved, the note about location stays.</summary>
    private void ShowSavedNetworks()
    {
        if (!_wifiPage || _wifiListPanel is null)
        {
            return;
        }

        List<string> saved = WifiSaved.Read();
        _savedRows.Clear();
        _savedOpen = null;
        _savedMode = saved.Count > 0;
        _wifiListPanel.Children.Clear();
        if (_savedMode)
        {
            _wifiListPanel.Children.Add(MakeSavedIntro());
            foreach (string name in saved)
            {
                SavedRow row = MakeSavedRow(name);
                _savedRows.Add(row);
                _wifiListPanel.Children.Add(row.Box);
            }

            ShowSavedStates();
        }

        ShowWifiNote();
    }

    private void ClearSavedRows()
    {
        _savedRows.Clear();
        _savedOpen = null;
        _savedMode = false;
    }

    private StackPanel MakeSavedIntro()
    {
        var text = new TextBlock
        {
            Text = Strings.WifiSavedNote,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        };
        Button location = MakeTextButton(Strings.WifiLocationSettings, () =>
        {
            HideFlyout();
            if (Launcher.Start("ms-settings:privacy-location"))
            {
                Log.Info("Quick settings: Windows' location settings.");
            }
        });
        location.HorizontalAlignment = HorizontalAlignment.Left;
        var title = new TextBlock
        {
            Text = Strings.WifiSavedTitle,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var intro = new StackPanel { Spacing = 8, Margin = new Thickness(10, 4, 10, 2) };
        intro.Children.Add(text);
        intro.Children.Add(location);
        intro.Children.Add(title);
        return intro;
    }

    /// <summary>A saved network: a button with the Wi-Fi sign (no bars: nothing was measured), the name and the state; below it Connect or Disconnect while open.</summary>
    private SavedRow MakeSavedRow(string name)
    {
        FontIcon icon = MakeGlyph(WifiGlyph, 18);
        icon.Opacity = 0.75;
        icon.Width = 22;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var title = new TextBlock { Text = name, FontSize = 14, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { FontSize = 12, Opacity = 0.75, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(title);
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

        var row = new SavedRow(name, box, head, status, details);
        head.Click += (_, _) => ToggleSavedRow(row);
        return row;
    }

    /// <summary>Which saved network is connected now (Windows' network list; no location needed).</summary>
    private void ShowSavedStates()
    {
        if (!_savedMode)
        {
            return;
        }

        string? connected = _toggles.StateOf(QuickToggle.WiFi) == SwitchState.On ? QuickToggles.WifiName() : null;
        foreach (SavedRow row in _savedRows)
        {
            bool now = string.Equals(row.Name, connected, StringComparison.Ordinal);
            bool changed = now != row.Connected;
            row.Connected = now;
            string status = now ? Strings.QsConnected : Strings.WifiSavedState;
            if (row.Status.Text != status)
            {
                row.Status.Text = status;
            }

            AutomationProperties.SetName(row.Head, row.Name + ", " + status);
            if (changed && row.Open && !row.Connecting)
            {
                FillSavedDetails(row);
            }
        }
    }

    private void ToggleSavedRow(SavedRow row)
    {
        if (_savedConnecting)
        {
            return;
        }

        if (row.Open)
        {
            row.Open = false;
            row.Error = null;
            _savedOpen = null;
            FillSavedDetails(row);
            return;
        }

        if (_savedOpen is SavedRow open)
        {
            open.Open = false;
            open.Error = null;
            FillSavedDetails(open);
        }

        row.Open = true;
        row.Error = null;
        _savedOpen = row;
        FillSavedDetails(row);
    }

    private void FillSavedDetails(SavedRow row)
    {
        StackPanel details = row.Details;
        details.Children.Clear();
        details.Visibility = row.Open ? Visibility.Visible : Visibility.Collapsed;
        row.Box.Background = row.Open ? _wifiOpenBrush : null;
        if (!row.Open)
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

        if (row.Connecting)
        {
            var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16 };
            AutomationProperties.SetName(ring, Strings.WifiConnecting);
            var text = new TextBlock { Text = Strings.WifiConnecting, VerticalAlignment = VerticalAlignment.Center };
            var waiting = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            waiting.Children.Add(ring);
            waiting.Children.Add(text);
            details.Children.Add(waiting);
            return;
        }

        details.Children.Add(row.Connected
            ? MakeWifiButtons(MakeWifiButton(Strings.WifiDisconnect, accent: false, () => _ = DisconnectSavedAsync(row)))
            : MakeWifiButtons(MakeWifiButton(Strings.WifiConnect, accent: true, () => _ = ConnectSavedAsync(row))));
    }

    /// <summary>Asks Windows to connect, then waits up to 15 s for it to report that network.</summary>
    private async Task ConnectSavedAsync(SavedRow row)
    {
        if (_savedConnecting)
        {
            return;
        }

        _savedConnecting = true;
        row.Connecting = true;
        row.Error = null;
        FillSavedDetails(row);
        bool taken = WifiSaved.Connect(row.Name);
        bool connected = false;
        try
        {
            for (int waited = 0; taken && waited < SavedWaitMs && _wifiPage; waited += SavedStepMs)
            {
                await Task.Delay(SavedStepMs);
                if (string.Equals(QuickToggles.WifiName(), row.Name, StringComparison.Ordinal))
                {
                    connected = true;
                    break;
                }
            }
        }
        finally
        {
            _savedConnecting = false;
        }

        // Back on the UI thread; the list may have closed meanwhile.
        if (!_wifiPage || !_savedRows.Contains(row))
        {
            return;
        }

        row.Connecting = false;
        if (connected)
        {
            Log.Info("Wi-Fi list: the saved network connected.");
            row.Open = false;
            _savedOpen = null;
        }
        else
        {
            if (taken)
            {
                Log.Info("Wi-Fi list: Windows did not report the saved network as connected within 15 s.");
            }

            row.Error = taken ? Strings.WifiSavedFailed : Strings.WifiErrFailed;
        }

        FillSavedDetails(row);
        ShowSavedStates();
        ShowToggles();
    }

    private async Task DisconnectSavedAsync(SavedRow row)
    {
        if (!WifiSaved.Disconnect())
        {
            row.Error = Strings.WifiErrDisconnect;
            FillSavedDetails(row);
            return;
        }

        row.Open = false;
        row.Error = null;
        _savedOpen = null;
        FillSavedDetails(row);

        // Windows reports it a moment later.
        await Task.Delay(1000);
        ShowSavedStates();
        ShowToggles();
    }

    /// <summary>A saved network shown in the list.</summary>
    private sealed class SavedRow
    {
        internal SavedRow(string name, Border box, Button head, TextBlock status, StackPanel details)
        {
            Name = name;
            Box = box;
            Head = head;
            Status = status;
            Details = details;
        }

        internal string Name { get; }

        internal Border Box { get; }

        internal Button Head { get; }

        internal TextBlock Status { get; }

        internal StackPanel Details { get; }

        internal bool Open { get; set; }

        internal bool Connected { get; set; }

        internal bool Connecting { get; set; }

        internal string? Error { get; set; }
    }
}
