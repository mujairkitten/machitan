using System;
using System.Diagnostics;
using System.Globalization;
using Machitan.Config;
using Machitan.Core;
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
/// The corner duo status ring, per the spec: a rounded rectangle (36 DIP tall, 10 DIP corners)
/// with no background. Its rim is the battery: it runs up the left, across the top, down the
/// right and in along the bottom from both corners, stopping short of the four volume dots in
/// the middle of the bottom edge. The fill follows the rim (white, red under 20 %, green while
/// charging) over a 22 % track. In the middle: the Wi-Fi arcs and the network's name (or
/// "Connecting...", "No signal", the plane and "Plane mode"), with a bolt while charging.
/// The width follows the label, animated. A click sends Win+A to Windows: its own quick settings.
/// The ink (white or near-black) follows the wallpaper behind the ring; where the wallpaper is
/// too mixed for either, a soft backing in the opposite tone is shown behind it.
/// Everything shown is read from Windows; what Windows does not give is left out.
/// </summary>
public sealed partial class DuoRingWindow : CornerWidgetWindow
{
    private const double RingHeight = LineHeightDip;
    private const double Radius = 10;
    private const double Rim = 3;
    private const double SidePad = 16;
    private const double LabelMax = 170;
    private const double DotSize = 4;
    private const double DotGap = 4;
    private const double DotMargin = 7;
    private const double TrackAlpha = 0.22;
    private const double AnimateMs = 220;

    /// <summary>Half of the gap in the bottom rim: the four dots plus a clear margin each side.</summary>
    private const double GapHalf = ((4 * DotSize) + (3 * DotGap)) / 2 + DotMargin;

    private const double MinRingWidth = 2 * (Radius + GapHalf + 8);
    private const double WindowWidthDip = SidePad + 16 + 6 + LabelMax + 4 + 12 + SidePad + 10;

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
    private readonly FontIcon _wiredIcon;
    private readonly TextBlock _label;
    private readonly FontIcon _bolt;
    private readonly SolidColorBrush _fillBrush = new(Colors.White);
    private readonly SolidColorBrush _trackBrush = new(Colors.White);
    private readonly SolidColorBrush _litBrush = new(Colors.White);
    private readonly SolidColorBrush _unlitBrush = new(Colors.White);
    private readonly DispatcherQueueTimer _poll;
    private readonly NetworkMonitor _network;
    private readonly VolumeService _volume = new();

    private int _pollTicks;
    private bool _hasBattery;
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
        _planeIcon = MakeGlyph(0xE709, 14);
        _wiredIcon = MakeGlyph(0xE839, 14);
        _bolt = MakeGlyph(0xE945, 12);
        _label = new TextBlock
        {
            FontSize = 13,
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
        _centre.Children.Add(_wiredIcon);
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

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(500);
        _poll.Tick += (_, _) => Poll();

        _label.Text = "\u2026";
        ApplyColours();
        ApplyState(animate: false);
    }

    private protected override bool IsEnabled(ShellSettings settings) => settings.DuoRingEnabled;

