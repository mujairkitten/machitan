using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Machitan.Bar;

/// <summary>
/// Dragging on the bar, at any time.
///
/// - Start moves its whole bubble (in Joined and Classic: the Start part inside the bar). An
///   app bubble (the apps, or a group) moves by its empty space (Segmented, where each bubble
///   is a pill of its own). Dropped between other bubbles: the order changes. Dropped near a
///   screen edge: the bar lines up with that edge. Dropped in open space away from both: the
///   bar is centred. Classic is full width already: there, Start goes to the left end or the
///   right end.
/// - The search icon moves between the Start bubble and the start or end of the apps.
/// - Any app, pinned or not, moves to another place in its bubble or into another app bubble.
///
/// While dragging, the piece follows the pointer, the bubble it would go into is outlined, and
/// an accent line marks the place (drawn by <see cref="BarDragLayer"/>). Esc, or letting go
/// far above the bar, puts it back. Results are saved: the bubbles' order and the apps'
/// places in bar-layout.json, the alignment and search place in the settings.
/// </summary>
public sealed partial class BarWindow
{
    private const double DragStartDip = 6;
    private const double EdgeZoneRatio = 1.0 / 6;
    private const double LiftDip = 48;
    private const double GroupSlackDip = 24;
    private const double DimmedOpacity = 0.35;
    private const int DragTickMs = 16;

    private DragPress? _press;
    private DragState? _drag;
    private DispatcherQueueTimer? _dragTimer;
    private BarDragLayer? _dragLayer;
    private bool _dockSyncPending;

    private enum DragKind
    {
        StartGroup,
        Bubble,
        Search,
        App,
        TrayIcon,
    }

    private enum DropKind
    {
        Order,
        Search,
        Place,
        Tray,

        /// <summary>A tray icon on the bar dropped on the ^ button: back into the tray box.</summary>
        TrayBox,
    }

    /// <summary>The drag layer, for the shell to close on shutdown.</summary>
    internal BarDragLayer? DragLayer => _dragLayer;

    /// <summary>A press that becomes a drag once the pointer moves far enough.</summary>
    private sealed record DragPress(DragKind Kind, FrameworkElement Source, uint PointerId, POINT At, bool Mouse, DockItem? Item, string? BubbleId, TrayIcon? Tray);

    /// <summary>
    /// Where a drop would go, and how to show it. Order: <see cref="Id"/> is the moved bubble,
    /// going before <see cref="BeforeId"/> (or after <see cref="AfterId"/>). Place: the app goes
    /// into bubble <see cref="Id"/> at <see cref="Index"/>.
    /// </summary>
    private sealed record DropTarget(
        DropKind Kind,
        string Id,
        int Index,
        string? BeforeId,
        string? AfterId,
        DockAlignment Alignment,
        SearchPlace Place,
        int MarkerX,
        RECT? Outline,
        double OutlineRadius);

    /// <summary>A bubble on screen when the drag started: its outline and corner radius.</summary>
    private sealed record SegmentSnap(string Id, RECT Outline, double Radius);

    /// <summary>An app bubble on screen when the drag started: its row, outline and app buttons, from the left.</summary>
    private sealed record BubbleSnap(string Id, RECT Row, RECT Outline, double Radius, List<(DockItem Item, RECT Rect)> Apps);

    /// <summary>A running drag, with where things were on screen when it started (physical pixels).</summary>
    private sealed class DragState
    {
        internal DragState(DragKind kind, FrameworkElement piece, bool mouse, DockItem? item, string movedId)
        {
            Kind = kind;
            Piece = piece;
            Mouse = mouse;
            Item = item;
            MovedId = movedId;
        }

        internal DragKind Kind { get; }

        /// <summary>What is shown faded while it is away.</summary>
        internal FrameworkElement Piece { get; }

        internal bool Mouse { get; }

        internal DockItem? Item { get; }

        /// <summary>The bubble being moved (Start or an app bubble), or the bubble the app or search comes from.</summary>
        internal string MovedId { get; }

        internal double PieceOpacity { get; set; } = 1;

        internal RECT Ghost { get; set; }

        internal int GrabX { get; set; }

        internal int GrabY { get; set; }

        internal RECT Band { get; set; }

        /// <summary>The Start bubble, when it is on the bar.</summary>
        internal RECT? StartOutline { get; set; }

        internal double StartRadius { get; set; }

        internal RECT? StartButton { get; set; }

        internal RECT? Desktop { get; set; }

        /// <summary>The bubbles shown, from the left, Start included.</summary>
        internal List<SegmentSnap> Segments { get; } = new();

        /// <summary>The app bubbles shown, from the left.</summary>
        internal List<BubbleSnap> Bubbles { get; } = new();

        /// <summary>The tray bubble's row and outline, and its icons from the left, when it is on the bar.</summary>
        internal RECT? TrayRow { get; set; }

        internal RECT TrayOutline { get; set; }

        internal double TrayRadius { get; set; }

        internal List<(TrayIcon Icon, RECT Rect)> TrayTiles { get; } = new();

        /// <summary>The dragged tray icon (a tray drag).</summary>
        internal TrayIcon? TrayIcon { get; set; }

