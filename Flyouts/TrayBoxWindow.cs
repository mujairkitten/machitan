using System;
using System.Collections.Generic;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Machitan.Flyouts;

/// <summary>
/// The tray box: when the tray sits on the bar, its icons live in this box behind the bar's ^
/// button, as in Windows' taskbar ("Show hidden icons"), unless they were dragged out onto the
/// bar. It opens above the ^ button (below it for a top bar), one rounded box with the icons
/// in rows of up to five. A click goes to the icon's app as on the bar; dragging an icon out
/// closes the box and hands the icon to the bar's drag, so it can land among the bar's tray
/// icons. Dragging a bar tray icon onto the ^ button puts it back in the box.
/// After a right-click the app shows its menu, which takes the focus; the box stays open
/// meanwhile, as Windows' own does, until a menu item is picked, a click lands outside both
/// the box and that app's windows (its menu included), another app comes to the front, or
/// the box is used again.
/// </summary>
public sealed partial class TrayBoxWindow : FlyoutWindow
{
    private const int MaxColumns = 5;
    private const double CellDip = 40;
    private const double CellSpacingDip = 2;
    private const double InnerPadDip = 6;
    private const double DragStartDip = 6;
    private const int MenuWatchMs = 60;
    private const int VkMButton = 0x04;

    private readonly Grid _cells;
    private readonly Grid _box;
    private RECT _anchor;
    private string? _lastKey;
    private long _lastAt;
    private (TrayIcon Icon, POINT At)? _press;

    // After a right-click: kept open while the icon's app shows its menu (see WatchMenu).
    private readonly DispatcherQueueTimer _menuWatch;
    private bool _keptForMenu;
    private uint _menuApp;
    private bool _buttonWasDown;
    private bool _closeOnRelease;

    internal TrayBoxWindow(ShellHost host)
        : base(host, "Tray box")
    {
        _cells = new Grid
        {
            Padding = new Thickness(InnerPadDip),
            RowSpacing = CellSpacingDip,
            ColumnSpacing = CellSpacingDip,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(_cells, Strings.TrayHiddenIcons);
        _box = MakeBox(_cells, BoxRadius);
        _box.HorizontalAlignment = HorizontalAlignment.Left;
        _box.VerticalAlignment = VerticalAlignment.Top;
        Root.Children.Add(_box);

        _menuWatch = DispatcherQueue.CreateTimer();
        _menuWatch.Interval = TimeSpan.FromMilliseconds(MenuWatchMs);
        _menuWatch.Tick += (_, _) => WatchMenu();

        // Used again (a click in the box gives it the focus back): the usual rules apply.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                StopKeeping();
            }
        };

