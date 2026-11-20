using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Machitan.Flyouts;

/// <summary>
/// Machitan's own Win+X menu (Windows' "Quick Link menu"), with Windows' entries in Windows'
/// order and groups, opening above the bar's Start button (below it for a top bar).
///
/// Windows' own menu could not be used: on this Windows build it is a XAML popup of Explorer
/// that Windows places by its own (hidden) Start button and keeps placing again, so moving it
/// made it jump (Parts 10 to 12, log of 2026-11-20).
///
/// Each label marks its key with '&amp;' (underlined, as Windows shows them after Win+X): that
/// letter picks the entry. Up and Down move, Enter picks, Right opens "Shut down or sign out",
/// Left and Esc close it. Entries Windows shows only on some PCs follow the same rule here:
/// Mobility Center on PCs with a battery; Terminal when Windows Terminal is installed, else
/// Windows PowerShell; Sleep and Hibernate when the PC can and Windows' "Show in power menu"
/// choices have them; the update entries while an update waits for a restart. Everything
/// runs at once, without a question, as in Windows' menu.
/// </summary>
public sealed partial class WinXMenuWindow : FlyoutWindow
{
    private const double RowHeightDip = 32;
    private const double MinMenuWidthDip = 168;
    private const double InnerPadDip = 4;
    private const double SeparatorMarginDip = 4;
    private const double SubGapDip = 4;
    private const int HoverDelayMs = 300;
    private const int ErrorCancelled = 1223;
    private const int ChevronGlyph = 0xE76C;

    private readonly StackPanel _mainRows;
    private readonly StackPanel _subRows;
    private readonly Grid _mainBox;
    private readonly Grid _subBox;
    private readonly Grid _layout;
    private readonly List<MenuRow> _main = new();
    private readonly List<MenuRow> _sub = new();
    private readonly DispatcherQueueTimer _hoverTimer;
    private Button? _subOpener;
    private Button? _hoverTarget;
    private double _subTopDip;