        /// <summary>The tray icon comes from the tray box (it was not on the bar).</summary>
        internal bool FromBox { get; set; }

        /// <summary>The tray bubble's ^ button, when it is on the bar.</summary>
        internal RECT? TrayChevron { get; set; }

        internal DropTarget? Target { get; set; }
    }

    /// <summary>Called once from Start: the layer window and the bar-wide drag events.</summary>
    private void PrepareDragging()
    {
        try
        {
            _dragLayer = new BarDragLayer();
        }
        catch (Exception ex)
        {
            // Dragging still works, without the picture, outline and marker.
            Log.Error("Bar drag: the drag layer could not be made; drags will show no piece or marker.", ex);
            _dragLayer = null;
        }

        Root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnRootDragReleased), true);
        Root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnRootDragCaptureLost), true);
    }

    /// <summary>
    /// Lets <paramref name="source"/> start a drag. Buttons handle presses themselves, so their
    /// presses are heard even when handled; a bubble's own surface only hears presses that no
    /// button took (its empty space). A bubble's pill carries its ID in its Tag.
    /// </summary>
    private void AttachDrag(FrameworkElement source, DragKind kind)
    {
        bool button = source is ButtonBase;
        source.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => OnDragPressed(kind, source, e)), button);
        source.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => OnDragPointerMoved(source, e)), true);
        source.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => OnDragPressEnded(source, e, released: true)), true);
        source.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) => OnDragPressEnded(source, e, released: false)), true);
    }

    /// <summary>Stops a drag without changing anything (shutdown, the bar being built again).</summary>
    internal void CancelDrag()
    {
        _press = null;
        EndDrag(apply: false);
    }

    private bool IsDragging => _drag is not null;

    // ---------------------------------------------------------------- press, then drag

    private void OnDragPressed(DragKind kind, FrameworkElement source, PointerRoutedEventArgs e)
    {
        try
        {
            if (_drag is not null || _closing || IsAutoHidden)
            {
                return;
            }

            if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed || !User32.GetCursorPos(out POINT at))
            {
                // Right-click menus and pen buttons stay as they are.
                return;
            }

            DockItem? item = source is Button { Tag: DockButtonParts parts } ? parts.Item : null;
            if (kind == DragKind.App && item is null)
            {
                return;
            }

            string? bubbleId = kind == DragKind.Bubble ? source.Tag as string : null;
            if (kind == DragKind.Bubble && bubbleId is null)
            {
                return;
            }

            TrayIcon? tray = source is Button { Tag: TrayTileTag trayTag } ? trayTag.Icon : null;
            if (kind == DragKind.TrayIcon && tray is null)
            {
                return;
            }

            _press = new DragPress(
                kind,
                source,
                e.Pointer.PointerId,
                at,
                e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse,
                item,
                bubbleId,
                tray);
            if (source is not ButtonBase)
            {
                // A bubble's empty space: keep the pointer so the moves keep coming here.
                source.CapturePointer(e.Pointer);
            }
        }
        catch (Exception ex)
        {
            _press = null;
            Log.Error("Bar drag: a press could not be noted.", ex);
        }
    }

    private void OnDragPointerMoved(FrameworkElement source, PointerRoutedEventArgs e)
    {
        if (_press is not { } press
            || _drag is not null
            || !ReferenceEquals(press.Source, source)
            || press.PointerId != e.Pointer.PointerId
            || !User32.GetCursorPos(out POINT now))
        {
            return;
        }

        double limit = DragStartDip * Scale();
        if (Math.Abs(now.X - press.At.X) < limit && Math.Abs(now.Y - press.At.Y) < limit)
        {
            return;
        }

        _press = null;
        BeginDrag(press, e.Pointer);
    }

    private void OnDragPressEnded(FrameworkElement source, PointerRoutedEventArgs e, bool released)
    {
        if (_press is not { } press || !ReferenceEquals(press.Source, source) || press.PointerId != e.Pointer.PointerId)
        {
            return;
        }

        // Let go before moving far: an ordinary click (buttons) or nothing (empty space).
        _press = null;
        if (released && source is not ButtonBase)
        {
            source.ReleasePointerCapture(e.Pointer);
        }
    }

    private void BeginDrag(DragPress press, Pointer pointer)
    {
        try
        {
            if (MakeDrag(press) is not { } start)
            {
                return;
            }

            DragState drag = start.Drag;
            _drag = drag;
            _host.EndDockHover();
            _host.HidePreview();

            // The bar takes the pointer from the button, so the button does not click on release.
            if (!Root.CapturePointer(pointer))
            {
                Log.Warn("Bar drag: the bar could not keep the pointer; the drag ends when the mouse button is let go.");
            }

            if (_dragLayer is BarDragLayer layer)
            {
                ShellSettings settings = _host.Settings.Current;
                double scale = Scale();

                // A plain fill at no less than 85%, so the copy reads clearly over anything.
                ShellSettings look = settings with { EnableAcrylic = false, Opacity = Math.Max(settings.Opacity, 0.85) };
                layer.ShowBand(drag.Band, scale);
                layer.SetGhost(SurfaceFill.Create(look, IsDarkTheme(), start.RadiusDip), drag.Ghost.Width / scale, drag.Ghost.Height / scale);
                _ = PictureGhostAsync(drag, start.Picture, start.PictureDip);
            }
            else
            {
                drag.Piece.Opacity = DimmedOpacity;
            }

            _dragTimer ??= MakeDragTimer();
            _dragTimer.Start();
            UpdateDrag();

            Log.Info("Bar drag: " + DescribeDrag(drag) + " picked up.");
        }
        catch (Exception ex)
        {
            Log.Error("Bar drag: the drag could not start.", ex);
            EndDrag(apply: false);
        }
    }

    /// <summary>
    /// An icon dragged out of the tray box (the box has closed): the drag goes on here, read
    /// from the mouse as every bar drag is, and the icon can land among the bar's tray icons.
    /// The copy under the pointer shows the icon's own picture.
    /// </summary>
    internal void BeginTrayDragFromBox(TrayIcon icon)
    {
        try
        {
            if (_drag is not null || _closing || IsAutoHidden || _host.Custom.IsOn
                || _trayChevron is not Button chevron || !IsOnBar(chevron)
                || !User32.GetCursorPos(out POINT at) || !IsMouseButtonDown())
            {
                return;
            }

            var press = new DragPress(DragKind.TrayIcon, chevron, 0, at, true, null, null, icon);
            if (MakeDrag(press) is not { } start)
            {
                return;
            }

            DragState drag = start.Drag;
            drag.FromBox = true;
            drag.GrabX = drag.Ghost.Width / 2;
            drag.GrabY = drag.Ghost.Height / 2;
            _drag = drag;
            _host.EndDockHover();
            _host.HidePreview();

            if (_dragLayer is BarDragLayer layer)
            {
                ShellSettings settings = _host.Settings.Current;
                double scale = Scale();
                ShellSettings look = settings with { EnableAcrylic = false, Opacity = Math.Max(settings.Opacity, 0.85) };
                double widthDip = drag.Ghost.Width / scale;
                double heightDip = drag.Ghost.Height / scale;
                layer.ShowBand(drag.Band, scale);
                layer.SetGhost(SurfaceFill.Create(look, IsDarkTheme(), start.RadiusDip), widthDip, heightDip);
                if (icon.Picture is IconPixels pixels)
                {
                    layer.SetGhostImage(Machitan.Start.StartMenuWindow.ToBitmap(pixels), (widthDip - 16) / 2, (heightDip - 16) / 2, 16, 16);
                }
            }

            _dragTimer ??= MakeDragTimer();
            _dragTimer.Start();
            UpdateDrag();
            Log.Info("Bar drag: a tray icon picked up from the tray box.");
        }
        catch (Exception ex)
        {
            Log.Error("Bar drag: the icon from the tray box could not be dragged.", ex);
            EndDrag(apply: false);
        }
    }

    private DispatcherQueueTimer MakeDragTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(DragTickMs);
        timer.Tick += (_, _) => OnDragTick();
        return timer;
    }

    /// <summary>
    /// Takes stock when a drag starts: the piece and its picture, and where the bubbles, Start,
    /// the app buttons and Show Desktop are on screen. The bar does not change under the
    /// pointer while the drag runs (dock updates wait), so these stay true.
    /// </summary>
    private (DragState Drag, UIElement Picture, Windows.Foundation.Rect PictureDip, double RadiusDip)? MakeDrag(DragPress press)
    {
        ShellSettings settings = _host.Settings.Current;
        TaskbarMode mode = EffectiveMode(settings);
        double scale = Scale();
        double pillRadius = PillHeightDip() / 2;
        bool segmented = mode == TaskbarMode.Segmented;

        // Start may be off the bar (custom mode can remove it).
        FrameworkElement? startGroup = _startGroup is FrameworkElement builtGroup && IsOnBar(builtGroup) ? builtGroup : null;
        Button? startButton = startGroup is not null && _startButton is Button builtStart && IsOnBar(builtStart) ? builtStart : null;
        if (press.Kind == DragKind.StartGroup && (startGroup is null || startButton is null))
        {
            return null;
        }

        // The piece, what its picture is taken from, and the copy's outline.
        FrameworkElement piece;
        RECT ghost;
        UIElement picture;
        Windows.Foundation.Rect pictureDip;
        double radiusDip;
        string movedId;
        switch (press.Kind)
        {
            case DragKind.StartGroup when segmented && _startSegment is Grid startPill:
                piece = startPill;
                ghost = ScreenRectOf(startPill);
                picture = startPill.Children[startPill.Children.Count - 1];
                pictureDip = new Windows.Foundation.Rect(0, 0, startPill.ActualWidth, startPill.ActualHeight);
                radiusDip = pillRadius;
                movedId = BarLayoutStore.StartId;
                break;

            case DragKind.StartGroup when startGroup is not null:
            {
                // Inside one bar: the copy is a small pill around the Start part.
                piece = startGroup;
                RECT row = ScreenRectOf(startGroup);
                int side = (int)Math.Round(SegmentPaddingDip * scale);
                ghost = new RECT { Left = row.Left - side, Top = _barRect.Top, Right = row.Right + side, Bottom = _barRect.Bottom };
                picture = startGroup;
                pictureDip = new Windows.Foundation.Rect(
                    SegmentPaddingDip,
                    (row.Top - _barRect.Top) / scale,
                    startGroup.ActualWidth,
                    startGroup.ActualHeight);
                radiusDip = mode == TaskbarMode.Classic ? ClassicRadiusDip : pillRadius;
                movedId = BarLayoutStore.StartId;
                break;
            }

            case DragKind.Bubble when segmented && press.Source is Grid bubblePill && IsOnBar(bubblePill) && press.BubbleId is string bubbleId:
                piece = bubblePill;
                ghost = ScreenRectOf(bubblePill);
                picture = bubblePill.Children[bubblePill.Children.Count - 1];
                pictureDip = new Windows.Foundation.Rect(0, 0, bubblePill.ActualWidth, bubblePill.ActualHeight);
                radiusDip = pillRadius;
                movedId = bubbleId;
                break;

            case DragKind.Search:
            case DragKind.App:
            case DragKind.TrayIcon:
                if (press.Source is not Button button || !IsOnBar(button) || button.Content is not FrameworkElement content)
                {
                    return null;
                }

                piece = button;
                ghost = ScreenRectOf(button);
                picture = content;
                pictureDip = new Windows.Foundation.Rect(
                    (button.ActualWidth - content.ActualWidth) / 2,
                    (button.ActualHeight - content.ActualHeight) / 2,
                    content.ActualWidth,
                    content.ActualHeight);
                radiusDip = button.CornerRadius.TopLeft;
                movedId = press.Kind == DragKind.TrayIcon ? BarLayoutStore.TrayId
                    : press.Item is DockItem dragged ? _host.BarLayout.BubbleOf(dragged)
                    : BarLayoutStore.StartId;
                break;

            default:
                return null;
        }

        var drag = new DragState(press.Kind, piece, press.Mouse, press.Item, movedId)
        {
            PieceOpacity = piece.Opacity,
            Ghost = ghost,
            GrabX = press.At.X - ghost.Left,
            GrabY = press.At.Y - ghost.Top,
            Band = DragBand(settings, scale),
            StartButton = startButton is null ? null : ScreenRectOf(startButton),
            TrayIcon = press.Tray,
        };

        // The bubbles: their own pills in Segmented; the parts inside the one bar otherwise.
        int inset = (int)Math.Round(4 * scale);
        if (startGroup is null)
        {
            drag.StartOutline = null;
        }
        else if (segmented && _startSegment is not null && IsOnBar(_startSegment))
        {
            drag.StartOutline = ScreenRectOf(_startSegment);
            drag.StartRadius = pillRadius;
        }
        else
        {
            drag.StartOutline = Grow(ScreenRectOf(startGroup), inset);
            drag.StartRadius = ButtonRadius(startGroup.ActualHeight, startGroup.ActualWidth);
        }

        foreach (string id in _builtIds)
        {
            if (id == BarLayoutStore.StartId)
            {
                if (drag.StartOutline is RECT startOutline)
                {
                    drag.Segments.Add(new SegmentSnap(id, startOutline, drag.StartRadius));
                }

                continue;
            }

            if (id == BarLayoutStore.TrayId)
            {
                if (_trayRow is StackPanel trayRow && IsOnBar(trayRow))
                {
                    RECT trayRect = ScreenRectOf(trayRow);
                    bool ownPill = segmented && _traySegment is Grid trayPill && IsOnBar(trayPill);
                    drag.TrayRow = trayRect;
                    drag.TrayOutline = ownPill ? ScreenRectOf(_traySegment!) : Grow(trayRect, inset);
                    drag.TrayRadius = ownPill ? pillRadius : ButtonRadius(trayRow.ActualHeight, trayRow.ActualWidth);
                    foreach (UIElement child in trayRow.Children)
                    {
                        if (child is Button { Tag: TrayTileTag tileTag } trayButton && IsOnBar(trayButton))
                        {
                            drag.TrayTiles.Add((tileTag.Icon, ScreenRectOf(trayButton)));
                        }
                    }

                    if (_trayChevron is Button chevron && IsOnBar(chevron))
                    {
                        drag.TrayChevron = ScreenRectOf(chevron);
                    }

                    drag.Segments.Add(new SegmentSnap(id, drag.TrayOutline, drag.TrayRadius));
                }

                continue;
            }

            if (BubbleById(id) is not AppBubble bubble || !IsOnBar(bubble.Row))
            {
                continue;
            }

            RECT row = ScreenRectOf(bubble.Row);
            RECT outline;
            double radius;
            if (segmented && bubble.Pill is Grid pill && IsOnBar(pill))
            {
                outline = ScreenRectOf(pill);
                radius = pillRadius;
            }
            else
            {
                outline = Grow(row, inset);
                radius = ButtonRadius(bubble.Row.ActualHeight, bubble.Row.ActualWidth);
            }

            var apps = new List<(DockItem Item, RECT Rect)>();
            foreach (UIElement child in bubble.Panel.Children)
            {
                if (child is Button { Tag: DockButtonParts parts } appButton && IsOnBar(appButton))
                {
                    apps.Add((parts.Item, ScreenRectOf(appButton)));
                }
            }

            drag.Segments.Add(new SegmentSnap(id, outline, radius));
            drag.Bubbles.Add(new BubbleSnap(id, row, outline, radius, apps));
        }

        if (_desktopButton is not null && IsOnBar(_desktopButton))
        {
            drag.Desktop = ScreenRectOf(_desktopButton);
        }

        return (drag, picture, pictureDip, radiusDip);
    }

    /// <summary>The band the drag layer covers: the bar's row the whole screen wide, with room to lift the piece.</summary>
    private RECT DragBand(ShellSettings settings, double scale)
    {
        int pad = (int)Math.Round(WindowPadDip * scale);
        int lift = (int)Math.Round(LiftDip * scale);
        bool top = settings.DockEdge == DockEdge.Top;
        return new RECT
        {
            Left = _monitor.Left,
            Right = _monitor.Right,
            Top = top ? _barRect.Top - pad : _barRect.Top - pad - lift,
            Bottom = top ? _barRect.Bottom + pad + lift : _barRect.Bottom + pad,
        };
    }

    /// <summary>
    /// The piece's picture: taken from the bar, then copied into a plain bitmap the layer
    /// window can show. Only then is the piece faded on the bar (the picture must not be faded).
    /// </summary>
    private async Task PictureGhostAsync(DragState drag, UIElement picture, Windows.Foundation.Rect pictureDip)
    {
        try
        {
            var rendered = new RenderTargetBitmap();
            await rendered.RenderAsync(picture);
            if (!ReferenceEquals(_drag, drag) || rendered.PixelWidth <= 0 || rendered.PixelHeight <= 0)
            {
                return;
            }

            byte[] pixels = (await rendered.GetPixelsAsync()).ToArray();
            if (!ReferenceEquals(_drag, drag))
            {
                return;
            }

            var bitmap = new WriteableBitmap(rendered.PixelWidth, rendered.PixelHeight);
            using (Stream stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(pixels, 0, pixels.Length);
            }

            bitmap.Invalidate();
            _dragLayer?.SetGhostImage(bitmap, pictureDip.X, pictureDip.Y, pictureDip.Width, pictureDip.Height);
        }
        catch (Exception ex)
        {
            Log.Warn("Bar drag: the dragged piece could not be pictured; the drag goes on without its picture.", ex);
        }
        finally
        {
            if (ReferenceEquals(_drag, drag))
            {
                drag.Piece.Opacity = DimmedOpacity;
            }
        }
    }

    // ---------------------------------------------------------------- while dragging

    private void OnDragTick()
    {
        if (_drag is not { } drag)
        {
            _dragTimer?.Stop();
            _dragLayer?.HideLayer();
            return;
        }

        try
        {
            if ((User32.GetAsyncKeyState(User32.VK_ESCAPE) & 0x8000) != 0)
            {
                Log.Info("Bar drag: Esc; put back.");
                EndDrag(apply: false);
                return;
            }

            if (drag.Mouse && !IsMouseButtonDown())
            {
                EndDrag(apply: true);
                return;
            }

            UpdateDrag();
        }
        catch (Exception ex)
        {
            Log.Error("Bar drag: the drag failed; put back.", ex);
            EndDrag(apply: false);
        }
    }

    /// <summary>The primary mouse button, read from the hardware (with swapped buttons, the right one).</summary>
    private static bool IsMouseButtonDown()
    {
        int key = User32.GetSystemMetrics(User32.SM_SWAPBUTTON) != 0 ? User32.VK_RBUTTON : User32.VK_LBUTTON;
        return (User32.GetAsyncKeyState(key) & 0x8000) != 0;
    }

    private void UpdateDrag()
    {
        if (_drag is not { } drag || !User32.GetCursorPos(out POINT cursor))
        {
            return;
        }

        DropTarget? target = FindTarget(drag, cursor);

        if (_dragLayer is BarDragLayer layer)
        {
            // The piece follows the pointer inside the band, at least half of it on screen.
            RECT band = drag.Band;
            int width = drag.Ghost.Width;
            int height = drag.Ghost.Height;
            int left = Math.Clamp(cursor.X - drag.GrabX, band.Left - (width / 2), band.Right - (width / 2));
            int top = Math.Clamp(cursor.Y - drag.GrabY, band.Top, Math.Max(band.Top, band.Bottom - height));
            layer.MoveGhost(left, top, target is not null);

            if (target != drag.Target)
            {
                ShowTarget(layer, target);
            }
        }

        drag.Target = target;
    }

    private void ShowTarget(BarDragLayer layer, DropTarget? target)
    {
        if (target is null)
        {
            layer.HideOutline();
            layer.HideMarker();
            return;
        }

        if (target.Outline is RECT outline)
        {
            Windows.UI.Color ink = IsDarkTheme()
                ? ColorHelper.FromArgb(200, 255, 255, 255)
                : ColorHelper.FromArgb(170, 0, 0, 0);
            layer.ShowOutline(outline, target.OutlineRadius, ink);
        }
        else
        {
            layer.HideOutline();
        }

        if (target.Kind == DropKind.TrayBox)
        {
            layer.HideMarker();
            return;
        }

        layer.ShowMarker(target.MarkerX, _barRect, ReadAccentColor());
    }

    /// <summary>Where the piece would go if let go now, or null (it would go back).</summary>
    private DropTarget? FindTarget(DragState drag, POINT cursor)
    {
        // Far above (or below) the bar: nowhere. Letting go there puts the piece back.
        int distance = cursor.Y < _barRect.Top ? _barRect.Top - cursor.Y
            : cursor.Y > _barRect.Bottom ? cursor.Y - _barRect.Bottom
            : 0;
        if (distance > _barRect.Height)
        {
            return null;
        }

        return drag.Kind switch
        {
            DragKind.StartGroup or DragKind.Bubble => BubbleTarget(drag, cursor.X),
            DragKind.Search => SearchTarget(drag, cursor.X),
            DragKind.App => AppTarget(drag, cursor.X),
            DragKind.TrayIcon => TrayTarget(drag, cursor.X),
            _ => null,
        };
    }

    /// <summary>A whole bubble: between the other bubbles, at a screen edge, or centred.</summary>
    private DropTarget BubbleTarget(DragState drag, int x)
    {
        ShellSettings settings = _host.Settings.Current;
        TaskbarMode mode = EffectiveMode(settings);
        double scale = Scale();
        DockAlignment current = settings.DockAlignment;

        var others = new List<SegmentSnap>();
        foreach (SegmentSnap segment in drag.Segments)
        {
            if (!string.Equals(segment.Id, drag.MovedId, StringComparison.OrdinalIgnoreCase))
            {
                others.Add(segment);
            }
        }

        if (mode == TaskbarMode.Classic)
        {
            // Full width already: Start at the left end, or at the right end before Show Desktop.
            bool leftEnd = x < (_barRect.Left + _barRect.Right) / 2 || others.Count == 0;
            int end = (int)Math.Round(SegmentPaddingDip / 2 * scale);
            int marker = leftEnd ? _barRect.Left + end
                : drag.Desktop is RECT desktop ? desktop.Left - end
                : _barRect.Right - end;
            return new DropTarget(
                DropKind.Order,
                drag.MovedId,
                leftEnd ? 0 : others.Count,
                leftEnd && others.Count > 0 ? others[0].Id : null,
                leftEnd || others.Count == 0 ? null : others[^1].Id,
                current,
                settings.SearchPlace,
                marker,
                _barRect,
                ClassicRadiusDip);
        }

        // The place among the others: before the first whose middle is right of the pointer.
        int slot;
        DockAlignment alignment;
        int zone = (int)Math.Round(_monitor.Width * EdgeZoneRatio);
        if (x < _monitor.Left + zone)
        {
            alignment = DockAlignment.Left;
            slot = 0;
        }
        else if (x > _monitor.Right - zone)
        {
            alignment = DockAlignment.Right;
            slot = others.Count;
        }
        else
        {
            slot = 0;
            while (slot < others.Count && x > (others[slot].Outline.Left + others[slot].Outline.Right) / 2)
            {
                slot++;
            }

            int slack = (int)Math.Round(GroupSlackDip * scale);
            bool overBar = x >= _barRect.Left - slack && x <= _barRect.Right + slack;
            alignment = overBar ? current : DockAlignment.Center;
        }

        string? before = slot < others.Count ? others[slot].Id : null;
        string? after = slot >= others.Count && others.Count > 0 ? others[^1].Id : null;

        if (alignment == current && others.Count > 0)
        {
            // The bar stays where it is: the line marks the place between the bubbles, and the
            // bubble beside it is outlined.
            int half = (int)Math.Round((mode == TaskbarMode.Joined ? 6.5 : SegmentGapDip / 2) * scale);
            SegmentSnap beside = slot < others.Count ? others[slot] : others[^1];
            int marker = slot < others.Count ? beside.Outline.Left - half : beside.Outline.Right + half;
            return new DropTarget(DropKind.Order, drag.MovedId, slot, before, after, alignment, settings.SearchPlace, marker, beside.Outline, beside.Radius);
        }

        // The bar moves along the edge: the line marks where it will line up.
        int margin = (int)Math.Round(EdgeMarginDip * scale);
        int lineUp = alignment switch
        {
            DockAlignment.Left => _monitor.Left + margin,
            DockAlignment.Right => _monitor.Right - margin,
            _ => _monitor.Left + (_monitor.Width / 2),
        };
        return new DropTarget(DropKind.Order, drag.MovedId, slot, before, after, alignment, settings.SearchPlace, lineUp, null, 0);
    }

    /// <summary>The search icon: with Start, or at the start or end of the apps; the nearest place in reach.</summary>
    private DropTarget? SearchTarget(DragState drag, int x)
    {
        ShellSettings settings = _host.Settings.Current;
        double scale = Scale();
        int half = Math.Max((int)Math.Round(ItemSpacingDip / 2 * scale), 1);

        var places = new List<(SearchPlace Place, int X, RECT Outline, double Radius)>();
        if (drag.StartButton is RECT startButton && drag.StartOutline is RECT startOutline)
        {
            places.Add((SearchPlace.WithStart, startButton.Right + half, startOutline, drag.StartRadius));
        }

        if (BubbleSnapOf(drag, BarLayoutStore.AppsId) is BubbleSnap apps)
        {
            places.Add((SearchPlace.BeforeApps, apps.Row.Left - half, apps.Outline, apps.Radius));
            places.Add((SearchPlace.AfterApps, apps.Row.Right + half, apps.Outline, apps.Radius));
        }

        int reach = (int)Math.Round(2.5 * ButtonWidthDip() * scale);
        (SearchPlace Place, int X, RECT Outline, double Radius)? best = null;
        int bestDistance = int.MaxValue;
        foreach ((SearchPlace Place, int X, RECT Outline, double Radius) place in places)
        {
            int distance = Math.Abs(x - place.X);
            if (distance <= reach && distance < bestDistance)
            {
                best = place;
                bestDistance = distance;
            }
        }

        return best is { } chosen
            ? new DropTarget(DropKind.Search, BarLayoutStore.AppsId, -1, null, null, settings.DockAlignment, chosen.Place, chosen.X, chosen.Outline, chosen.Radius)
            : null;
    }

    /// <summary>
    /// An app: into the app bubble under the pointer (or the nearest one in reach), before any
    /// of its apps or after the last.
    /// </summary>
    private DropTarget? AppTarget(DragState drag, int x)
    {
        if (drag.Item is not DockItem dragged)
        {
            return null;
        }

        ShellSettings settings = _host.Settings.Current;
        double scale = Scale();
        int reach = (int)Math.Round(ButtonWidthDip() * scale);

        BubbleSnap? over = null;
        int overDistance = int.MaxValue;
        foreach (BubbleSnap bubble in drag.Bubbles)
        {
            int distance = x < bubble.Row.Left ? bubble.Row.Left - x : x > bubble.Row.Right ? x - bubble.Row.Right : 0;
            if (distance <= reach && distance < overDistance)
            {
                over = bubble;
                overDistance = distance;
            }
        }

        if (over is null)
        {
            return null;
        }

        int half = Math.Max((int)Math.Round(ItemSpacingDip / 2 * scale), 1);
        List<(DockItem Item, RECT Rect)> apps = over.Apps;
        int count = apps.Count;
        int bestSlot = 0;
        int bestX = 0;
        int bestDistance = int.MaxValue;
        for (int slot = 0; slot <= count; slot++)
        {
            int slotX = slot < count ? apps[slot].Rect.Left - half
                : count > 0 ? apps[count - 1].Rect.Right + half
                : over.Row.Left;
            int distance = Math.Abs(x - slotX);
            if (distance < bestDistance)
            {
                bestSlot = slot;
                bestX = slotX;
                bestDistance = distance;
            }
        }

        // Counted without the dragged app itself (it leaves its own place first).
        int index = 0;
        for (int i = 0; i < bestSlot; i++)
        {
            if (!string.Equals(apps[i].Item.Key, dragged.Key, StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }
        }

        return new DropTarget(DropKind.Place, over.Id, index, null, null, settings.DockAlignment, settings.SearchPlace, bestX, over.Outline, over.Radius);
    }

    /// <summary>A tray icon: before any icon of the tray bubble, or after the last; only over the tray.</summary>
    private DropTarget? TrayTarget(DragState drag, int x)
    {
        if (drag.TrayIcon is not TrayIcon dragged || drag.TrayRow is not RECT row)
        {
            return null;
        }

        ShellSettings settings = _host.Settings.Current;
        double scale = Scale();
        int reach = (int)Math.Round(ButtonWidthDip() * scale);
        if (x < row.Left - reach || x > row.Right + reach)
        {
            return null;
        }

        int half = Math.Max((int)Math.Round(ItemSpacingDip / 2 * scale), 1);
        if (drag.TrayChevron is RECT chevron && x >= chevron.Left - half && x <= chevron.Right + half)
        {
            // On the ^ button: an icon from the bar goes back into the box; one from the box stays there.
            if (drag.FromBox)
            {
                return null;
            }

            return new DropTarget(DropKind.TrayBox, BarLayoutStore.TrayId, 0, null, null, settings.DockAlignment, settings.SearchPlace,
                (chevron.Left + chevron.Right) / 2, chevron, ButtonRadius(chevron.Height / scale, chevron.Width / scale));
        }

        List<(TrayIcon Icon, RECT Rect)> tiles = drag.TrayTiles;
        int count = tiles.Count;
        int bestSlot = 0;
        int bestX = drag.TrayChevron is RECT afterChevron ? afterChevron.Right + half : row.Left;
        int bestDistance = int.MaxValue;
        for (int slot = 0; slot <= count; slot++)
        {
            int slotX = slot < count ? tiles[slot].Rect.Left - half
                : count > 0 ? tiles[count - 1].Rect.Right + half
                : bestX;
            int distance = Math.Abs(x - slotX);
            if (distance < bestDistance)
            {
                bestSlot = slot;
                bestX = slotX;
                bestDistance = distance;
            }
        }

        int index = 0;
        for (int i = 0; i < bestSlot; i++)
        {
            if (!string.Equals(tiles[i].Icon.Key, dragged.Key, StringComparison.Ordinal))
            {
                index++;
            }
        }

        return new DropTarget(DropKind.Tray, BarLayoutStore.TrayId, index, null, null, settings.DockAlignment, settings.SearchPlace, bestX, drag.TrayOutline, drag.TrayRadius);
    }

    private static BubbleSnap? BubbleSnapOf(DragState drag, string id)
    {
        foreach (BubbleSnap bubble in drag.Bubbles)
        {
            if (string.Equals(bubble.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return bubble;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- the end

    private void OnRootDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not null)
        {
            EndDrag(apply: true);
        }
    }

    private void OnRootDragCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // With a mouse, the drag ends when the button is let go (read on each tick), even if
        // Windows took the pointer away. Touch and pen have no button to read: put back.
        if (_drag is { Mouse: false })
        {
            EndDrag(apply: false);
        }
    }

    /// <summary>Ends the drag: the layer goes, the piece comes back, and the drop is applied (or not).</summary>
    private void EndDrag(bool apply)
    {
        DragState? drag = _drag;
        if (drag is null)
        {
            return;
        }

        if (apply && User32.GetCursorPos(out POINT at))
        {
            drag.Target = FindTarget(drag, at);
        }

        _drag = null;
        _dragTimer?.Stop();

        try
        {
            _dragLayer?.HideLayer();
        }
        catch (Exception ex)
        {
            Log.Warn("Bar drag: the drag layer could not be hidden.", ex);
        }

        try
        {
            drag.Piece.Opacity = drag.PieceOpacity;
            Root.ReleasePointerCaptures();
        }
        catch (Exception ex)
        {
            Log.Warn("Bar drag: the bar could not be put back as it was.", ex);
        }

        _lastInsideTick = Environment.TickCount64;

        bool pending = _dockSyncPending;
        _dockSyncPending = false;
        if (apply && drag.Target is DropTarget target)
        {
            ApplyDrop(drag, target);
        }
        else
        {
            Log.Info("Bar drag: " + DescribeDrag(drag) + " put back.");
        }

        if (pending && !_closing)
        {
            // Apps that opened or closed during the drag.
            SyncDock(rebuild: true);
        }

        QueuePlacement(forceReservation: false);
    }

    private void ApplyDrop(DragState drag, DropTarget target)
    {
        const string Source = "the bar (a drag)";
        try
        {
            switch (target.Kind)
            {
                case DropKind.Order:
                    _host.BarLayout.MoveBubble(target.Id, target.BeforeId, target.AfterId);
                    _host.Settings.Update(s => s.DockAlignment = target.Alignment, Source);
                    Log.Info("Bar drag: " + DescribeDrag(drag) + " placed; order " + string.Join(", ", _host.BarLayout.Order) + ".");
                    break;

                case DropKind.Search:
                    _host.Settings.Update(s => s.SearchPlace = target.Place, Source);
                    break;

                case DropKind.Place when drag.Item is DockItem item && BubbleSnapOf(drag, target.Id) is BubbleSnap destination:
                    var shown = new List<DockItem>(destination.Apps.Count);
                    foreach ((DockItem Item, RECT Rect) app in destination.Apps)
                    {
                        shown.Add(app.Item);
                    }

                    _host.BarLayout.PlaceApp(item, target.Id, target.Index, shown);
                    Log.Info("Bar drag: " + item.DisplayName + " placed in " + target.Id + " at place " + (target.Index + 1) + ".");
                    break;

                case DropKind.Tray when drag.TrayIcon is TrayIcon trayIcon:
                    var trayShown = new List<TrayIcon>(drag.TrayTiles.Count);
                    foreach ((TrayIcon Icon, RECT Rect) tile in drag.TrayTiles)
                    {
                        trayShown.Add(tile.Icon);
                    }

                    _host.BarLayout.PlaceTrayIcon(trayIcon, target.Index, trayShown, takeOut: drag.FromBox);
                    Log.Info("Bar drag: a tray icon placed at place " + (target.Index + 1) + (drag.FromBox ? ", taken out of the tray box." : "."));
                    break;

                case DropKind.TrayBox when drag.TrayIcon is TrayIcon boxedIcon:
                    _host.BarLayout.PutTrayIconInBox(boxedIcon);
                    Log.Info("Bar drag: a tray icon put back into the tray box.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bar drag: the drop could not be applied.", ex);
        }
    }

    private static string DescribeDrag(DragState drag)
    {
        return drag.Kind switch
        {
            DragKind.StartGroup => "the Start bubble",
            DragKind.Bubble => "the bubble " + drag.MovedId,
            DragKind.Search => "the search icon",
            DragKind.TrayIcon => "a tray icon",
            _ => drag.Item?.DisplayName ?? "an app",
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Compact is not built yet and shows as Segmented.</summary>
    private static TaskbarMode EffectiveMode(ShellSettings settings)
    {
        return settings.TaskbarMode == TaskbarMode.Compact ? TaskbarMode.Segmented : settings.TaskbarMode;
    }

    private double ButtonWidthDip()
    {
        return _host.Settings.Current.IconSize + 20;
    }

    private static bool IsOnBar(FrameworkElement element)
    {
        return element.Visibility == Visibility.Visible && element.XamlRoot is not null && element.ActualWidth > 0;
    }

    private static RECT Grow(RECT rect, int by)
    {
        return new RECT { Left = rect.Left - by, Top = rect.Top, Right = rect.Right + by, Bottom = rect.Bottom };
    }
}
