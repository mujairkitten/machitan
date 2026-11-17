using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace Machitan.Flyouts;

/// <summary>
/// Window previews: hover a running app on the bar's dock and its windows show above the
/// button as live pictures (drawn by Windows through the DWM thumbnail API), each with the
/// app's name, the window's title and a close button. A click on a preview brings that
/// window back (restored if minimised). Pinned apps that are not running get none.
///
/// It never takes the keyboard from the app in use. It opens about 0.4 s after the pointer
/// rests on a button, follows the pointer from button to button at once, stays while the
/// pointer is over it, and goes about 0.3 s after the pointer leaves both.
/// The cards are made once and reused, so hovering around builds nothing new.
///
/// Peek: resting on a card for about 0.3 s brings its window to the front for a moment
/// (shown without activating if it was minimised), without taking the keyboard. Moving to
/// another card switches at once; moving away puts the window back exactly where it was
/// (behind the same window, or minimised again). A click keeps it in front for real.
/// Windows does not let a background program simply move another app's window in front of
/// the active one, so for the moment of the peek the window is made always-on-top (the bar
/// and this preview are raised above it again), and that is always undone afterwards.
/// Windows' own Peek also turns the other windows see-through; that needs an undocumented
/// Windows call, so it is not done here.
/// </summary>
public sealed partial class PreviewWindow : FlyoutWindow
{
    private const int MaxCards = 8;
    private const double CardWidthDip = 232;
    private const double MinCardWidthDip = 160;
    private const double MoreCardWidthDip = 96;
    private const double CardPadDip = 8;
    private const double HeaderHeightDip = 36;
    private const double PictureGapDip = 6;
    private const double CardSpacingDip = 8;
    private const double CardRadius = 12;
    private const double ScreenMarginDip = 8;
    private const double PictureAspect = 0.6;

    private const int ShowDelayMs = 400;
    private const int HideDelayMs = 300;
    private const int PeekDelayMs = 300;

    /// <summary>Moving from one card to the next: the gap between them does not end a peek.</summary>
    private const int PeekEndGraceMs = 120;
    private const int CloseGlyph = 0xE8BB;

    private readonly StackPanel _strip;
    private readonly List<Card> _cards = new();
    private readonly Grid _moreBox;
    private readonly TextBlock _moreText;
    private readonly DispatcherQueueTimer _showTimer;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _peekTimer;
    private readonly DispatcherQueueTimer _peekEndTimer;

    private DockItem? _pendingItem;
    private RECT _pendingButton;
    private string? _groupKey;
    private RECT _button;
    private bool _pointerInside;
    private string _pictureSignature = string.Empty;

    private nint _peekCandidate;
    private nint _peekWindow;
    private bool _peekWasMinimized;
    private nint _peekWasBehind;
    private bool _peekMadeTopmost;
    private bool _peekLogged;

    internal PreviewWindow(ShellHost host)
        : base(host, "Window preview", takesFocus: false)
    {
        _strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = CardSpacingDip,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Root.Children.Add(_strip);

        for (int i = 0; i < MaxCards; i++)
        {
            var card = new Card(this);
            card.Box = MakeBox(card.Content, CardRadius);
            card.Box.Visibility = Visibility.Collapsed;
            _strip.Children.Add(card.Box);
            _cards.Add(card);
        }

        _moreText = new TextBlock
        {
            FontSize = 13,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(CardPadDip),
        };
        Grid moreBox = MakeBox(_moreText, CardRadius);
        moreBox.Visibility = Visibility.Collapsed;
        _strip.Children.Add(moreBox);
        _moreBox = moreBox;

        Root.LayoutUpdated += (_, _) => UpdatePictures();

        _showTimer = DispatcherQueue.CreateTimer();
        _showTimer.Interval = TimeSpan.FromMilliseconds(ShowDelayMs);
        _showTimer.IsRepeating = false;
        _showTimer.Tick += (_, _) =>
        {
            if (_pendingItem is not null)
            {
                Show(_pendingItem.Group?.Key, _pendingButton);
            }
        };

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(HideDelayMs);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            if (!_pointerInside)
            {
                HideFlyout();
            }
        };

        _peekTimer = DispatcherQueue.CreateTimer();
        _peekTimer.Interval = TimeSpan.FromMilliseconds(PeekDelayMs);
        _peekTimer.IsRepeating = false;
        _peekTimer.Tick += (_, _) =>
        {
            if (IsOpen && _peekCandidate != 0)
            {
                StartPeek(_peekCandidate);
            }
        };