    internal WinXMenuWindow(ShellHost host)
        : base(host, "Win+X menu")
    {
        _mainRows = new StackPanel { Padding = new Thickness(InnerPadDip) };
        _subRows = new StackPanel { Padding = new Thickness(InnerPadDip) };
        AutomationProperties.SetName(_mainRows, Strings.WinXMenuName);
        AutomationProperties.SetName(_subRows, Strings.WinXShutDownMenu.Replace("&", string.Empty, StringComparison.Ordinal));

        _mainBox = MakeBox(_mainRows, BoxRadius);
        _mainBox.VerticalAlignment = VerticalAlignment.Top;
        _subBox = MakeBox(_subRows, BoxRadius);
        _subBox.VerticalAlignment = VerticalAlignment.Top;
        _subBox.Visibility = Visibility.Collapsed;

        _layout = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_mainBox, 1);
        _layout.Children.Add(_mainBox);
        _layout.Children.Add(_subBox);
        Root.Children.Add(_layout);

        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(HoverDelayMs);
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (_, _) => OnHoverSettled();
        _subBox.PointerEntered += (_, _) => _hoverTimer.Stop();
    }

    private bool SubOpen => _subBox.Visibility == Visibility.Visible;

    /// <summary>Win+X: opens the menu above the Start button, or closes it when open.</summary>
    internal void Toggle()
    {
        if (IsOpen)
        {
            HideFlyout();
            return;
        }

        Fill();
        Place();
        DispatcherQueue.TryEnqueue(() => FocusRow(_main, 0, FocusState.Programmatic));
    }

    private protected override void OnHiding()
    {
        _hoverTimer.Stop();
        _hoverTarget = null;
        _subBox.Visibility = Visibility.Collapsed;
    }

    private protected override void OnPreviewKey(KeyRoutedEventArgs e)
    {
        bool inSub = SubOpen && IndexOfFocused(_sub) >= 0;
        List<MenuRow> level = inSub ? _sub : _main;
        switch (e.Key)
        {
            case VirtualKey.Down:
                Move(level, +1);
                e.Handled = true;
                return;
            case VirtualKey.Up:
                Move(level, -1);
                e.Handled = true;
                return;
            case VirtualKey.Right:
                int at = inSub ? -1 : IndexOfFocused(_main);
                if (at >= 0 && _subOpener is not null && ReferenceEquals(_main[at].Button, _subOpener))
                {
                    OpenSub(focusFirst: true);
                    e.Handled = true;
                }

                return;
            case VirtualKey.Left:
            case VirtualKey.Escape:
                if (SubOpen)
                {
                    CloseSubAndFocusOpener();
                    e.Handled = true;
                }

                return;
        }

        int code = (int)e.Key;
        if (code < (int)VirtualKey.A || code > (int)VirtualKey.Z)
        {
            return;
        }

        char key = (char)code;
        foreach (MenuRow row in level)
        {
            if (row.Key == key)
            {
                e.Handled = true;
                Pick(row);
                return;
            }
        }
    }

    // ---------------------------------------------------------------- building

    /// <summary>The entries as Windows' Win+X menu has them on this PC right now.</summary>
    private void Fill()
    {
        _main.Clear();
        _sub.Clear();
        _mainRows.Children.Clear();
        _subRows.Children.Clear();
        _subOpener = null;

        AddRow(_main, _mainRows, Strings.WinXInstalledApps, () => Open("ms-settings:appsfeatures", "Installed apps"));
        if (HasMobilityCenter())
        {
            AddRow(_main, _mainRows, Strings.WinXMobilityCenter, () => Open(SystemFile("mblctr.exe"), "Mobility Center"));
        }

        AddRow(_main, _mainRows, Strings.WinXPowerOptions, () => Open("ms-settings:powersleep", "Power Options"));
        AddRow(_main, _mainRows, Strings.WinXEventViewer, () => Open(SystemFile("eventvwr.msc"), "Event Viewer"));
        AddRow(_main, _mainRows, Strings.WinXSystem, () => Open("ms-settings:about", "System"));
        AddRow(_main, _mainRows, Strings.WinXDeviceManager, () => Open(SystemFile("devmgmt.msc"), "Device Manager"));
        AddRow(_main, _mainRows, Strings.WinXNetworkConnections, () => Open("ms-settings:network", "Network Connections"));
        AddRow(_main, _mainRows, Strings.WinXDiskManagement, () => Open(SystemFile("diskmgmt.msc"), "Disk Management"));
        AddRow(_main, _mainRows, Strings.WinXComputerManagement, () => Open(SystemFile("compmgmt.msc"), "Computer Management"));

        string terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(terminal))
        {
            AddRow(_main, _mainRows, Strings.WinXTerminal, () => StartInHome(terminal, elevated: false, "Terminal"));
            AddRow(_main, _mainRows, Strings.WinXTerminalAdmin, () => StartInHome(terminal, elevated: true, "Terminal (Admin)"));
        }
        else
        {
            string powerShell = SystemFile(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"));
            AddRow(_main, _mainRows, Strings.WinXPowerShell, () => StartInHome(powerShell, elevated: false, "Windows PowerShell"));
            AddRow(_main, _mainRows, Strings.WinXPowerShellAdmin, () => StartInHome(powerShell, elevated: true, "Windows PowerShell (Admin)"));
        }

        AddSeparator(_mainRows);
        AddRow(_main, _mainRows, Strings.WinXTaskManager, () => Open(SystemFile("taskmgr.exe"), "Task Manager"));
        AddRow(_main, _mainRows, Strings.WinXSettings, () => Open("ms-settings:", "Settings"));
        AddRow(_main, _mainRows, Strings.WinXFileExplorer, () => Open("explorer.exe", "File Explorer"));
        AddRow(_main, _mainRows, Strings.WinXSearch, () => Host.StartMenu?.Open());
        AddRow(_main, _mainRows, Strings.WinXRun, HotkeyActions.ShowRunBox);
        AddSeparator(_mainRows);

        MenuRow opener = AddRow(_main, _mainRows, Strings.WinXShutDownMenu, run: null, opensSub: true);
        _subOpener = opener.Button;
        AddRow(_main, _mainRows, Strings.WinXDesktop, () => Host.Desktop.Toggle(Host.Tasks.Groups));

        foreach (PowerChoice choice in PowerActions.WinXChoices())
        {
            AddRow(_sub, _subRows, choice.Label, () => choice.Run(Hwnd));
        }
    }

    private MenuRow AddRow(List<MenuRow> level, StackPanel panel, string label, Action? run, bool opensSub = false)
    {
        (string text, int keyAt) = SplitLabel(label);
        var words = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        if (keyAt >= 0)
        {
            words.Inlines.Add(new Run { Text = text[..keyAt] });
            words.Inlines.Add(new Run { Text = text[keyAt].ToString(), TextDecorations = Windows.UI.Text.TextDecorations.Underline });
            words.Inlines.Add(new Run { Text = text[(keyAt + 1)..] });
        }
        else
        {
            words.Text = text;
        }

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(words);
        if (opensSub)
        {
            FontIcon chevron = MakeGlyph(ChevronGlyph, 10);
            chevron.Margin = new Thickness(16, 0, 0, 0);
            chevron.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chevron, 1);
            content.Children.Add(chevron);
        }

        var button = new Button
        {
            Content = content,
            Height = RowHeightDip,
            MinWidth = 0,
            Padding = new Thickness(12, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };

        char key = keyAt >= 0 ? char.ToUpperInvariant(text[keyAt]) : '\0';
        AutomationProperties.SetName(button, text);
        if (key != '\0')
        {
            AutomationProperties.SetAccessKey(button, key.ToString());
        }

        var row = new MenuRow(button, key, run, opensSub);
        button.Click += (_, _) => Pick(row);
        button.PointerEntered += (_, _) => OnRowHovered(level, button);
        panel.Children.Add(button);
        level.Add(row);
        return row;
    }

    private void AddSeparator(StackPanel panel)
    {
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(SeparatorMarginDip, SeparatorMarginDip, SeparatorMarginDip, SeparatorMarginDip),
            Background = new SolidColorBrush(IsDark ? ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x26, 0x00, 0x00, 0x00)),
        });
    }

    /// <summary>"Mo&amp;bility Center" gives "Mobility Center" and the place of its key (-1 for none); "&amp;&amp;" is a plain '&amp;'.</summary>
    private static (string Text, int KeyAt) SplitLabel(string label)
    {
        var text = new System.Text.StringBuilder(label.Length);
        int keyAt = -1;
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] == '&' && i + 1 < label.Length)
            {
                if (label[i + 1] == '&')
                {
                    text.Append('&');
                    i++;
                    continue;
                }

                if (keyAt < 0)
                {
                    keyAt = text.Length;
                }

                continue;
            }

            text.Append(label[i]);
        }

        return (text.ToString(), keyAt < text.Length ? keyAt : -1);
    }

    // ---------------------------------------------------------------- placing

    /// <summary>
    /// The menu's left edge at the Start button's, its bottom 8 DIP above it (its top 8 DIP
    /// below it for a top bar); "Shut down or sign out" opens beside it on the side with room,
    /// lined up with its row, kept above the bar.
    /// </summary>
    private void Place()
    {
        var unbounded = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        _mainRows.Measure(unbounded);
        _subBox.Visibility = Visibility.Visible;
        _subRows.Measure(unbounded);
        _subBox.Visibility = Visibility.Collapsed;

        double mainWidth = Math.Max(MinMenuWidthDip, Math.Ceiling(_mainRows.DesiredSize.Width));
        double mainHeight = Math.Ceiling(_mainRows.DesiredSize.Height);
        double subWidth = Math.Max(MinMenuWidthDip * 0.75, Math.Ceiling(_subRows.DesiredSize.Width));
        double subHeight = Math.Ceiling(_subRows.DesiredSize.Height);
        _mainBox.Width = mainWidth;
        _mainBox.Height = mainHeight;
        _subBox.Width = subWidth;
        _subBox.Height = subHeight;

        // The submenu's first row level with its opener, moved up when it would reach below the menu.
        double openerTop = InnerPadDip;
        foreach (UIElement child in _mainRows.Children)
        {
            if (ReferenceEquals(child, _subOpener))
            {
                break;
            }

            openerTop += child is Border ? 1 + (2 * SeparatorMarginDip) : RowHeightDip;
        }

        _subTopDip = Math.Max(0, Math.Min(openerTop - InnerPadDip, mainHeight - subHeight));
        double contentWidth = mainWidth + SubGapDip + subWidth;
        double contentHeight = Math.Max(mainHeight, _subTopDip + subHeight);

        double scale = Scale();
        int pad = (int)Math.Round(PadDip * scale);
        int gap = (int)Math.Round(GapToBarDip * scale);
        int width = (int)Math.Round((contentWidth + (2 * PadDip)) * scale);
        int height = (int)Math.Round((contentHeight + (2 * PadDip)) * scale);
        RectInt32 screen = DisplayArea.Primary.OuterBounds;
        RECT anchor = Host.ElementRect(new CustomTarget(CustomPiece.Start)) ?? Host.BarRect;
        bool barOnTop = Host.Settings.Current.DockEdge == DockEdge.Top;

        // The submenu goes right of the menu when there is room, else left of it.
        bool subRight = anchor.Left - pad + width <= screen.X + screen.Width;
        Grid.SetColumn(_subBox, subRight ? 2 : 0);
        _subBox.Margin = subRight ? new Thickness(SubGapDip, _subTopDip, 0, 0) : new Thickness(0, _subTopDip, SubGapDip, 0);

        int mainLeft = pad + (subRight ? 0 : (int)Math.Round((subWidth + SubGapDip) * scale));
        int x = anchor.Left - mainLeft;
        int mainHeightPx = (int)Math.Round(mainHeight * scale);
        int y = barOnTop ? anchor.Bottom + gap - pad : anchor.Top - gap - mainHeightPx - pad;
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - width));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - height));
        PresentAt(new RectInt32(x, y, width, height));
    }

    // ---------------------------------------------------------------- acting

    private void Pick(MenuRow row)
    {
        if (row.OpensSub)
        {
            OpenSub(focusFirst: true);
            return;
        }

        if (row.Run is not { } run)
        {
            return;
        }

        // Closed first, so what opens next can come to the front.
        HideFlyout();
        try
        {
            run();
        }
        catch (Exception ex)
        {
            Log.Error("Win+X menu: an entry could not be run.", ex);
        }
    }

    private void OpenSub(bool focusFirst)
    {
        _hoverTimer.Stop();
        if (_sub.Count == 0)
        {
            return;
        }

        _subBox.Visibility = Visibility.Visible;
        if (focusFirst)
        {
            DispatcherQueue.TryEnqueue(() => FocusRow(_sub, 0, FocusState.Keyboard));
        }
    }

    private void CloseSubAndFocusOpener()
    {
        _hoverTimer.Stop();
        _subBox.Visibility = Visibility.Collapsed;
        _subOpener?.Focus(FocusState.Keyboard);
    }

    /// <summary>Pointing at "Shut down or sign out" opens it after a moment; pointing at another entry closes it.</summary>
    private void OnRowHovered(List<MenuRow> level, Button button)
    {
        if (!ReferenceEquals(level, _main))
        {
            _hoverTimer.Stop();
            return;
        }

        bool isOpener = ReferenceEquals(button, _subOpener);
        if (isOpener == SubOpen)
        {
            _hoverTimer.Stop();
            return;
        }

        _hoverTarget = button;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnHoverSettled()
    {
        if (!IsOpen || _hoverTarget is null)
        {
            return;
        }

        if (ReferenceEquals(_hoverTarget, _subOpener))
        {
            OpenSub(focusFirst: false);
        }
        else
        {
            _subBox.Visibility = Visibility.Collapsed;
        }
    }

    private void Move(List<MenuRow> level, int step)
    {
        if (level.Count == 0)
        {
            return;
        }

        int at = IndexOfFocused(level);
        int next = at < 0 ? (step > 0 ? 0 : level.Count - 1) : (at + step + level.Count) % level.Count;
        FocusRow(level, next, FocusState.Keyboard);
    }

    private int IndexOfFocused(List<MenuRow> level)
    {
        if (Root.XamlRoot is null || FocusManager.GetFocusedElement(Root.XamlRoot) is not Button focused)
        {
            return -1;
        }

        for (int i = 0; i < level.Count; i++)
        {
            if (ReferenceEquals(level[i].Button, focused))
            {
                return i;
            }
        }

        return -1;
    }

    private static void FocusRow(List<MenuRow> level, int index, FocusState how)
    {
        if (index >= 0 && index < level.Count)
        {
            level[index].Button.Focus(how);
        }
    }

    private static string SystemFile(string name)
    {
        return Path.Combine(Environment.SystemDirectory, name);
    }

    /// <summary>Windows shows Mobility Center on PCs with a battery (laptops, tablets) where it is installed.</summary>
    private static bool HasMobilityCenter()
    {
        return File.Exists(SystemFile("mblctr.exe"))
            && Kernel32.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status)
            && status.BatteryFlag != Kernel32.BATTERY_FLAG_UNKNOWN
            && (status.BatteryFlag & Kernel32.BATTERY_FLAG_NO_BATTERY) == 0;
    }

    private static void Open(string target, string what)
    {
        if (Launcher.Start(target))
        {
            Log.Info("Win+X menu: " + what + ".");
        }
    }

    /// <summary>Terminal or PowerShell, starting in the user's folder as from Windows' menu; elevated asks Windows first.</summary>
    private static void StartInHome(string program, bool elevated, string what)
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(program)
            {
                UseShellExecute = true,
                Verb = elevated ? "runas" : string.Empty,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            Log.Info("Win+X menu: " + what + ".");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Win+X menu: " + what + " was not allowed in Windows' question.");
        }
        catch (Exception ex)
        {
            Log.Error("Win+X menu: " + what + " could not be started.", ex);
        }
    }

    /// <summary>One entry: its button, its key (or '\0'), what it does, and whether it opens the submenu.</summary>
    private sealed record MenuRow(Button Button, char Key, Action? Run, bool OpensSub);
}
