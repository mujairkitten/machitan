using System;
using System.Diagnostics;
using System.Globalization;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Color = Windows.UI.Color;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace Machitan.Widgets;

/// <summary>
/// The corner duo status ring, per the spec: a capsule like the info line (32 DIP tall, fully round: 16 DIP)
/// with no background. Its rim is the battery: it runs up the left, across the top, down the
/// right and in along the bottom from both corners, stopping short of the four volume dots in
/// the middle of the bottom edge. The fill follows the rim (white, red under 20 % or the charge
/// set in Settings, green while charging) over a 22 % track; a PC without a battery (a desktop)
/// gets the whole rim, in the ink. In the middle: the Wi-Fi arcs and the network's name (or
/// "Connecting...", "No signal", the plane and "Plane mode"), or on a network cable a plug and
/// the network's name; a bolt while charging. The volume dots stay as they are.
/// The width follows the label, animated. A click sends Win+A to Windows: its own quick settings.
/// The ink (white or near-black) follows the wallpaper behind the ring; where the wallpaper is
/// too mixed for either, a soft backing in the opposite tone is shown behind it (Transparency:
/// Auto, the default). With Transparency set to Manual the backing is always there, as
/// see-through as Settings says (100 %: none at all).
/// Everything shown is read from Windows; what Windows does not give is left out.
/// </summary>
public sealed partial class DuoRingWindow : CornerWidgetWindow
{
    private const double RingHeight = LineHeightDip;
    private const double Radius = LineHeightDip / 2;

    /// <summary>The Wi-Fi icon's width (DIP), per the spec.</summary>
    private const double WifiIconSize = 19;

    /// <summary>The plug's width (DIP); as tall as the Wi-Fi icon.</summary>
    private const double PlugIconWidth = 12;
    private const double Rim = 3;
    private const double SidePad = 14;
    private const double LabelMax = 170;
    private const double DotSize = 4;
    private const double DotGap = 4;
    private const double DotMargin = 7;
    private const double TrackAlpha = 0.22;
    private const double AnimateMs = 220;

    /// <summary>Half of the gap in the bottom rim: the four dots plus a clear margin each side.</summary>
    private const double GapHalf = ((4 * DotSize) + (3 * DotGap)) / 2 + DotMargin;

    private const double MinRingWidth = 2 * (Radius + GapHalf + 8);
    private const double WindowWidthDip = SidePad + WifiIconSize + 6 + LabelMax + 6 + 12 + SidePad + 10;

    private static readonly Color DarkInk = ColorHelper.FromArgb(0xFF, 0x1C, 0x1C, 0x1C);
    private static readonly Color Red = ColorHelper.FromArgb(0xFF, 0xFF, 0x3B, 0x30);
    private static readonly Color Green = ColorHelper.FromArgb(0xFF, 0x34, 0xC7, 0x59);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly Grid _ring;
    private readonly Border _backing;
    private readonly SolidColorBrush _backingBrush = new(Colors.Transparent);
    private readonly Shapes.Path _track;
    private readonly Shapes.Path _fill;
    private readonly Shapes.Ellipse[] _dots = new Shapes.Ellipse[4];
    private readonly Canvas _drawing;
    private readonly StackPanel _centre;
    private readonly Canvas _wifiIcon;
    private readonly Shapes.Path[] _arcs = new Shapes.Path[3];
    private readonly Shapes.Ellipse _wifiDot;
    private readonly FontIcon _planeIcon;
    /// <summary>A plug, drawn (Windows' icon font has none), for a network cable.</summary>
    private readonly Canvas _plugIcon;
    private readonly TextBlock _label;
    private readonly FontIcon _bolt;
    private readonly SolidColorBrush _fillBrush = new(Colors.White);
    private readonly SolidColorBrush _trackBrush = new(Colors.White);
    private readonly SolidColorBrush _litBrush = new(Colors.White);
    private readonly SolidColorBrush _unlitBrush = new(Colors.White);
    private readonly DispatcherQueueTimer _poll;
    private readonly NetworkMonitor _network;
    private readonly VolumeService _volume = new();
    private readonly WallpaperWatcher _wallpaperWatcher;