        _peekEndTimer = DispatcherQueue.CreateTimer();
        _peekEndTimer.Interval = TimeSpan.FromMilliseconds(PeekEndGraceMs);
        _peekEndTimer.IsRepeating = false;
        _peekEndTimer.Tick += (_, _) => EndPeek(putBack: true);

        // After the timers exist: these use them.
        Root.PointerEntered += (_, _) =>
        {
            _pointerInside = true;
            _hideTimer.Stop();
        };
        Root.PointerExited += (_, _) =>
        {
            _pointerInside = false;
            RestartHideTimer();
        };

        Host.Tasks.Changed += OnTasksChanged;
        Closed += (_, _) =>
        {
            Host.Tasks.Changed -= OnTasksChanged;
            EndPeek(putBack: true);
            ReleasePictures();
        };
    }

    /// <summary>The pointer rests on a dock button (its place on screen in physical pixels).</summary>
    internal void HoverStart(DockItem item, RECT button)
    {
        _hideTimer.Stop();
        if (item.Group is null || item.Group.Windows.Count == 0)
        {
            // Pinned but not running: no preview, and an open one for another app goes.
            _showTimer.Stop();
            _pendingItem = null;
            HideFlyout();
            return;
        }

        _pendingItem = item;
        _pendingButton = button;
        if (IsOpen)
        {
            // Back on the same app's button: keep what is showing. Another app: switch now.
            if (!string.Equals(_groupKey, item.Group.Key, StringComparison.OrdinalIgnoreCase))
            {
                Show(item.Group.Key, button);
            }

            return;
        }

        _showTimer.Stop();
        _showTimer.Start();
    }

    /// <summary>The pointer left the dock button.</summary>
    internal void HoverEnd()
    {
        _showTimer.Stop();
        _pendingItem = null;
        if (IsOpen)
        {
            RestartHideTimer();
        }
    }

    private protected override void OnHiding()
    {
        _showTimer.Stop();
        _hideTimer.Stop();
        _peekTimer.Stop();
        _peekEndTimer.Stop();
        _peekCandidate = 0;
        EndPeek(putBack: true);
        _pointerInside = false;
        _groupKey = null;
        ReleasePictures();
    }

    /// <summary>The pointer rests on a card.</summary>
    private void OnCardEntered(Card card)
    {
        _peekEndTimer.Stop();
        if (card.Window == 0)
        {
            return;
        }

        if (_peekWindow == card.Window)
        {
            return;
        }

        if (_peekWindow != 0)
        {
            // Already peeking: switch to this card's window at once.
            EndPeek(putBack: true);
            StartPeek(card.Window);
            return;
        }

        _peekCandidate = card.Window;
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    /// <summary>The pointer left a card.</summary>
    private void OnCardExited()
    {
        _peekTimer.Stop();
        _peekCandidate = 0;
        if (_peekWindow != 0)
        {
            _peekEndTimer.Stop();
            _peekEndTimer.Start();
        }
    }

    /// <summary>Brings a window to the front for a moment, without taking the keyboard; remembers where it was.</summary>
    private void StartPeek(nint window)
    {
        _peekTimer.Stop();
        _peekCandidate = 0;
        if (!User32.IsWindow(window))
        {
            return;
        }

        if (User32.IsHungAppWindow(window))
        {
            // Its app is not answering; moving it could make Machitan wait too.
            return;
        }

        _peekWasMinimized = User32.IsIconic(window);
        _peekWasBehind = User32.GetWindow(window, User32.GW_HWNDPREV);
        _peekMadeTopmost = (User32.GetWindowLongPtrW(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) == 0;
        _peekWindow = window;

        if (_peekWasMinimized)
        {
            User32.ShowWindow(window, User32.SW_SHOWNOACTIVATE);
        }

        const uint Keep = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER;
        bool moved = User32.SetWindowPos(window, _peekMadeTopmost ? User32.HWND_TOPMOST : User32.HWND_TOP, 0, 0, 0, 0, Keep);

        // The bar and this preview stay above the window being peeked at.
        nint bar = Host.BarHandle;
        if (bar != 0)
        {
            User32.SetWindowPos(bar, User32.HWND_TOPMOST, 0, 0, 0, 0, Keep);
        }

        User32.SetWindowPos(Hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0, Keep);

        if (!_peekLogged)
        {
            _peekLogged = true;
            Log.Info(moved
                ? "Peek: a window was brought forward for the first time this run (" + WindowInfo.GetTitle(window) + ")."
                : "Peek: Windows did not let the window come forward (error " + System.Runtime.InteropServices.Marshal.GetLastSystemError() + ").");
        }
    }

    /// <summary>
    /// Ends a peek. With <paramref name="putBack"/>, the window goes back: minimised again, or
    /// right behind the window that was in front of it. Without, it simply stays (a click).
    /// </summary>
    private void EndPeek(bool putBack)
    {
        _peekEndTimer.Stop();
        nint window = _peekWindow;
        if (window == 0)
        {
            return;
        }

        _peekWindow = 0;
        if (!User32.IsWindow(window))
        {
            return;
        }

        const uint Keep = User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER;
        if (_peekMadeTopmost)
        {
            // Always undone, also after a click: the window must not stay always-on-top.
            // It lands at the front of the normal windows.
            User32.SetWindowPos(window, User32.HWND_NOTOPMOST, 0, 0, 0, 0, Keep);
            _peekMadeTopmost = false;
        }

        if (!putBack)
        {
            return;
        }

        if (_peekWasMinimized)
        {
            User32.ShowWindow(window, User32.SW_SHOWMINNOACTIVE);
            return;
        }

        // Behind the window that was in front of it. When that one is always-on-top (or gone),
        // this window was already the front-most normal window, so there is nothing to undo.
        nint behind = _peekWasBehind;
        if (behind == 0 || !User32.IsWindow(behind)
            || (User32.GetWindowLongPtrW(behind, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0)
        {
            return;
        }

        User32.SetWindowPos(window, behind, 0, 0, 0, 0, Keep);
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>Windows closed, opened or renamed while the preview is open.</summary>
    private void OnTasksChanged()
    {
        if (!IsOpen || _groupKey is null)
        {
            return;
        }

        (TaskGroup? group, _) = Find(_groupKey);
        if (group is null || group.Windows.Count == 0)
        {
            HideFlyout();
            return;
        }

        // Same windows: only the titles may have changed. Otherwise lay the cards out again.
        List<TaskWindow> windows = Ordered(group);
        bool same = true;
        int shown = 0;
        foreach (Card card in _cards)
        {
            if (card.Box.Visibility == Visibility.Visible)
            {
                if (shown >= windows.Count || windows[shown].Handle != card.Window)
                {
                    same = false;
                    break;
                }

                card.Title.Text = windows[shown].Title;
                shown++;
            }
        }

        if (!same || (shown < windows.Count && _moreBox.Visibility != Visibility.Visible))
        {
            Show(_groupKey, _button);
        }
    }

    /// <summary>Fills the cards for one app's windows and places the strip above (or below) the button.</summary>
    private void Show(string? groupKey, RECT button)
    {
        if (groupKey is null)
        {
            return;
        }

        (TaskGroup? group, DockItem? item) = Find(groupKey);
        if (group is null || item is null || group.Windows.Count == 0)
        {
            HideFlyout();
            return;
        }

        _groupKey = groupKey;
        _button = button;
        ReleasePictures();

        List<TaskWindow> windows = Ordered(group);
        double scale = Scale();
        RectInt32 screen = DisplayArea.Primary.OuterBounds;
        double room = (screen.Width / scale) - (2 * ScreenMarginDip) - (2 * PadDip);

        // As wide as possible, down to a minimum; past that, a "+N more" card.
        int count = windows.Count;
        double cardWidth = CardWidthDip;
        double needed = (count * CardWidthDip) + ((count - 1) * CardSpacingDip);
        if (needed > room)
        {
            cardWidth = Math.Max(MinCardWidthDip, (room - ((count - 1) * CardSpacingDip)) / count);
        }

        int shown = Math.Min(count, MaxCards);
        if ((shown * cardWidth) + ((shown - 1) * CardSpacingDip) > room || count > MaxCards)
        {
            int fit = (int)Math.Floor((room - MoreCardWidthDip) / (cardWidth + CardSpacingDip));
            shown = Math.Clamp(fit, 1, MaxCards);
        }

        bool more = shown < count;
        double pictureHeight = Math.Round((cardWidth - (2 * CardPadDip)) * PictureAspect);
        double cardHeight = (2 * CardPadDip) + HeaderHeightDip + PictureGapDip + pictureHeight;
        int iconPixels = IconService.PickSize((int)Math.Ceiling(16 * scale));
        ImageSource? icon = Host.Icons.GetIcon(item, iconPixels);

        for (int i = 0; i < _cards.Count; i++)
        {
            Card card = _cards[i];
            if (i < shown)
            {
                card.Bind(windows[i], item.DisplayName, icon, cardWidth, cardHeight, pictureHeight);
                card.Picture = Register(windows[i].Handle);
                card.Box.Visibility = Visibility.Visible;
            }
            else
            {
                card.Window = 0;
                card.Box.Visibility = Visibility.Collapsed;
            }
        }

        if (more)
        {
            int rest = count - shown;
            _moreText.Text = "+" + rest.ToString(CultureInfo.CurrentCulture) + (rest == 1 ? " more window" : " more windows");
            _moreBox.Width = MoreCardWidthDip;
            _moreBox.Height = cardHeight;
            _moreBox.Visibility = Visibility.Visible;
        }
        else
        {
            _moreBox.Visibility = Visibility.Collapsed;
        }

        double contentWidth = (shown * cardWidth) + ((shown - 1) * CardSpacingDip) + (more ? CardSpacingDip + MoreCardWidthDip : 0);
        int width = (int)Math.Round((contentWidth + (2 * PadDip)) * scale);
        int height = (int)Math.Round((cardHeight + (2 * PadDip)) * scale);
        int pad = (int)Math.Round(PadDip * scale);
        int gap = (int)Math.Round(GapToBarDip * scale);

        RECT bar = Host.BarRect;
        bool top = Host.Settings.Current.DockEdge == DockEdge.Top;
        int x = ((button.Left + button.Right) / 2) - (width / 2);
        int y = top ? bar.Bottom + gap - pad : bar.Top - gap - height + pad;
        int margin = (int)Math.Round(ScreenMarginDip * scale);
        int leftmost = screen.X + margin - pad;
        int rightmost = Math.Max(leftmost, screen.X + screen.Width - width - margin + pad);
        x = Math.Max(leftmost, Math.Min(x, rightmost));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - height));

        _pictureSignature = string.Empty;
        PresentAt(new RectInt32(x, y, width, height));
    }

    /// <summary>The app's task group and its dock item, by group key.</summary>
    private (TaskGroup? Group, DockItem? Item) Find(string groupKey)
    {
        foreach (DockItem item in DockModel.Build(Host.Pins.Pins, Host.Tasks.Groups))
        {
            if (item.Group is not null && string.Equals(item.Group.Key, groupKey, StringComparison.OrdinalIgnoreCase))
            {
                return (item.Group, item);
            }
        }

        return (null, null);
    }

    /// <summary>A steady order (by window handle), so cards do not swap places as windows take turns in front.</summary>
    private static List<TaskWindow> Ordered(TaskGroup group)
    {
        var windows = new List<TaskWindow>(group.Windows);
        windows.Sort((a, b) => a.Handle.CompareTo(b.Handle));
        return windows;
    }

    private nint Register(nint window)
    {
        int hr = Dwmapi.DwmRegisterThumbnail(Hwnd, window, out nint picture);
        return hr == 0 ? picture : 0;
    }

    private void ReleasePictures()
    {
        foreach (Card card in _cards)
        {
            if (card.Picture != 0)
            {
                Dwmapi.DwmUnregisterThumbnail(card.Picture);
                card.Picture = 0;
            }
        }

        _pictureSignature = string.Empty;
    }

    /// <summary>
    /// After each layout: tells Windows where each card's picture goes (physical pixels in this
    /// window), keeping the window's shape, never larger than the window itself.
    /// </summary>
    private void UpdatePictures()
    {
        if (!IsOpen || Root.XamlRoot is null)
        {
            return;
        }

        double scale = Root.XamlRoot.RasterizationScale;
        var signature = new StringBuilder();
        var updates = new List<(nint Picture, RECT Place)>();
        foreach (Card card in _cards)
        {
            if (card.Picture == 0 || card.Box.Visibility != Visibility.Visible || card.Area.ActualWidth <= 0)
            {
                continue;
            }

            Point origin = card.Area.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            double areaLeft = origin.X * scale;
            double areaTop = origin.Y * scale;
            double areaWidth = card.Area.ActualWidth * scale;
            double areaHeight = card.Area.ActualHeight * scale;

            double width = areaWidth;
            double height = areaHeight;
            if (Dwmapi.DwmQueryThumbnailSourceSize(card.Picture, out SIZE source) == 0 && source.cx > 0 && source.cy > 0)
            {
                double fit = Math.Min(1.0, Math.Min(areaWidth / source.cx, areaHeight / source.cy));
                width = source.cx * fit;
                height = source.cy * fit;
            }

            var place = new RECT
            {
                Left = (int)Math.Round(areaLeft + ((areaWidth - width) / 2)),
                Top = (int)Math.Round(areaTop + ((areaHeight - height) / 2)),
            };
            place.Right = place.Left + (int)Math.Round(width);
            place.Bottom = place.Top + (int)Math.Round(height);

            signature.Append(card.Picture).Append(':').Append(place.Left).Append(',').Append(place.Top)
                .Append(',').Append(place.Right).Append(',').Append(place.Bottom).Append(';');
            updates.Add((card.Picture, place));
        }

        string text = signature.ToString();
        if (text == _pictureSignature)
        {
            return;
        }

        _pictureSignature = text;
        foreach ((nint picture, RECT place) in updates)
        {
            var properties = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Dwmapi.DWM_TNP_RECTDESTINATION | Dwmapi.DWM_TNP_VISIBLE | Dwmapi.DWM_TNP_OPACITY,
                rcDestination = place,
                opacity = 255,
                fVisible = 1,
                fSourceClientAreaOnly = 0,
            };
            Dwmapi.DwmUpdateThumbnailProperties(picture, ref properties);
        }
    }

    private void SwitchTo(nint window)
    {
        // The window being peeked at stays in front for real; any other goes back first.
        EndPeek(putBack: _peekWindow != window);
        HideFlyout();
        WindowActivator.BringToFront(window);
    }

    private static bool IsWithin(object? source, DependencyObject ancestor)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>One window's card: header (icon, app name, title, close) over the picture area.</summary>
    private sealed class Card
    {
        private readonly PreviewWindow _owner;
        private readonly Grid _surface;
        private readonly Image _icon;
        private readonly TextBlock _app;
        private readonly Button _close;

        internal Card(PreviewWindow owner)
        {
            _owner = owner;

            _icon = new Image
            {
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _app = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Title = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.75,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(_app);
            names.Children.Add(Title);

            _close = new Button
            {
                Content = MakeGlyph(CloseGlyph, 10),
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(_close, "Close window");
            ToolTipService.SetToolTip(_close, "Close window");
            _close.Click += (_, _) =>
            {
                if (Window != 0)
                {
                    // Closing what is being peeked at: nothing to put back.
                    _owner.EndPeek(putBack: _owner._peekWindow != Window);
                    User32.PostMessageW(Window, User32.WM_CLOSE, 0, 0);
                }
            };

            var header = new Grid { Height = HeaderHeightDip, ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_icon);
            Grid.SetColumn(names, 1);
            header.Children.Add(names);
            Grid.SetColumn(_close, 2);
            header.Children.Add(_close);

            // Windows draws the live picture over this area.
            Area = new Border
            {
                Margin = new Thickness(0, PictureGapDip, 0, 0),
                CornerRadius = new CornerRadius(6),
            };

            var body = new Grid { Padding = new Thickness(CardPadDip) };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(header);
            Grid.SetRow(Area, 1);
            body.Children.Add(Area);

            // A light wash on hover, under the content.
            _surface = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            _surface.Children.Add(body);
            _surface.PointerEntered += (_, _) =>
            {
                _surface.Background = new SolidColorBrush(
                    _owner.IsDark ? ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00));
                _owner.OnCardEntered(this);
            };
            _surface.PointerExited += (_, _) =>
            {
                _surface.Background = new SolidColorBrush(Colors.Transparent);
                _owner.OnCardExited();
            };
            _surface.Tapped += OnTapped;

            Content = _surface;
        }

        /// <summary>The card's content, put in a rounded box by the window.</summary>
        internal UIElement Content { get; }

        internal Grid Box { get; set; } = new();

        internal Border Area { get; }

        internal TextBlock Title { get; }

        internal nint Window { get; set; }

        internal nint Picture { get; set; }

        internal void Bind(TaskWindow window, string appName, ImageSource? icon, double width, double height, double pictureHeight)
        {
            Window = window.Handle;
            _app.Text = appName;
            Title.Text = window.Title;
            Title.Visibility = string.Equals(window.Title, appName, StringComparison.Ordinal) || window.Title.Length == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            _icon.Source = icon;
            Area.Height = pictureHeight;
            Box.Width = width;
            Box.Height = height;
            _surface.Background = new SolidColorBrush(Colors.Transparent);
            AutomationProperties.SetName(_surface, appName + ", " + window.Title);
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            if (Window == 0 || IsWithin(e.OriginalSource, _close))
            {
                return;
            }

            _owner.SwitchTo(Window);
        }
    }
}