        Host.Tray.Changed += OnContentChanged;
        Host.BarLayout.Changed += OnContentChanged;
        Closed += (_, _) =>
        {
            Host.Tray.Changed -= OnContentChanged;
            Host.BarLayout.Changed -= OnContentChanged;
        };
    }

    /// <summary>The box opened (true) or closed (false); the bar turns its ^ over meanwhile.</summary>
    internal static event Action<bool>? OpenChanged;

    /// <summary>Opens the box by the ^ button (its place on screen, physical pixels), or closes it when open.</summary>
    internal void Toggle(RECT anchor)
    {
        if (IsOpen)
        {
            HideFlyout();
            return;
        }

        _anchor = anchor;
        if (!Fill())
        {
            return;
        }

        Place();
    }

    private protected override void OnHiding()
    {
        _press = null;
        StopKeeping();
    }

    private protected override bool KeepOpenWhenDeactivated => _keptForMenu;

    private protected override void OnOpenChanged(bool open)
    {
        OpenChanged?.Invoke(open);
    }

    /// <summary>The box rises from the bar as it opens and fades back as it closes.</summary>
    private protected override UIElement? MotionTarget => _box;

    /// <summary>A right-click on an icon: the box stays while its app shows the menu.</summary>
    private void KeepForMenu(TrayIcon icon)
    {
        _keptForMenu = true;
        _menuApp = Host.Tray.OwnerOf(icon);
        _buttonWasDown = AnyButtonDown();
        _closeOnRelease = false;
        _menuWatch.Start();
    }

    private void StopKeeping()
    {
        _keptForMenu = false;
        _menuApp = 0;
        _closeOnRelease = false;
        _menuWatch.Stop();
    }

    /// <summary>
    /// While kept open after a right-click: a mouse button pressed outside the box and outside
    /// the icon's app (its menu is one of its windows), or another app coming to the front,
    /// closes the box. A press on the ^ button is left to the button (it closes the box itself).
    /// A press in the app's menu is a choice: the box closes when the button is let go, as
    /// Windows' own box does once a menu item is picked.
    /// </summary>
    private void WatchMenu()
    {
        if (!IsOpen || !_keptForMenu)
        {
            StopKeeping();
            return;
        }

        bool down = AnyButtonDown();
        bool pressed = down && !_buttonWasDown;
        _buttonWasDown = down;

        if (_closeOnRelease && !down)
        {
            StopKeeping();
            HideFlyout();
            return;
        }

        nint front = User32.GetForegroundWindow();
        if (front != 0 && front != Hwnd && !IsOwnOrApp(front))
        {
            StopKeeping();
            HideFlyout();
            return;
        }

        if (!pressed || !User32.GetCursorPos(out POINT at))
        {
            return;
        }

        if (at.X >= _anchor.Left && at.X < _anchor.Right && at.Y >= _anchor.Top && at.Y < _anchor.Bottom)
        {
            return;
        }

        nint under = User32.WindowFromPoint(at);
        nint root = under == 0 ? 0 : User32.GetAncestor(under, User32.GA_ROOT);
        if (root == Hwnd)
        {
            // A click in the box: it takes the focus again, and the usual rules apply.
            StopKeeping();
            return;
        }

        if (root != 0 && IsApp(root))
        {
            _closeOnRelease = true;
            return;
        }

        StopKeeping();
        HideFlyout();
    }

    /// <summary>A window of the icon's app (its menu or a window it opened).</summary>
    private bool IsApp(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint process);
        return process != 0 && process == _menuApp;
    }

    /// <summary>A window of the icon's app, or of Machitan.</summary>
    private bool IsOwnOrApp(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint process);
        return process != 0 && (process == _menuApp || process == (uint)Environment.ProcessId);
    }

    private static bool AnyButtonDown()
    {
        return (User32.GetAsyncKeyState(User32.VK_LBUTTON) & 0x8000) != 0
            || (User32.GetAsyncKeyState(User32.VK_RBUTTON) & 0x8000) != 0
            || (User32.GetAsyncKeyState(VkMButton) & 0x8000) != 0;
    }

    /// <summary>Icons came, went, changed, or moved in or out of the box while it is open.</summary>
    private void OnContentChanged()
    {
        if (!IsOpen)
        {
            return;
        }

        if (!Fill())
        {
            HideFlyout();
            return;
        }

        Place();
    }

    /// <summary>The icons in the box, in the tray's kept order. False when the box is empty.</summary>
    private bool Fill()
    {
        var icons = new List<TrayIcon>();
        foreach (TrayIcon icon in Host.BarLayout.ArrangeTray(Host.Tray.Icons))
        {
            if (!Host.BarLayout.IsTrayIconOut(icon))
            {
                icons.Add(icon);
            }
        }

        _cells.Children.Clear();
        _cells.RowDefinitions.Clear();
        _cells.ColumnDefinitions.Clear();
        if (icons.Count == 0)
        {
            return false;
        }

        int columns = Math.Min(icons.Count, MaxColumns);
        int rows = (icons.Count + columns - 1) / columns;
        for (int c = 0; c < columns; c++)
        {
            _cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellDip) });
        }

        for (int r = 0; r < rows; r++)
        {
            _cells.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellDip) });
        }

        for (int i = 0; i < icons.Count; i++)
        {
            Button cell = MakeCell(icons[i]);
            Grid.SetColumn(cell, i % columns);
            Grid.SetRow(cell, i / columns);
            _cells.Children.Add(cell);
        }

        _box.Width = (2 * InnerPadDip) + (columns * CellDip) + ((columns - 1) * CellSpacingDip);
        _box.Height = (2 * InnerPadDip) + (rows * CellDip) + ((rows - 1) * CellSpacingDip);
        return true;
    }

    private Button MakeCell(TrayIcon icon)
    {
        string name = icon.Name.Length > 0 ? icon.Name : Strings.TrayIcon;
        var button = new Button
        {
            Content = TrayPicture(icon, name),
            Width = CellDip,
            Height = CellDip,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name.Replace("\n", ", ", StringComparison.Ordinal));

        string key = icon.Key;
        button.Click += (_, _) => OnCellClick(icon, key);
        button.RightTapped += (_, e) =>
        {
            e.Handled = true;
            _lastKey = null;
            KeepForMenu(icon);
            Host.Tray.Click(icon, TrayClick.Right);
        };

        // Drag out: pressed, then moved far enough (read from the screen, as the bar's drag does).
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed && User32.GetCursorPos(out POINT at))
            {
                _press = (icon, at);
            }
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => OnCellPointerMoved()), handledEventsToo: true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _press = null), handledEventsToo: true);
        return button;
    }

    private void OnCellPointerMoved()
    {
        if (_press is not { } press || !User32.GetCursorPos(out POINT now))
        {
            return;
        }

        double limit = DragStartDip * Scale();
        if (Math.Abs(now.X - press.At.X) < limit && Math.Abs(now.Y - press.At.Y) < limit)
        {
            return;
        }

        _press = null;
        TrayIcon icon = press.Icon;
        HideFlyout();
        Host.StartTrayDragFromBox(icon);
    }

    /// <summary>A click goes to the icon's app; a second one within Windows' double-click time is a double click.</summary>
    private void OnCellClick(TrayIcon icon, string key)
    {
        long now = Environment.TickCount64;
        long doubleMs = Math.Clamp((long)User32.GetDoubleClickTime(), 200L, 900L);
        if (string.Equals(_lastKey, key, StringComparison.Ordinal) && now - _lastAt <= doubleMs)
        {
            _lastKey = null;
            Host.Tray.Click(icon, TrayClick.Double);
            return;
        }

        _lastKey = key;
        _lastAt = now;
        Host.Tray.Click(icon, TrayClick.Left);
    }

    /// <summary>Centred on the ^ button, above the bar (below a top bar), kept on the screen.</summary>
    private void Place()
    {
        double scale = Scale();
        RectInt32 screen = DisplayArea.Primary.OuterBounds;
        int width = (int)Math.Round((_box.Width + (2 * PadDip)) * scale);
        int height = (int)Math.Round((_box.Height + (2 * PadDip)) * scale);
        int pad = (int)Math.Round(PadDip * scale);
        int gap = (int)Math.Round(GapToBarDip * scale);

        RECT bar = Host.BarRect;
        bool top = Host.Settings.Current.DockEdge == DockEdge.Top;
        int x = ((_anchor.Left + _anchor.Right) / 2) - (width / 2);
        int y = top ? bar.Bottom + gap - pad : bar.Top - gap - height + pad;
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - width));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - height));
        PresentAt(new RectInt32(x, y, width, height));
    }

    /// <summary>The app's own tray picture at Windows' tray size (16), else its name's first letter.</summary>
    private static FrameworkElement TrayPicture(TrayIcon icon, string name)
    {
        if (icon.Picture is IconPixels pixels)
        {
            try
            {
                return new Image
                {
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    Source = Machitan.Start.StartMenuWindow.ToBitmap(pixels),
                };
            }
            catch (Exception ex)
            {
                Log.Warn("Tray box: a tray icon's picture could not be shown.", ex);
            }
        }

        return new TextBlock
        {
            Text = name.Substring(0, 1).ToUpperInvariant(),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