    private int _pollTicks;
    private bool _hasBattery;

    /// <summary>Windows says this PC has no battery at all (a desktop): the whole rim is drawn.</summary>
    private bool _noBattery;
    private int _batteryPercent = -1;
    private bool _charging;
    private bool _volumeKnown;
    private int _volumeDots;
    private int _volumePercent;
    private bool _volumeMuted;
    private NetworkState _net;

    private double _shownWidth;
    private double _fromWidth;
    private double _targetWidth;

    /// <summary>The width last told to the shell, so the widgets beside the ring move with it.</summary>
    private double _reportedWidth;
    private long _animationStart;
    private bool _animating;
    private bool _clickedOnce;

    private WallpaperSampler? _wallpaper;
    private bool _wallpaperLoading;
    private bool _wallpaperAgain;
    private bool _wallpaperReloadAgain;
    private bool? _wallpaperDarkInk;
    private bool _wallpaperBacking;
    private double _judgedWidth = -1;
    private string _lastJudgement = string.Empty;

    internal DuoRingWindow(ShellHost host)
        : base(host, "status ring", WindowWidthDip)
    {
        _track = new Shapes.Path
        {
            Stroke = _trackBrush,
            StrokeThickness = Rim,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _fill = new Shapes.Path
        {
            Stroke = _fillBrush,
            StrokeThickness = Rim,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Visibility = Visibility.Collapsed,
        };

        _drawing = new Canvas { IsHitTestVisible = false };
        _drawing.Children.Add(_track);
        _drawing.Children.Add(_fill);
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Shapes.Ellipse { Width = DotSize, Height = DotSize, Fill = _unlitBrush };
            _drawing.Children.Add(_dots[i]);
        }

        _wifiIcon = MakeWifiIcon(_arcs, out _wifiDot);
        _planeIcon = MakeGlyph(0xE709, 13);
        _plugIcon = MakePlugIcon(_litBrush);
        _bolt = MakeGlyph(0xE945, 11);
        _label = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = LabelMax,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _litBrush,
        };

        _centre = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _centre.Children.Add(_wifiIcon);
        _centre.Children.Add(_planeIcon);
        _centre.Children.Add(_plugIcon);
        _centre.Children.Add(_label);
        _centre.Children.Add(_bolt);

        _ring = new Grid
        {
            Height = RingHeight,
            Width = MinRingWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,

            // Hit-testable but draws nothing: the wallpaper shows through (no fill, blur or shadow).
            Background = new SolidColorBrush(Colors.Transparent),
        };
        // Behind everything; shown only when the wallpaper there is too mixed for the ink alone.
        _backing = new Border
        {
            CornerRadius = new CornerRadius(Radius),
            Background = _backingBrush,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _ring.Children.Add(_backing);
        _ring.Children.Add(_drawing);
        _ring.Children.Add(_centre);
        _ring.Tapped += (_, _) => OnClicked();

        Root.Children.Add(_ring);
        AddShape(_ring, Radius);

        _network = new NetworkMonitor(DispatcherQueue);
        _network.Changed += OnNetworkChanged;

        _wallpaperWatcher = new WallpaperWatcher(DispatcherQueue);
        _wallpaperWatcher.Changed += reason => CheckWallpaper(reason);

        // The top strip cover changes what is behind the ring as much as a new wallpaper does.
        Host.CoverChanged += OnCoverChanged;

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(500);
        _poll.Tick += (_, _) => Poll();

        _label.Text = "\u2026";
        ApplyColours();
        ApplyState(animate: false);
    }

    private protected override CustomPiece Piece => CustomPiece.DuoRing;

    private protected override bool IsEnabled(ShellSettings settings) => settings.ShowDuoRing;

    /// <summary>The width the ring is going to (it animates there), for the widgets beside it.</summary>
    private protected override double ShapeWidthDip() => Math.Max(_targetWidth, MinRingWidth);

    private protected override ScreenCorner CornerOf(ShellSettings settings) => settings.DuoRingCorner;