    private protected override ScreenCorner CornerOf(ShellSettings settings) => settings.DuoRingCorner;

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
            _ = FollowWallpaperAsync(reload: false);
        }
        else
        {
            _poll.Stop();
            _network.Dispose();
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
        _ = FollowWallpaperAsync(reload: true);
    }

    private protected override void OnClosing()
    {
        _poll.Stop();
        _network.Changed -= OnNetworkChanged;
        _network.Dispose();
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

    /// <summary>Three arcs over a dot, 16 x 14 DIP; each part lit or dimmed on its own.</summary>
    private static Canvas MakeWifiIcon(Shapes.Path[] arcs, out Shapes.Ellipse dot)
    {
        var canvas = new Canvas { Width = 16, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        const double centreX = 8;
        const double centreY = 12;
        double[] radii = { 3.6, 7.0, 10.4 };
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
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            canvas.Children.Add(arcs[i]);
        }

        dot = new Shapes.Ellipse { Width = 3, Height = 3 };
        Canvas.SetLeft(dot, centreX - 1.5);
        Canvas.SetTop(dot, centreY - 1.5);
        canvas.Children.Add(dot);
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
        figure.Segments.Add(Corner(left, bottom - r, r));
        figure.Segments.Add(new LineSegment { Point = new Point(left, top + r) });
        figure.Segments.Add(Corner(left + r, top, r));
        figure.Segments.Add(new LineSegment { Point = new Point(right - r, top) });
        figure.Segments.Add(Corner(right, top + r, r));
        figure.Segments.Add(new LineSegment { Point = new Point(right, bottom - r) });
        figure.Segments.Add(Corner(right - r, bottom, r));
        figure.Segments.Add(new LineSegment { Point = new Point(middle + GapHalf, bottom) });

        length = (2 * ((middle - GapHalf) - (left + r)))
            + (2 * ((bottom - r) - (top + r)))
            + ((right - r) - (left + r))
            + (2 * Math.PI * r);

        var geometry = new PathGeometry { Figures = new PathFigureCollection() };
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static ArcSegment Corner(double x, double y, double r)
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
        _wiredIcon.Foreground = _litBrush;
        _bolt.Foreground = _litBrush;

        // The backing is the opposite tone of the ink, soft enough to keep the wallpaper visible.
        bool darkInk = foreground.R < 0x80;
        _backingBrush.Color = darkInk
            ? ColorHelper.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x59, 0x00, 0x00, 0x00);
        Show(_backing, _wallpaperBacking);
        ApplyBatteryColour();
    }

    private void ApplyBatteryColour()
    {
        _fillBrush.Color = _charging ? Green : _batteryPercent is >= 0 and < 20 ? Red : Ink;
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
            CheckWallpaper();
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

    private bool ReadBattery()
    {
        bool has = false;
        int percent = -1;
        bool charging = false;
        if (Kernel32.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status)
            && status.BatteryFlag != Kernel32.BATTERY_FLAG_UNKNOWN
            && (status.BatteryFlag & Kernel32.BATTERY_FLAG_NO_BATTERY) == 0
            && status.BatteryLifePercent != Kernel32.BATTERY_PERCENT_UNKNOWN)
        {
            has = true;
            percent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
            charging = (status.BatteryFlag & Kernel32.BATTERY_FLAG_CHARGING) != 0;
        }

        if (has == _hasBattery && percent == _batteryPercent && charging == _charging)
        {
            return false;
        }

        if (!has && _hasBattery != has)
        {
            Log.Info("Status ring: Windows reports no battery; the rim shows only its track.");
        }

        _hasBattery = has;
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
        bool wired = false;
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
                networkTip = "Wi-Fi: " + label + (_net.Bars < 0
                    ? " (Windows gives no signal strength)"
                    : " (signal " + _net.Bars.ToString(CultureInfo.InvariantCulture) + " of 3)");
                break;
            case NetworkKind.WifiConnecting:
                wifiIcon = true;
                dotLit = true;
                label = "Connecting...";
                networkTip = "Wi-Fi: connecting";
                break;
            case NetworkKind.WifiOff:
                plane = true;
                label = "Plane mode";
                networkTip = "Wi-Fi is off";
                break;
            case NetworkKind.Wired:
                wired = true;
                label = _net.Name.Length > 0 ? _net.Name : "Ethernet";
                networkTip = "Wired: " + label;
                break;
            case NetworkKind.NoSignal:
                wifiIcon = true;
                label = "No signal";
                networkTip = "Wi-Fi: not connected";
                break;
            default:
                wifiIcon = true;
                label = "\u2026";
                networkTip = "Network: checking";
                break;
        }

        if (_label.Text != label)
        {
            _label.Text = label;
        }

        Show(_wifiIcon, wifiIcon);
        Show(_planeIcon, plane);
        Show(_wiredIcon, wired);
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

        // A much wider or narrower ring covers other wallpaper: judge it again.
        if (_wallpaper is not null && Math.Abs(_targetWidth - _judgedWidth) > 16)
        {
            JudgeWallpaper();
        }

        string batteryTip = _hasBattery
            ? "Battery " + _batteryPercent.ToString(CultureInfo.InvariantCulture) + "%" + (_charging ? ", charging" : string.Empty)
            : "No battery";
        string volumeTip = !_volumeKnown
            ? "Volume: no output device"
            : _volumeMuted
                ? "Volume muted"
                : "Volume " + _volumePercent.ToString(CultureInfo.InvariantCulture) + "%";
        string tip = batteryTip + "\n" + networkTip + "\n" + volumeTip;
        if (ToolTipService.GetToolTip(_ring) as string != tip)
        {
            ToolTipService.SetToolTip(_ring, tip);
            AutomationProperties.SetName(_ring, "Status. " + tip.Replace("\n", ". ", StringComparison.Ordinal) + ". Opens Windows' quick settings.");
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

    /// <summary>The battery part of the rim: the same path, cut to the battery's share of its length.</summary>
    private void DrawFill()
    {
        if (!_hasBattery || _batteryPercent <= 0 || _shownWidth <= 0)
        {
            Show(_fill, false);
            return;
        }

        PathGeometry geometry = RimGeometry(_shownWidth, out double length);
        double share = Math.Clamp(_batteryPercent / 100.0, 0, 1) * length;

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

        double scale = Scale();
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

    /// <summary>Every 30 s: reads Windows' wallpaper record (no decoding) and reloads when it changed.</summary>
    private void CheckWallpaper()
    {
        if (_wallpaper is null || _wallpaperLoading)
        {
            return;
        }

        RECT ring = RingScreenRect();
        if (ring.Width <= 0)
        {
            return;
        }

        WallpaperSettings? now = WallpaperSampler.ReadSettings(ring);
        if (now is not null && now.Signature != _wallpaper.Signature)
        {
            _ = FollowWallpaperAsync(reload: true);
        }
    }

    /// <summary>Picks the ink and the backing for the wallpaper behind the ring, and logs a change.</summary>
    private void JudgeWallpaper()
    {
        RECT ring = RingScreenRect();
        _judgedWidth = _targetWidth;
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

        WallpaperJudgement judgement = _wallpaper.Judge(ring);
        bool changed = _wallpaperDarkInk != judgement.DarkInk || _wallpaperBacking != judgement.NeedsBacking;
        _wallpaperDarkInk = judgement.DarkInk;
        _wallpaperBacking = judgement.NeedsBacking;
        if (changed)
        {
            ApplyColours();
        }

        string text = "Status ring: the wallpaper behind it is " + _wallpaper.Kind + ", "
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