    private protected override double SizeOf(ShellSettings settings) => settings.DuoRingSize / 100.0;

    private protected override void OnSettingsApplied(ShellSettings settings)
    {
        ApplyColours();
    }

    private protected override void OnShownChanged(bool shown)
    {
        if (shown)
        {
            _network.Start();
            _pollTicks = 0;
            ReadBattery();
            ReadVolume(reconnect: true);
            ApplyState(animate: false);
            _poll.Start();
            _wallpaperWatcher.Start();
            _ = FollowWallpaperAsync(reload: false);
        }
        else
        {
            _poll.Stop();
            _network.Dispose();
            _wallpaperWatcher.Stop();
        }
    }

    private protected override void OnCornerChanged(bool left)
    {
        _ring.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private protected override void OnPlaced()
    {
        _ = FollowWallpaperAsync(reload: false);
    }

    private protected override void OnWallpaperChanged()
    {
        CheckWallpaper("Windows announced a new wallpaper");
    }

    private void OnCoverChanged()
    {
        if (IsShown)
        {
            JudgeWallpaper();
        }
    }

    private protected override void OnClosing()
    {
        Host.CoverChanged -= OnCoverChanged;
        _poll.Stop();
        _network.Changed -= OnNetworkChanged;
        _network.Dispose();
        _wallpaperWatcher.Dispose();
        StopAnimation();
    }

    private static FontIcon MakeGlyph(int codePoint, double size)
    {
        return new FontIcon
        {
            Glyph = ((char)codePoint).ToString(),
            FontFamily = IconFont,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
    }

    /// <summary>Three arcs over a dot, 19 x 17 DIP; each part lit or dimmed on its own.</summary>
    private static Canvas MakeWifiIcon(Shapes.Path[] arcs, out Shapes.Ellipse dot)
    {
        // 19 DIP wide: the largest arc spans 2 x 12.4 x sin 45 = 17.5 DIP, plus the stroke.
        var canvas = new Canvas { Width = WifiIconSize, Height = 17, VerticalAlignment = VerticalAlignment.Center };
        const double centreX = WifiIconSize / 2;
        const double centreY = 14.6;
        double[] radii = { 4.3, 8.35, 12.4 };
        double side = Math.Sqrt(0.5);
        for (int i = 0; i < arcs.Length; i++)
        {
            double r = radii[i];
            var figure = new PathFigure
            {
                StartPoint = new Point(centreX - (r * side), centreY - (r * side)),
                IsClosed = false,
                IsFilled = false,
                Segments = new PathSegmentCollection(),
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(centreX + (r * side), centreY - (r * side)),
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
            });
            var geometry = new PathGeometry { Figures = new PathFigureCollection() };
            geometry.Figures.Add(figure);
            arcs[i] = new Shapes.Path
            {
                Data = geometry,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            canvas.Children.Add(arcs[i]);
        }

        dot = new Shapes.Ellipse { Width = 3.4, Height = 3.4 };
        Canvas.SetLeft(dot, centreX - 1.7);
        Canvas.SetTop(dot, centreY - 1.7);
        canvas.Children.Add(dot);
        return canvas;
    }

    /// <summary>
    /// A plug, 12 x 17 DIP like the Wi-Fi icon's height: two prongs on top, the head, and the
    /// cord down from it, in the ink (the brush follows the wallpaper as the rest does).
    /// </summary>
    private static Canvas MakePlugIcon(SolidColorBrush ink)
    {
        var canvas = new Canvas
        {
            Width = PlugIconWidth,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        // The prongs.
        foreach (double x in new[] { 4.0, 8.0 })
        {
            canvas.Children.Add(new Shapes.Line
            {
                X1 = x,
                Y1 = 1.5,
                X2 = x,
                Y2 = 5,
                Stroke = ink,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        // The head: square at the prongs, narrowing to the cord.
        var head = new PathFigure
        {
            StartPoint = new Point(1.5, 5),
            IsClosed = true,
            IsFilled = true,
            Segments = new PathSegmentCollection(),
        };
        head.Segments.Add(new LineSegment { Point = new Point(10.5, 5) });
        head.Segments.Add(new LineSegment { Point = new Point(10.5, 8.5) });
        head.Segments.Add(new LineSegment { Point = new Point(7.6, 11.8) });
        head.Segments.Add(new LineSegment { Point = new Point(4.4, 11.8) });
        head.Segments.Add(new LineSegment { Point = new Point(1.5, 8.5) });
        var headGeometry = new PathGeometry { Figures = new PathFigureCollection() };
        headGeometry.Figures.Add(head);
        canvas.Children.Add(new Shapes.Path
        {
            Data = headGeometry,
            Fill = ink,
            Stroke = ink,
            StrokeThickness = 1,
            StrokeLineJoin = PenLineJoin.Round,
        });

        // The cord.
        canvas.Children.Add(new Shapes.Line
        {
            X1 = 6,
            Y1 = 11.8,
            X2 = 6,
            Y2 = 15.8,
            Stroke = ink,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Round,
        });
        return canvas;
    }

    /// <summary>The rim's path for a ring of the given width, and its length.</summary>
    private static PathGeometry RimGeometry(double width, out double length)
    {
        double inset = Rim / 2;
        double left = inset;
        double top = inset;
        double right = width - inset;
        double bottom = RingHeight - inset;
        double r = Radius - inset;
        double middle = width / 2;

        var figure = new PathFigure
        {
            StartPoint = new Point(middle - GapHalf, bottom),
            IsClosed = false,
            IsFilled = false,
            Segments = new PathSegmentCollection(),
        };
        figure.Segments.Add(new LineSegment { Point = new Point(left + r, bottom) });
        figure.Segments.Add(CornerArc(left, bottom - r, r));
        figure.Segments.Add(new LineSegment { Point = new Point(left, top + r) });
        figure.Segments.Add(CornerArc(left + r, top, r));
        figure.Segments.Add(new LineSegment { Point = new Point(right - r, top) });
        figure.Segments.Add(CornerArc(right, top + r, r));
        figure.Segments.Add(new LineSegment { Point = new Point(right, bottom - r) });
        figure.Segments.Add(CornerArc(right - r, bottom, r));
        figure.Segments.Add(new LineSegment { Point = new Point(middle + GapHalf, bottom) });

        length = (2 * ((middle - GapHalf) - (left + r)))
            + (2 * ((bottom - r) - (top + r)))
            + ((right - r) - (left + r))
            + (2 * Math.PI * r);

        var geometry = new PathGeometry { Figures = new PathFigureCollection() };
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static ArcSegment CornerArc(double x, double y, double r)
    {
        return new ArcSegment
        {
            Point = new Point(x, y),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        };
    }

    private static Color WithAlpha(Color colour, double alpha)
    {
        return ColorHelper.FromArgb((byte)Math.Round(alpha * 255), colour.R, colour.G, colour.B);
    }

    private static void Show(UIElement element, bool show)
    {
        Visibility visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility)
        {
            element.Visibility = visibility;
        }
    }

    /// <summary>
    /// The colour of everything but the battery's warning colours: what reads best on the
    /// wallpaper behind the ring; until that is known, white on the dark look, near-black on the light.
    /// </summary>
    private Color Ink => _wallpaperDarkInk is bool dark
        ? (dark ? DarkInk : Colors.White)
        : (IsDark ? Colors.White : DarkInk);

    private void ApplyColours()
    {
        Color foreground = Ink;
        _litBrush.Color = foreground;
        _unlitBrush.Color = WithAlpha(foreground, TrackAlpha);
        _trackBrush.Color = WithAlpha(foreground, TrackAlpha);
        _planeIcon.Foreground = _litBrush;
        _bolt.Foreground = _litBrush;

        // The backing is the opposite tone of the ink, soft enough to keep the wallpaper visible
        // (Auto), or as see-through as Settings says (Manual).
        ShellSettings settings = Host.Settings.Current;
        bool darkInk = foreground.R < 0x80;
        byte alpha = settings.DuoRingTransparencyAuto
            ? (byte)(darkInk ? 0x8C : 0x59)
            : (byte)Math.Round((100 - settings.DuoRingTransparency) * 255 / 100.0);
        _backingBrush.Color = darkInk
            ? ColorHelper.FromArgb(alpha, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(alpha, 0x00, 0x00, 0x00);
        Show(_backing, settings.DuoRingTransparencyAuto ? _wallpaperBacking : alpha > 0);
        ApplyBatteryColour();
    }

    private void ApplyBatteryColour()
    {
        bool low = _batteryPercent >= 0 && _batteryPercent < Host.Settings.Current.DuoRingLowBattery;
        _fillBrush.Color = _charging ? Green : low ? Red : Ink;
    }

    /// <summary>
    /// Twice a second: volume. Every 5 seconds: battery, and the volume device again (it can
    /// change). Every 30 seconds: whether the wallpaper changed without Windows saying so (a
    /// slideshow, a new picture in the same file).
    /// </summary>
    private void Poll()
    {
        _pollTicks++;
        if (_pollTicks % 60 == 0)
        {
            CheckWallpaper("30-second check");
        }

        bool slow = _pollTicks % 10 == 0;
        bool changed = false;
        if (slow)
        {
            changed |= ReadBattery();
        }

        changed |= ReadVolume(reconnect: slow);
        if (changed)
        {
            ApplyState(animate: true);
        }
    }

    /// <summary>
    /// The battery as Windows reports it: a level (the rim's fill), no battery at all (a desktop:
    /// the whole rim), or not known (Windows could not read it: only the track, nothing made up).
    /// </summary>
    private bool ReadBattery()
    {
        bool has = false;
        bool none = false;
        int percent = -1;
        bool charging = false;
        if (Kernel32.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
        {
            if (status.BatteryFlag != Kernel32.BATTERY_FLAG_UNKNOWN
                && (status.BatteryFlag & Kernel32.BATTERY_FLAG_NO_BATTERY) != 0)
            {
                none = true;
            }
            else if (status.BatteryFlag != Kernel32.BATTERY_FLAG_UNKNOWN
                && status.BatteryLifePercent != Kernel32.BATTERY_PERCENT_UNKNOWN)
            {
                has = true;
                percent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
                charging = (status.BatteryFlag & Kernel32.BATTERY_FLAG_CHARGING) != 0;
            }
        }

        if (has == _hasBattery && none == _noBattery && percent == _batteryPercent && charging == _charging)
        {
            return false;
        }

        if (none && !_noBattery)
        {
            Log.Info("Status ring: Windows reports no battery (a desktop PC); the rim is drawn whole.");
        }
        else if (!has && !none && (_hasBattery || _noBattery || _batteryPercent == -1))
        {
            Log.Info("Status ring: Windows could not read the battery; the rim shows only its track.");
        }

        _hasBattery = has;
        _noBattery = none;
        _batteryPercent = percent;
        _charging = charging;
        return true;
    }

    private bool ReadVolume(bool reconnect)
    {
        if (reconnect || !_volume.IsAvailable)
        {
            _volume.Connect();
        }

        bool known = _volume.TryRead(out float level, out bool muted);
        int percent = known ? Math.Clamp((int)Math.Round(level * 100), 0, 100) : 0;

        // Sam's rule, on the percentage Windows shows: 0 -> none, 1-25 -> 1, 26-50 -> 2,
        // 51-75 -> 3, 76-100 -> 4. Muted shows none.
        int dots = !known || muted ? 0
            : percent == 0 ? 0
            : percent <= 25 ? 1
            : percent <= 50 ? 2
            : percent <= 75 ? 3
            : 4;
        if (known == _volumeKnown && dots == _volumeDots && percent == _volumePercent && muted == _volumeMuted)
        {
            return false;
        }

        _volumeKnown = known;
        _volumeDots = dots;
        _volumePercent = percent;
        _volumeMuted = muted;
        return true;
    }

    private void OnNetworkChanged(NetworkState state)
    {
        _net = state;
        ApplyState(animate: true);
    }

    /// <summary>Puts the current readings on screen.</summary>
    private void ApplyState(bool animate)
    {
        // Network: icon and label.
        string label;
        bool wifiIcon = false;
        bool plane = false;
        bool plug = false;
        int litArcs = 0;
        bool dotLit = false;
        string networkTip;
        switch (_net.Kind)
        {
            case NetworkKind.WifiConnected:
                wifiIcon = true;
                dotLit = true;
                litArcs = _net.Bars < 0 ? 3 : _net.Bars;
                label = _net.Name.Length > 0 ? _net.Name : "Wi-Fi";
                networkTip = Fmt.F(Strings.RingWifi, label);
                break;
            case NetworkKind.WifiConnecting:
                wifiIcon = true;
                dotLit = true;
                label = Strings.RingConnecting;
                networkTip = Strings.RingWifiConnecting;
                break;
            case NetworkKind.WifiOff:
                plane = true;
                label = Strings.RingPlaneMode;
                networkTip = Strings.RingWifiOff;
                break;
            case NetworkKind.Wired:
                plug = true;
                label = _net.Name.Length > 0 ? _net.Name : Strings.NetEthernet;
                networkTip = Fmt.F(Strings.RingWired, label);
                break;
            case NetworkKind.NoSignal:
                wifiIcon = true;
                label = Strings.RingNoSignal;
                networkTip = Strings.RingWifiNotConnected;
                break;
            default:
                wifiIcon = true;
                label = "\u2026";
                networkTip = Strings.RingNetworkChecking;
                break;
        }

        if (_label.Text != label)
        {
            _label.Text = label;
        }

        Show(_wifiIcon, wifiIcon);
        Show(_planeIcon, plane);
        Show(_plugIcon, plug);
        Show(_bolt, _hasBattery && _charging);
        for (int i = 0; i < _arcs.Length; i++)
        {
            _arcs[i].Stroke = i < litArcs ? _litBrush : _unlitBrush;
        }

        _wifiDot.Fill = dotLit ? _litBrush : _unlitBrush;

        // Volume dots.
        for (int i = 0; i < _dots.Length; i++)
        {
            Show(_dots[i], _volumeKnown);
            _dots[i].Fill = i < _volumeDots ? _litBrush : _unlitBrush;
        }

        ApplyBatteryColour();

        // Width: the centre's natural width plus padding, never narrower than the corners
        // and the dot gap need.
        _centre.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double target = Math.Max(MinRingWidth, Math.Ceiling(_centre.DesiredSize.Width) + (2 * SidePad));
        target = Math.Min(target, WindowWidthDip);
        if (!animate || _shownWidth <= 0)
        {
            StopAnimation();
            SetWidth(target);
        }
        else
        {
            AnimateTo(target);
        }

        // Battery fill (the geometry depends on the width, so SetWidth redraws it too).
        DrawFill();

        if (Math.Abs(target - _reportedWidth) >= 0.5)
        {
            _reportedWidth = target;
            NoteShapeWidthChanged();
        }

        // A much wider or narrower ring covers other wallpaper: judge it again.
        if (_wallpaper is not null && Math.Abs(_targetWidth - _judgedWidth) > 16)
        {
            JudgeWallpaper();
        }

        string batteryTip = _hasBattery
            ? Fmt.F(_charging ? Strings.RingBatteryCharging : Strings.RingBattery, _batteryPercent)
            : _noBattery ? Strings.RingNoBattery : Strings.RingBatteryUnknown;
        string volumeTip = !_volumeKnown
            ? Strings.RingVolumeNoDevice
            : _volumeMuted
                ? Strings.RingVolumeMuted
                : Fmt.F(Strings.RingVolume, _volumePercent);
        string tip = batteryTip + "\n" + networkTip + "\n" + volumeTip;
        if (ToolTipService.GetToolTip(_ring) as string != tip)
        {
            ToolTipService.SetToolTip(_ring, tip);
            AutomationProperties.SetName(_ring, Fmt.F(Strings.RingName, tip.Replace("\n", ". ", StringComparison.Ordinal)));
        }
    }

    private void AnimateTo(double target)
    {
        if (Math.Abs(target - _targetWidth) < 0.5 && (_animating || Math.Abs(target - _shownWidth) < 0.5))
        {
            return;
        }

        _fromWidth = _shownWidth;
        _targetWidth = target;
        _animationStart = Stopwatch.GetTimestamp();
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void StopAnimation()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        double elapsed = Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds;
        double t = Math.Clamp(elapsed / AnimateMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        SetWidth(_fromWidth + ((_targetWidth - _fromWidth) * eased));
        if (t >= 1)
        {
            StopAnimation();
        }
    }

    /// <summary>Resizes the ring and redraws the rim, the fill and the dots for that width.</summary>
    private void SetWidth(double width)
    {
        width = Math.Round(width, 1);
        if (Math.Abs(width - _shownWidth) < 0.05)
        {
            return;
        }

        _shownWidth = width;
        if (!_animating)
        {
            _targetWidth = width;
        }

        _ring.Width = width;
        _drawing.Width = width;
        _drawing.Height = RingHeight;
        _track.Data = RimGeometry(width, out _);
        DrawFill();

        double first = (width / 2) - (((4 * DotSize) + (3 * DotGap)) / 2);
        for (int i = 0; i < _dots.Length; i++)
        {
            Canvas.SetLeft(_dots[i], first + (i * (DotSize + DotGap)));
            Canvas.SetTop(_dots[i], RingHeight - (Rim / 2) - (DotSize / 2));
        }
    }

    /// <summary>
    /// The battery part of the rim: the same path, cut to the battery's share of its length.
    /// No battery at all (a desktop): the whole rim, in the ink.
    /// </summary>
    private void DrawFill()
    {
        if (_shownWidth <= 0 || !(_noBattery || (_hasBattery && _batteryPercent > 0)))
        {
            Show(_fill, false);
            return;
        }

        PathGeometry geometry = RimGeometry(_shownWidth, out double length);
        double share = _noBattery ? length : Math.Clamp(_batteryPercent / 100.0, 0, 1) * length;

        // Dash lengths count in stroke thicknesses.
        _fill.Data = geometry;
        _fill.StrokeDashArray = new DoubleCollection { share / Rim, (length * 2) / Rim };
        Show(_fill, true);
    }

    /// <summary>The ring's place on screen at its target width (physical pixels).</summary>
    private RECT RingScreenRect()
    {
        RECT window = PlacedRect;
        if (window.Width <= 0 || window.Height <= 0)
        {
            return default;
        }

        // Design sizes, times the screen scale and the ring's size setting.
        double scale = Scale() * SizeFactor;
        int pad = (int)Math.Round(PadDip * scale);
        int width = (int)Math.Round(Math.Max(_targetWidth, MinRingWidth) * scale);
        int height = (int)Math.Round(RingHeight * scale);
        int left = OnLeft ? window.Left + pad : window.Right - pad - width;
        int top = window.Top + pad;
        return new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    /// <summary>
    /// Reads the wallpaper behind the ring (again when <paramref name="reload"/>, or when the
    /// ring moved to another screen), then judges the ink. One read at a time.
    /// </summary>
    private async System.Threading.Tasks.Task FollowWallpaperAsync(bool reload)
    {
        if (!IsShown)
        {
            return;
        }

        if (_wallpaperLoading)
        {
            _wallpaperAgain = true;
            _wallpaperReloadAgain |= reload;
            return;
        }

        RECT ring = RingScreenRect();
        if (ring.Width <= 0)
        {
            return;
        }

        if (reload || _wallpaper is null || !_wallpaper.Covers(ring))
        {
            _wallpaperLoading = true;
            try
            {
                _wallpaper = await WallpaperSampler.LoadAsync(ring);
            }
            catch (Exception ex)
            {
                _wallpaper = null;
                Log.Warn("Status ring: the wallpaper could not be read; it keeps the theme's colours.", ex);
            }
            finally
            {
                _wallpaperLoading = false;
            }
        }

        JudgeWallpaper();

        if (_wallpaperAgain)
        {
            bool again = _wallpaperReloadAgain;
            _wallpaperAgain = false;
            _wallpaperReloadAgain = false;
            await FollowWallpaperAsync(again);
        }
    }

    /// <summary>
    /// Reads the wallpaper settings (no decoding) and reads the picture again when they differ
    /// from the ones the ink was judged on (or when nothing could be read before). Called by
    /// the wallpaper watcher, Windows' notice and the 30-second check.
    /// </summary>
    private void CheckWallpaper(string reason)
    {
        if (!IsShown)
        {
            return;
        }

        if (_wallpaperLoading)
        {
            _wallpaperAgain = true;
            _wallpaperReloadAgain = true;
            return;
        }

        RECT ring = RingScreenRect();
        if (ring.Width <= 0)
        {
            return;
        }

        WallpaperSettings? now = WallpaperSampler.ReadSettings(ring);
        if (now is null)
        {
            return;
        }

        if (_wallpaper is null || now.Signature != _wallpaper.Signature)
        {
            Log.Info("Status ring: the wallpaper changed (" + reason + "); reading it again.");
            _ = FollowWallpaperAsync(reload: true);
        }
    }

    /// <summary>
    /// Picks the ink and the backing for what is behind the ring: the wallpaper, seen through
    /// the top strip cover when the cover is on (its colour blended in, as the screen shows it).
    /// Logs a change.
    /// </summary>
    private void JudgeWallpaper()
    {
        RECT ring = RingScreenRect();
        _judgedWidth = _targetWidth;
        CoverTint? cover = ring.Width > 0 ? Host.CoverOver(ring) : null;
        if (_wallpaper is null && cover is { Alpha: >= 0.5 } flat)
        {
            // The wallpaper could not be read, but a mostly solid cover is behind the ring.
            WallpaperJudgement onCover = WallpaperSampler.JudgeFlat(flat.R, flat.G, flat.B);
            if (_wallpaperDarkInk != onCover.DarkInk || _wallpaperBacking)
            {
                _wallpaperDarkInk = onCover.DarkInk;
                _wallpaperBacking = false;
                ApplyColours();
            }

            return;
        }

        if (_wallpaper is null || ring.Width <= 0)
        {
            if (_wallpaperDarkInk is not null || _wallpaperBacking)
            {
                _wallpaperDarkInk = null;
                _wallpaperBacking = false;
                ApplyColours();
            }

            return;
        }

        WallpaperJudgement judgement = _wallpaper.Judge(ring, cover);
        bool changed = _wallpaperDarkInk != judgement.DarkInk || _wallpaperBacking != judgement.NeedsBacking;
        _wallpaperDarkInk = judgement.DarkInk;
        _wallpaperBacking = judgement.NeedsBacking;
        if (changed)
        {
            ApplyColours();
        }

        string text = "Status ring: the wallpaper behind it is " + _wallpaper.Kind + " from " + _wallpaper.Source
            + (cover is CoverTint shown ? " under the top strip cover (" + Math.Round(shown.Alpha * 100).ToString(CultureInfo.InvariantCulture) + "% solid)" : string.Empty) + ", "
            + (judgement.Luminance >= 0.18 ? "light" : "dark") + " (luminance "
            + judgement.Luminance.ToString("0.00", CultureInfo.InvariantCulture) + ", "
            + Math.Round(judgement.HardShare * 100).ToString(CultureInfo.InvariantCulture) + "% hard to read on); "
            + (judgement.DarkInk ? "dark" : "white") + " ink, "
            + (judgement.NeedsBacking ? "with a soft backing." : "no backing.");
        if (text != _lastJudgement)
        {
            _lastJudgement = text;
            Log.Info(text);
        }
    }

    private void OnClicked()
    {
        // Windows' own quick settings: Win+A, marked so Machitan's keyboard hook lets it
        // through to Windows instead of opening Machitan's quick settings.
        bool sent = KeyChord.SendWin('A');
        if (!_clickedOnce || !sent)
        {
            _clickedOnce = true;
            if (sent)
            {
                Log.Info("Status ring clicked: Win+A sent to Windows (its quick settings).");
            }
            else
            {
                Log.Warn("Status ring clicked, but Windows did not take the Win+A key press.");
            }
        }
    }
}
