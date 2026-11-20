using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Start;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Color = Windows.UI.Color;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace Machitan.Widgets;

/// <summary>
/// Dynaland: a black pill at the top of the bar's screen, in one of two shapes: a notch (the
/// default; hanging from the top edge, its sides curving out into the edge at the top, round
/// bottom corners) or an iPhone-inspired island (floating just below the edge, round all around).
/// Idle it is a small empty pill and does nothing (no clock: the info line owns the time).
/// It grows only for live things: music (the cover, the title and a note in the cover's
/// colour; on hover, or for 4 seconds when a new track starts, the full view with app,
/// cover, title, artist, controls and position), a volume change (the level for 1.8 s), and
/// camera or microphone use (a green camera dot and an orange mic dot on the pill in every
/// state; for 4 seconds when a use starts, and on hover, an "In use now" view naming the
/// apps). Never for notifications: Machitan cannot see them arrive.
/// Hovering opens Dynaland itself: with music playing, on the player first, with an arrow to
/// the general page; otherwise straight on the general page ("In use now" for the camera and
/// microphone, and a button for Windows' own notification centre), with an arrow back to the
/// player while music plays. Moving away closes it.
/// The window has one fixed size and never resizes: the pill is a Composition shape whose
/// size, position and corner radius animate inside it, and the window is cut to the pill, so
/// everything outside the pill lets clicks through. Never takes focus; steps aside for
/// full-screen apps; kept in place and on top every 2 seconds.
/// </summary>
public sealed partial class DynalandWindow : Window
{

    private const double Pad = 4;
    /// <summary>The open pages' standard width (DIP); the user's width setting replaces it.</summary>
    private const double StandardStageWidth = 400;
    /// <summary>The open views' height: the player, and the general page without tray icons.</summary>
    private const double OpenHeight = 152;

    // The tray on the general page: a title, then up to two rows of icon tiles.
    private const double TrayTitleHeight = 24;
    private const double TrayRowHeight = 36;
    private const int MaxTrayRows = 2;
    private const double TrayTile = 32;
    private const double TraySpacing = 4;

    /// <summary>The stage: room for the tallest view (the general page with two rows of tray icons).</summary>
    private const double StageHeight = OpenHeight + TrayTitleHeight + (MaxTrayRows * TrayRowHeight);
    private const double CollapsedHeight = 32;
    private const double CollapsedRadius = 12;
    private const double MediaRadius = 22;
    private const double PrivacyWidth = 340;
    private const double PrivacyHeight = 80;
    private const double PrivacyRadius = 20;
    private const double IdleWidth = 110;
    private const double MinBusyWidth = 96;
    private const double MaxCollapsedWidth = 270;
    private const double MiniTextMaxWidth = 160;
    private const double CollapsedSidePad = 14;

    /// <summary>The pill's top runs this far past the screen edge, so its top corners never show round.</summary>
    private const double TopExtension = 40;

    /// <summary>
    /// Notch shape: radius of the curves where the notch's sides flare out into the screen's
    /// top edge, like a laptop's notch. The window is this much wider on each side for them.
    /// </summary>
    private const double EarRadius = 10;

    /// <summary>Island shape: the gap between the screen's top edge and the floating pill.</summary>
    private const double IslandGap = 8;

    /// <summary>Island shape: corner radii of the open views (the collapsed pill is a full capsule).</summary>
    private const double IslandOpenRadius = 30;
    private const double IslandPrivacyRadius = 28;

    private const int AnimateMs = 300;
    private const int PeekMs = 4000;
    private const int VolumeShowMs = 1800;

    private static readonly Color Neutral = ColorHelper.FromArgb(0xFF, 0xEC, 0xEC, 0xEC);
    private static readonly Color Dim = ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
    private static readonly Color CameraGreen = ColorHelper.FromArgb(0xFF, 0x34, 0xC7, 0x59);
    private static readonly Color MicOrange = ColorHelper.FromArgb(0xFF, 0xFF, 0x9F, 0x0A);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly ShellHost _host;
    private readonly nint _hwnd;
    private readonly Grid _root;

    /// <summary>The window's content: Dynaland at its standard size, scaled as a whole (size setting).</summary>
    private readonly Canvas _canvas;
    private readonly ScaleTransform _zoom = new();
    private double _size = 1;

    /// <summary>The open pages' width (DIP, the width setting): the stage's width.</summary>
    private double _stageWidth = StandardStageWidth;

    /// <summary>How far the closed pill may grow (music): follows the width setting.</summary>
    private double _maxCollapsedWidth = MaxCollapsedWidth;

    /// <summary>The closed pill's width with nothing playing: follows the width setting.</summary>
    private double _idleWidth = IdleWidth;

    private Border? _customShield;
    private MenuFlyout? _customMenu;

    /// <summary>The window at standard size (DIP): the stage, the ears either side, the island's gap, the pad.</summary>
    private double DesignWidth => _stageWidth + (2 * (Pad + EarRadius));
    private const double DesignHeight = StageHeight + IslandGap + (2 * Pad);
    private readonly Grid _stage;
    private readonly Grid _pillLayer;
    private readonly Border _collapsedHit;
    private readonly Border _expandedHit;

    /// <summary>The notch's top band, pill plus both curves, so the window is not cut there.</summary>
    private readonly Border _earHit;
    private readonly WindowCutter _cutter;
    private readonly List<(StackPanel Panel, Ellipse Camera, Ellipse Mic)> _dotSets = new();

    // Collapsed.
    private readonly Grid _collapsedContent;
    private readonly StackPanel _idleRow;
    private readonly StackPanel _miniRow;
    private readonly Border _miniArt;
    private readonly ImageBrush _miniArtBrush = new() { Stretch = Stretch.UniformToFill };
    private readonly Marquee _miniText;
    private readonly FontIcon _miniState;
    private readonly StackPanel _volumeRow;
    private readonly FontIcon _volumeIcon;
    private readonly ColumnDefinition _volumeDone;
    private readonly ColumnDefinition _volumeLeft;
    private readonly TextBlock _volumeText;

    // Music, open.
    private readonly Grid _mediaContent;
    private readonly TextBlock _appName;
    private readonly Border _bigArt;
    private readonly ImageBrush _bigArtBrush = new() { Stretch = Stretch.UniformToFill };
    private readonly SolidColorBrush _noArtBrush = new(ColorHelper.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
    private readonly FontIcon _noArtIcon;
    private readonly Marquee _title;
    private readonly Marquee _artist;
    private readonly Button _previous;
    private readonly Button _playPause;
    private readonly FontIcon _playPauseIcon;
    private readonly Button _next;
    private readonly Grid _timeline;

    /// <summary>The timeline as a seek bar: a press or drag moves it, the app jumps there on release.</summary>
    private readonly Grid _seekArea;
    private readonly Ellipse _seekThumb;
    private readonly TranslateTransform _seekThumbMove = new();
    private bool _seeking;
    private bool _seekHover;
    private double _seekFraction;
    private TimeSpan _seekTarget;
    private long _seekHoldUntil;
    private readonly TextBlock _elapsed;
    private readonly TextBlock _total;
    private readonly ColumnDefinition _doneColumn;
    private readonly ColumnDefinition _leftColumn;

    // In use now, open (for a moment when a use starts).
    private readonly Grid _privacyContent;
    private readonly TextBlock _privacyText;

    // General page, open.
    private readonly Grid _generalContent;
    private readonly StackPanel _useRows;
    private readonly TextBlock _noUses;
    private readonly Button _toGeneral;
    private readonly Button _toMedia;

    private readonly SolidColorBrush _accentBrush = new(Neutral);
    private readonly SolidColorBrush _whiteBrush = new(Colors.White);
    private readonly SolidColorBrush _dimBrush = new(Dim);

    // Composition: held for the window's whole life (never left to .NET's finalizer).
    private readonly Compositor _compositor;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionSpriteShape _shape;
    private readonly ShapeVisual _shapeVisual;
    private readonly ContainerVisual _pillHost;

    // The notch's two outward curves; null when they could not be drawn (square corners then).
    private ShapeVisual? _leftEar;
    private ShapeVisual? _rightEar;
    private ExpressionAnimation? _leftEarFollow;
    private ExpressionAnimation? _rightEarFollow;
    private readonly CompositionEasingFunction _ease;
    private readonly Vector2KeyFrameAnimation _sizeAnimation;
    private readonly Vector2KeyFrameAnimation _offsetAnimation;
    private readonly Vector2KeyFrameAnimation _radiusAnimation;
    private readonly Visual _collapsedVisual;
    private readonly Visual _mediaVisual;
    private readonly Visual _privacyVisual;
    private readonly Visual _generalVisual;
    private readonly ScalarKeyFrameAnimation _collapsedFade;
    private readonly ScalarKeyFrameAnimation _mediaFade;
    private readonly ScalarKeyFrameAnimation _privacyFade;
    private readonly ScalarKeyFrameAnimation _generalFade;

    private readonly DispatcherQueueTimer _heal;
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _hoverTimer;
    private readonly DispatcherQueueTimer _leaveTimer;
    private readonly DispatcherQueueTimer _cursorTimer;
    private readonly DispatcherQueueTimer _regionTimer;
    private readonly DispatcherQueueTimer _peekTimer;
    private readonly DispatcherQueueTimer _volumeTimer;
    private readonly DispatcherQueueTimer _volumePoll;
    private readonly MediaService _media;
    private readonly PrivacyMonitor _privacy;
    /// <summary>The tray icons: Machitan's one receiver, kept by the shell (Dynaland or the bar shows them).</summary>
    private TrayIconService _tray => _host.Tray;

    // Dragging a tray tile to another place (Dynaland's general page).
    private const double TrayDragStartDip = 6;
    private Grid _trayArea = new();
    private Canvas _trayOverlay = new();
    private Border _trayMarker = new();
    private string? _trayPressKey;
    private uint _trayPressPointer;
    private Windows.Foundation.Point _trayPressAt;
    private string? _trayDragKey;
    private int _trayDropIndex = -1;
    private bool _traySuppressClick;
    private List<TrayIcon> _trayOrdered = new();
    private readonly StackPanel _traySection;
    private readonly StackPanel _trayRows;

    /// <summary>One tile per tray icon, kept while the icon lives (a new picture changes only its tile).</summary>
    private readonly Dictionary<string, TrayTileView> _trayTiles = new(StringComparer.Ordinal);
    private List<string> _trayShownKeys = new();
    private int _trayMore;

    /// <summary>Windows' double-click time: how long Dynaland waits after a left click on a tile for a second one.</summary>
    private readonly long _doubleClickMs = Math.Clamp((long)User32.GetDoubleClickTime(), 200L, 900L);
    private readonly DispatcherQueueTimer _trayCloseTimer;
    private string? _lastTrayKey;
    private long _lastTrayClickAt;

    /// <summary>After a right-click on a tray tile: Dynaland stays open while the app's menu is up.</summary>
    private bool _menuHolding;
    private long _menuHoldUntil;

    /// <summary>The general page's height now: taller by the tray's rows while there are tray icons.</summary>
    private double _generalHeight = OpenHeight;
    private readonly VolumeService _volume = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    private bool _onGeneralPage;
    private DynalandStyle _style = DynalandStyle.Notch;

    /// <summary>At the screen's bottom edge (mirrored: the pill grows upward) instead of the top.</summary>
    private bool _atBottom;

    /// <summary>The bar is on Dynaland's edge and covers its centre: Dynaland floats just past the bar.</summary>
    private bool _pastBar;

    // The notch curves' circles, to move them to the other corner when Dynaland changes edge.
    private readonly List<(CompositionEllipseGeometry Geometry, bool Left)> _earCircles = new();
    private double _topExtension = TopExtension;
    private double _gap;
    private double _collapsedRadius = CollapsedRadius;
    private double _openRadius = MediaRadius;
    private double _privacyRadius = PrivacyRadius;
    private WindowSubclass? _subclass;
    private RECT _placed;
    private bool _started;
    private bool _closed;
    private bool _shown;
    private bool _servicesStarted;
    private bool _hovering;
    private bool _pointerInside;
    private PillShape _pill = PillShape.Collapsed;
    private PillShape _peek = PillShape.Collapsed;
    private double _collapsedWidth;
    private string _peekedTrack = string.Empty;
    private string _privacySignature = string.Empty;
    private List<(string Name, bool Camera)> _uses = new();
    private bool _volumeKnown;
    private int _volumePercent = -1;
    private bool _volumeMuted;
    private int _volumeTicks;
    private long _volumeUntil;
    private string _artKey = string.Empty;
    private bool _artFound;
    private int _artVersion;
    private bool _accentLogged;
    private bool _privacyLogged;

    internal DynalandWindow(ShellHost host)
    {
        _host = host;
        Title = "Machitan Dynaland";
        _stageWidth = WidthSetting();
        _maxCollapsedWidth = Math.Round(MaxCollapsedWidth * WidthRatio);
        _idleWidth = Math.Round(IdleWidth * WidthRatio);
        _collapsedWidth = _idleWidth;

        _root = new Grid
        {
            Padding = new Thickness(Pad),
            Background = new SolidColorBrush(Colors.Transparent),

            // A notch is black whatever the theme.
            RequestedTheme = ElementTheme.Dark,

            // Laid out at standard size, scaled as a whole by the size setting; the canvas
            // lets the scaled content fill the window without layout clipping.
            Width = DesignWidth,
            Height = DesignHeight,
            RenderTransform = _zoom,
        };
        _canvas = new Canvas { Background = new SolidColorBrush(Colors.Transparent) };
        _canvas.Children.Add(_root);
        Content = _canvas;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, _hwnd, alwaysOnTop: true);
        long exStyle = User32.GetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE, (nint)(exStyle | User32.WS_EX_NOACTIVATE));
        _subclass = new WindowSubclass(_hwnd, OnWindowMessage);

        _stage = new Grid
        {
            Width = _stageWidth,
            Height = StageHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root.Children.Add(_stage);

        _pillLayer = new Grid { IsHitTestVisible = false };
        _stage.Children.Add(_pillLayer);

        // Invisible outlines the window is cut to; they reach past the top so the cut is square there.
        _collapsedHit = MakeOutline(_idleWidth, CollapsedHeight);
        _expandedHit = MakeOutline(_stageWidth, OpenHeight);
        _expandedHit.Visibility = Visibility.Collapsed;
        _stage.Children.Add(_collapsedHit);
        _stage.Children.Add(_expandedHit);

        // In the window's root (wider than the stage), at the screen's top edge.
        _earHit = new Border
        {
            Width = _idleWidth + (2 * EarRadius),
            Height = EarRadius,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _root.Children.Add(_earHit);

        // ---- collapsed: idle (dots only), music, volume
        _idleRow = Row();
        _idleRow.Children.Add(MakeDots(6));

        _miniArt = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Background = _miniArtBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        // "Title - Artist", scrolling when it does not fit.
        _miniText = new Marquee(12, FontWeights.SemiBold, _whiteBrush, maxWidth: Math.Round(MiniTextMaxWidth * WidthRatio));
        _miniState = Glyph(0xEC4F, 12, _accentBrush);
        _miniRow = Row();
        _miniRow.Children.Add(_miniArt);
        _miniRow.Children.Add(_miniText.Element);
        _miniRow.Children.Add(_miniState);
        _miniRow.Children.Add(MakeDots(6));

        _volumeIcon = Glyph(0xE995, 14, _whiteBrush);
        _volumeDone = new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) };
        _volumeLeft = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        Grid volumeBar = MakeBar(_volumeDone, _volumeLeft, _whiteBrush);
        volumeBar.Width = 120;
        _volumeText = Text(12, FontWeights.SemiBold, _whiteBrush);
        _volumeText.MinWidth = 28;
        _volumeText.TextAlignment = TextAlignment.Right;
        _volumeRow = Row();
        _volumeRow.Spacing = 10;
        _volumeRow.Children.Add(_volumeIcon);
        _volumeRow.Children.Add(volumeBar);
        _volumeRow.Children.Add(_volumeText);
        _volumeRow.Children.Add(MakeDots(6));

        _collapsedContent = new Grid
        {
            Width = _idleWidth,
            Height = CollapsedHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _collapsedContent.Children.Add(_idleRow);
        _collapsedContent.Children.Add(_miniRow);
        _collapsedContent.Children.Add(_volumeRow);
        _stage.Children.Add(_collapsedContent);

        // ---- music, open
        _appName = Text(12, FontWeights.Normal, _dimBrush);
        _appName.MaxWidth = 280;

        // Left, not centred: a capped width with the default stretch would centre it.
        _appName.HorizontalAlignment = HorizontalAlignment.Left;
        _toGeneral = MakeControl(0xE76C, Strings.DynToGeneral, () => ShowPage(general: true), out FontIcon toGeneralIcon);
        SmallArrow(_toGeneral, toGeneralIcon);
        var topRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        topRight.Children.Add(MakeDots(7));
        topRight.Children.Add(_toGeneral);
        var top = new Grid { Height = 28 };
        top.Children.Add(_appName);
        top.Children.Add(topRight);

        _noArtIcon = Glyph(0xEC4F, 22, _dimBrush);
        _noArtIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _bigArt = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(10),
            Background = _noArtBrush,
            Child = _noArtIcon,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _title = new Marquee(15, FontWeights.SemiBold, _whiteBrush);
        _artist = new Marquee(12, FontWeights.Normal, _dimBrush);
        var words = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
        };
        words.Children.Add(_title.Element);
        words.Children.Add(_artist.Element);

        _previous = MakeControl(0xE892, Strings.DynPrevious, () => _media!.PreviousAsync(), out _);
        _playPause = MakeControl(0xE768, Strings.DynPlayPause, () => _media!.TogglePlayPauseAsync(), out _playPauseIcon);
        _playPauseIcon.Foreground = _accentBrush;
        _playPauseIcon.FontSize = 20;
        _next = MakeControl(0xE893, Strings.DynNext, () => _media!.NextAsync(), out _);
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        controls.Children.Add(_previous);
        controls.Children.Add(_playPause);
        controls.Children.Add(_next);

        var mediaPanel = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        mediaPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mediaPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mediaPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(words, 1);
        Grid.SetColumn(controls, 2);
        mediaPanel.Children.Add(_bigArt);
        mediaPanel.Children.Add(words);
        mediaPanel.Children.Add(controls);

        _elapsed = Text(11, FontWeights.Normal, _dimBrush);
        _total = Text(11, FontWeights.Normal, _dimBrush);
        _doneColumn = new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) };
        _leftColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        Grid bar = MakeBar(_doneColumn, _leftColumn, _accentBrush);

        // The bar is thin; the area around it (the row's full height) takes the pointer.
        _seekThumb = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = _whiteBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _seekThumbMove,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _seekArea = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        _seekArea.Children.Add(bar);
        _seekArea.Children.Add(_seekThumb);
        AutomationProperties.SetName(_seekArea, Strings.DynPosition);
        _seekArea.PointerEntered += (_, _) =>
        {
            _seekHover = true;
            UpdateSeekThumb();
        };
        _seekArea.PointerExited += (_, _) =>
        {
            _seekHover = false;
            UpdateSeekThumb();
        };
        _seekArea.PointerPressed += (_, e) =>
        {
            // _media is set further down this constructor; the press comes later.
            if (_media?.Current.CanSeek != true)
            {
                return;
            }

            _seeking = _seekArea.CapturePointer(e.Pointer);
            PreviewSeek(e.GetCurrentPoint(_seekArea).Position.X);
            e.Handled = true;
        };
        _seekArea.PointerMoved += (_, e) =>
        {
            if (_seeking)
            {
                PreviewSeek(e.GetCurrentPoint(_seekArea).Position.X);
            }
        };
        _seekArea.PointerReleased += (_, e) =>
        {
            if (_seeking)
            {
                _seekArea.ReleasePointerCapture(e.Pointer);
                FinishSeek();
            }
        };
        _seekArea.PointerCaptureLost += (_, _) =>
        {
            if (_seeking)
            {
                FinishSeek();
            }
        };

        _timeline = new Grid { ColumnSpacing = 10, Height = 20 };
        _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_seekArea, 1);
        Grid.SetColumn(_total, 2);
        _timeline.Children.Add(_elapsed);
        _timeline.Children.Add(_seekArea);
        _timeline.Children.Add(_total);

        _mediaContent = new Grid
        {
            Width = _stageWidth,
            Height = OpenHeight,
            Padding = new Thickness(18, 6, 12, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _mediaContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _mediaContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _mediaContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _mediaContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(mediaPanel, 1);
        Grid.SetRow(_timeline, 3);
        _mediaContent.Children.Add(top);
        _mediaContent.Children.Add(mediaPanel);
        _mediaContent.Children.Add(_timeline);
        _stage.Children.Add(_mediaContent);

        // ---- in use now, open
        StackPanel privacyDots = MakeDots(8);
        privacyDots.HorizontalAlignment = HorizontalAlignment.Center;
        TextBlock privacyTitle = Text(11, FontWeights.Normal, _dimBrush);
        privacyTitle.Text = Strings.DynInUseNow;
        privacyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _privacyText = Text(13, FontWeights.SemiBold, _whiteBrush);
        _privacyText.HorizontalAlignment = HorizontalAlignment.Center;
        _privacyText.MaxWidth = PrivacyWidth - 36;
        var privacyStack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        privacyStack.Children.Add(privacyDots);
        privacyStack.Children.Add(privacyTitle);
        privacyStack.Children.Add(_privacyText);
        _privacyContent = new Grid
        {
            Width = PrivacyWidth,
            Height = PrivacyHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _privacyContent.Children.Add(privacyStack);
        _stage.Children.Add(_privacyContent);

        // ---- general page, open
        TextBlock usesTitle = Text(13, FontWeights.SemiBold, _whiteBrush);
        usesTitle.Text = Strings.DynInUseNow;
        _toMedia = MakeControl(0xE76B, Strings.DynBackToMusic, () => ShowPage(general: false), out FontIcon toMediaIcon);
        SmallArrow(_toMedia, toMediaIcon);
        _toMedia.HorizontalAlignment = HorizontalAlignment.Right;
        var generalTop = new Grid { Height = 28 };
        generalTop.Children.Add(usesTitle);
        generalTop.Children.Add(_toMedia);

        _useRows = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        _noUses = Text(12, FontWeights.Normal, _dimBrush);
        _noUses.Text = Strings.DynNothingInUse;
        _noUses.Margin = new Thickness(0, 2, 0, 0);

        TextBlock notificationsTitle = Text(13, FontWeights.SemiBold, _whiteBrush);
        notificationsTitle.Text = Strings.DynNotifications;
        var notifications = new Button
        {
            Content = Strings.DynOpenNotifications,
            Height = 32,
            Margin = new Thickness(0, 6, 6, 0),
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(Radii.Small),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(notifications, Strings.DynOpenNotifications);
        notifications.Click += (_, _) => OpenNotifications();
        // Windows' tray icons (see TrayIconService); only while there are some.
        TextBlock trayTitle = Text(13, FontWeights.SemiBold, _whiteBrush);
        trayTitle.Text = Strings.DynTray;
        trayTitle.Height = TrayTitleHeight;
        trayTitle.Padding = new Thickness(0, 4, 0, 0);
        _trayRows = new StackPanel { Spacing = TrayRowHeight - TrayTile };
        _trayMarker = new Border
        {
            Width = 3,
            Height = TrayTile - 8,
            CornerRadius = new CornerRadius(1.5),
            Background = _accentBrush,
            Visibility = Visibility.Collapsed,
        };
        _trayOverlay = new Canvas { IsHitTestVisible = false };
        _trayOverlay.Children.Add(_trayMarker);
        _trayArea = new Grid();
        _trayArea.Children.Add(_trayRows);
        _trayArea.Children.Add(_trayOverlay);
        _traySection = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        _traySection.Children.Add(trayTitle);
        _traySection.Children.Add(_trayArea);

        var notificationsPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        notificationsPanel.Children.Add(notificationsTitle);
        notificationsPanel.Children.Add(notifications);

        _generalContent = new Grid
        {
            Width = _stageWidth,
            Height = OpenHeight,
            Padding = new Thickness(18, 6, 12, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _generalContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _generalContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _generalContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _generalContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_useRows, 1);
        Grid.SetRow(_noUses, 1);
        Grid.SetRow(_traySection, 2);
        Grid.SetRow(notificationsPanel, 3);
        _generalContent.Children.Add(generalTop);
        _generalContent.Children.Add(_useRows);
        _generalContent.Children.Add(_noUses);
        _generalContent.Children.Add(_traySection);
        _generalContent.Children.Add(notificationsPanel);
        _stage.Children.Add(_generalContent);

        // ---- the pill (Composition)
        _collapsedVisual = ElementCompositionPreview.GetElementVisual(_collapsedContent);
        _mediaVisual = ElementCompositionPreview.GetElementVisual(_mediaContent);
        _privacyVisual = ElementCompositionPreview.GetElementVisual(_privacyContent);
        _generalVisual = ElementCompositionPreview.GetElementVisual(_generalContent);
        _compositor = _collapsedVisual.Compositor;
        _geometry = _compositor.CreateRoundedRectangleGeometry();
        _geometry.CornerRadius = new Vector2((float)CollapsedRadius);
        _geometry.Size = new Vector2((float)_idleWidth, (float)(CollapsedHeight + TopExtension));
        _geometry.Offset = new Vector2((float)((_stageWidth - _idleWidth) / 2), (float)-TopExtension);
        _shape = _compositor.CreateSpriteShape(_geometry);
        _shape.FillBrush = _compositor.CreateColorBrush(Colors.Black);
        _shape.StrokeBrush = _compositor.CreateColorBrush(ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        _shape.StrokeThickness = 1;
        _shapeVisual = _compositor.CreateShapeVisual();
        _shapeVisual.Size = new Vector2((float)_stageWidth, (float)StageHeight);
        _shapeVisual.Shapes.Add(_shape);
        _pillHost = _compositor.CreateContainerVisual();
        _pillHost.Children.InsertAtTop(_shapeVisual);
        ElementCompositionPreview.SetElementChildVisual(_pillLayer, _pillHost);
        AddEars();

        _ease = MakeEase(_compositor);
        _sizeAnimation = _compositor.CreateVector2KeyFrameAnimation();
        _offsetAnimation = _compositor.CreateVector2KeyFrameAnimation();
        _radiusAnimation = _compositor.CreateVector2KeyFrameAnimation();
        _collapsedFade = _compositor.CreateScalarKeyFrameAnimation();
        _mediaFade = _compositor.CreateScalarKeyFrameAnimation();
        _privacyFade = _compositor.CreateScalarKeyFrameAnimation();
        _generalFade = _compositor.CreateScalarKeyFrameAnimation();
        _mediaVisual.Opacity = 0;
        _privacyVisual.Opacity = 0;
        _generalVisual.Opacity = 0;

        _cutter = new WindowCutter(_hwnd, _canvas);
        ApplySize();
        // The cut's corners are the notch's (the smaller ones): never rounder than the pill drawn.
        _cutter.Add(_collapsedHit, CollapsedRadius);
        _cutter.Add(_expandedHit, MediaRadius);
        _cutter.Add(_earHit, 0);
        _root.LayoutUpdated += (_, _) => _cutter.Update();

        // ---- behaviour
        _root.PointerEntered += (_, _) =>
        {
            _pointerInside = true;
            _leaveTimer!.Stop();
            if (!_hovering)
            {
                _hoverTimer!.Start();
            }

            _cursorTimer!.Start();
        };
        _root.PointerExited += (_, _) =>
        {
            // Whether the pointer really left (or went on to the centre below) is decided by
            // CheckCursor, which sees both windows.
            _pointerInside = false;
            _hoverTimer!.Stop();
        };
        _root.Tapped += (_, e) =>
        {
            // A click is a quick way in: the hover result at once.
            if (!_hovering && !IsInsideButton(e.OriginalSource as DependencyObject))
            {
                _hoverTimer!.Stop();
                StartHover();
            }
        };

        _heal = MakeTimer(TimeSpan.FromSeconds(2), repeating: true, Heal);
        _tick = MakeTimer(TimeSpan.FromMilliseconds(500), repeating: true, UpdateTimeline);
        _hoverTimer = MakeTimer(TimeSpan.FromMilliseconds(140), repeating: false, StartHover);
        _leaveTimer = MakeTimer(TimeSpan.FromMilliseconds(380), repeating: false, EndHover);
        _cursorTimer = MakeTimer(TimeSpan.FromMilliseconds(120), repeating: true, CheckCursor);
        _regionTimer = MakeTimer(TimeSpan.FromMilliseconds(AnimateMs + 60), repeating: false, SettleRegion);
        _peekTimer = MakeTimer(TimeSpan.FromMilliseconds(PeekMs), repeating: false, () =>
        {
            _peek = PillShape.Collapsed;
            Refresh();
        });
        _volumeTimer = MakeTimer(TimeSpan.FromMilliseconds(VolumeShowMs + 20), repeating: false, Refresh);
        _volumePoll = MakeTimer(TimeSpan.FromMilliseconds(250), repeating: true, PollVolume);

        // The tray icons arrive from their apps (TrayIconService), live; started with Dynaland.
        _tray.Changed += FillTray;
        _host.BarLayout.Changed += FillTray;
        _trayCloseTimer = MakeTimer(TimeSpan.FromMilliseconds(_doubleClickMs), repeating: false, () =>
        {
            _lastTrayKey = null;
            EndHover();
        });

        _media = new MediaService(DispatcherQueue, AppNameFor);
        _media.Changed += ApplyMedia;
        _privacy = new PrivacyMonitor(DispatcherQueue);
        _privacy.Changed += OnPrivacyChanged;

        _host.Settings.Changed += OnSettingsChanged;
        _host.FullScreenChanged += OnFullScreenChanged;
        _host.Custom.Changed += OnCustomChanged;
        Closed += (_, _) =>
        {
            _host.Custom.Changed -= OnCustomChanged;
            _closed = true;
            foreach (DispatcherQueueTimer timer in new[]
                     {
                         _heal, _tick, _hoverTimer, _leaveTimer, _cursorTimer, _regionTimer, _peekTimer, _volumeTimer, _volumePoll,
                         _trayCloseTimer,
                     })
            {
                timer.Stop();
            }

            _host.Settings.Changed -= OnSettingsChanged;
            _host.FullScreenChanged -= OnFullScreenChanged;
            _media.Changed -= ApplyMedia;
            _media.Dispose();
            _privacy.Changed -= OnPrivacyChanged;
            _privacy.Dispose();
            _tray.Changed -= FillTray;
            _host.BarLayout.Changed -= FillTray;
            _subclass?.Dispose();
            _subclass = null;
        };

        UpdateDots();
        FillUses();
        ApplyLook(EffectiveStyle(), !_host.DynalandOnTop());
        ApplyMedia();
    }

    private enum PillShape
    {
        Collapsed,
        Media,
        Privacy,
        General,
    }

    private enum CollapsedForm
    {
        Idle,
        Media,
        Volume,
    }

    /// <summary>True while on screen.</summary>
    internal bool IsShown => _shown;

    /// <summary>Where the pill is on screen now (physical pixels), for custom mode's badge; null when hidden.</summary>
    internal RECT? BadgeRect => _shown && !_closed ? PillScreenRect(_pill) : null;

    /// <summary>The title of what plays now, or null.</summary>
    internal string? NowPlayingTitle => _media.Current.HasMedia ? _media.Current.Title : null;

    /// <summary>Custom mode on: a see-through cover takes Dynaland's clicks (pick) and right-clicks (black menu).</summary>
    private void OnCustomChanged()
    {
        if (_closed)
        {
            return;
        }

        bool on = _host.Custom.IsOn;
        if (on && _customShield is null)
        {
            if (_hovering)
            {
                EndHover();
            }

            var shield = new Border { Background = new SolidColorBrush(Colors.Transparent) };
            shield.Tapped += (_, e) =>
            {
                e.Handled = true;
                _host.Custom.Select(new CustomTarget(CustomPiece.Dynaland));
            };
            shield.RightTapped += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    _customMenu ??= new MenuFlyout { ShouldConstrainToRootBounds = false };
                    CustomMenus.Fill(_customMenu, _host.Custom, new CustomTarget(CustomPiece.Dynaland));
                    User32.SetForegroundWindow(_hwnd);
                    _customMenu.ShowAt(shield, new FlyoutShowOptions { Position = e.GetPosition(shield) });
                }
                catch (Exception ex)
                {
                    Log.Error("Dynaland: the custom mode menu could not be shown.", ex);
                }
            };
            _customShield = shield;
            _root.Children.Add(shield);
        }
        else if (!on && _customShield is not null)
        {
            _root.Children.Remove(_customShield);
            _customShield = null;
        }
    }

    /// <summary>
    /// The span (physical pixels) the small pill can take while Dynaland sits in the bar's own
    /// band (on the bar's edge, the bar aligned to a side); null otherwise or while hidden.
    /// </summary>
    internal (int Left, int Right)? ZoneInBarBand { get; private set; }

    /// <summary>Height the top strip keeps for Dynaland alone: the collapsed pill (and the island's gap) plus a margin, DIP.</summary>
    internal double StripNeedDip => ((_gap + CollapsedHeight) * _size) + 8;

    /// <summary>True when Dynaland is meant to be on screen (on); stays true while a full-screen app hides it.</summary>
    internal bool WantsSpace => _started && !_closed && _host.Settings.Current.EnableDynaland;

    /// <summary>Back on top of the other always-on-top windows (above the top strip cover).</summary>
    internal void KeepOnTop()
    {
        if (_shown && !_closed)
        {
            User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    internal void Start()
    {
        if (_started || _closed)
        {
            return;
        }

        _started = true;
        UpdateVisibility();
        _heal.Start();
        FillTray();
    }

    /// <summary>Hides Dynaland for good (shutdown). Close it afterwards.</summary>
    internal void Stop()
    {
        _started = false;
        _heal.Stop();
        UpdateVisibility();
    }

    /// <summary>
    /// Centred at the top of the bar's screen (below the bar when the bar is docked at the top),
    /// the window's 4 DIP pad above the screen edge so the pill touches it. Moves only when needed.
    /// </summary>
    internal void Place()
    {
        if (!_shown || _closed)
        {
            ZoneInBarBand = null;
            return;
        }

        try
        {
            nint bar = _host.BarHandle;
            nint near = bar != 0 ? bar : _hwnd;
            if (!User32.TryGetMonitorRect(near, out RECT monitor))
            {
                return;
            }

            uint dpi = User32.GetDpiForWindow(near);
            double scale = (dpi == 0 ? 96 : dpi) / 96.0;

            // Dynaland's own sizes scale with its size setting; the margins to the bar do not.
            double sized = scale * _size;
            int width = (int)Math.Round(DesignWidth * sized);
            int height = (int)Math.Round(DesignHeight * sized);
            int pad = (int)Math.Round(Pad * sized);

            // Dynaland's edge: the chosen one, or (Auto) the one without the bar. A bar on the same
            // edge that leaves the centre free (aligned left or right) is no obstacle: Dynaland
            // stays at the screen's edge, between. When the bar covers the centre, Dynaland
            // floats just past it, as an island (a notch cannot hang from the bar).
            int x = monitor.Left + ((monitor.Width - width) / 2);
            bool atBottom = !_host.DynalandOnTop();
            RECT barRect = _host.BarRect;
            int clearance = (int)Math.Round(8 * scale);
            bool barHere = _host.BarOnEdge(top: !atBottom);
            bool pastBar = barHere
                && barRect.Left < x + width - pad + clearance
                && barRect.Right > x + pad - clearance;
            if (atBottom != _atBottom || pastBar != _pastBar)
            {
                _pastBar = pastBar;
                Log.Info("Dynaland: " + (pastBar
                    ? "the bar covers the " + (atBottom ? "bottom" : "top") + " centre; Dynaland floats " + (atBottom ? "above" : "below") + " it as an island."
                    : "at the " + (atBottom ? "bottom" : "top") + " edge of the screen."));
                ApplyLook(EffectiveStyle(), atBottom);
            }

            int centre = x + (width / 2);
            int zoneHalf = (int)Math.Round(((_maxCollapsedWidth / 2) + EarRadius) * sized);
            ZoneInBarBand = barHere && !pastBar ? (centre - zoneHalf, centre + zoneHalf) : null;

            int y;
            if (atBottom)
            {
                int bottomEdge = pastBar ? Math.Min(monitor.Bottom, barRect.Top) : monitor.Bottom;
                y = bottomEdge + pad - height;
            }
            else
            {
                int topEdge = pastBar ? Math.Max(monitor.Top, barRect.Bottom) : monitor.Top;
                y = topEdge - pad;
            }
            var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
            if (rect.SameAs(_placed))
            {
                return;
            }

            _placed = rect;
            User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, width, height, User32.SWP_NOACTIVATE);
            _cutter.Invalidate();
        }
        catch (Exception ex)
        {
            Log.Error("Dynaland: placing it failed.", ex);
        }
    }

    /// <summary>
    /// The notch's two outward curves: a small black square at each top corner of the pill with
    /// a quarter circle cut out, so the sides bend out into the screen's top edge. Each follows
    /// the pill's left or right edge through every animation (an expression on the pill's shape),
    /// and overlaps the pill by 1 DIP so the pill's faint outline does not show between them.
    /// If Windows refuses, the notch keeps square top corners and says so in the log.
    /// </summary>
    private void AddEars()
    {
        try
        {
            _leftEar = MakeEar(left: true);
            _rightEar = MakeEar(left: false);
            _leftEarFollow = _compositor.CreateExpressionAnimation("Vector3(geo.Offset.X - r, y, 0)");
            _leftEarFollow.SetReferenceParameter("geo", _geometry);
            _leftEarFollow.SetScalarParameter("r", (float)EarRadius);
            _leftEarFollow.SetScalarParameter("y", 0);
            _rightEarFollow = _compositor.CreateExpressionAnimation("Vector3(geo.Offset.X + geo.Size.X - 1, y, 0)");
            _rightEarFollow.SetReferenceParameter("geo", _geometry);
            _rightEarFollow.SetScalarParameter("y", 0);
            _pillHost.Children.InsertAtTop(_leftEar);
            _pillHost.Children.InsertAtTop(_rightEar);
            _leftEar.StartAnimation("Offset", _leftEarFollow);
            _rightEar.StartAnimation("Offset", _rightEarFollow);
        }
        catch (Exception ex)
        {
            Log.Warn("Dynaland: the notch's curved top corners could not be drawn; they stay square.", ex);
            if (_leftEar is not null)
            {
                _pillHost.Children.Remove(_leftEar);
            }

            if (_rightEar is not null)
            {
                _pillHost.Children.Remove(_rightEar);
            }

            _leftEar = null;
            _rightEar = null;
            _earHit.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// One curve, r + 1 wide and r tall: a thick ring (inner radius r) around the corner it
    /// bends towards fills everything outside the quarter circle; the visual clips the rest.
    /// A thin ring at radius r draws the pill's faint outline along the curve.
    /// </summary>
    private ShapeVisual MakeEar(bool left)
    {
        float r = (float)EarRadius;
        var centre = new Vector2(left ? 0 : r + 1, r);

        ShapeVisual visual = _compositor.CreateShapeVisual();
        visual.Size = new Vector2(r + 1, r);
        visual.Clip = _compositor.CreateInsetClip();

        CompositionEllipseGeometry band = _compositor.CreateEllipseGeometry();
        band.Center = centre;
        _earCircles.Add((band, left));
        band.Radius = new Vector2(1.5f * r);
        CompositionSpriteShape fill = _compositor.CreateSpriteShape(band);
        fill.StrokeBrush = _shape.FillBrush;
        fill.StrokeThickness = r;
        visual.Shapes.Add(fill);

        CompositionEllipseGeometry line = _compositor.CreateEllipseGeometry();
        line.Center = centre;
        _earCircles.Add((line, left));
        line.Radius = new Vector2(r);
        CompositionSpriteShape outline = _compositor.CreateSpriteShape(line);
        outline.StrokeBrush = _shape.StrokeBrush;
        outline.StrokeThickness = 1;
        visual.Shapes.Add(outline);
        return visual;
    }

    private static Border MakeOutline(double width, double height)
    {
        // Notch at the top: reaches past the edge, so the cut is square there (ApplyLook sets the others).
        return new Border
        {
            Width = width,
            Height = height + TopExtension,
            Margin = new Thickness(0, -TopExtension, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
    }

    private static StackPanel Row()
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
    }

    private static Grid MakeBar(ColumnDefinition done, ColumnDefinition left, Brush fill)
    {
        var bar = new Grid { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(done);
        bar.ColumnDefinitions.Add(left);
        var rest = new Border { Background = new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) };
        Grid.SetColumn(rest, 1);
        bar.Children.Add(rest);
        bar.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(2) });
        return bar;
    }

    private static TextBlock Text(double size, Windows.UI.Text.FontWeight weight, Brush foreground)
    {
        return new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static FontIcon Glyph(int codePoint, double size, Brush foreground)
    {
        return new FontIcon
        {
            Glyph = ((char)codePoint).ToString(),
            FontFamily = IconFont,
            FontSize = size,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static CompositionEasingFunction MakeEase(Compositor compositor)
    {
        try
        {
            // A little past the end and back: the springy feel of a notch opening.
            return compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1.08f));
        }
        catch (Exception)
        {
            return compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
        }
    }

    private static void Show(UIElement element, bool show)
    {
        Visibility visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility)
        {
            element.Visibility = visibility;
        }
    }

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text)
        {
            block.Text = text;
        }
    }

    private static string Format(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + time.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : ((int)time.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + time.ToString(@"ss", CultureInfo.InvariantCulture);
    }

    private static void Fade(Visual visual, ScalarKeyFrameAnimation animation, float to, int delayMs, int durationMs)
    {
        animation.InsertKeyFrame(1f, to);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        animation.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        visual.StartAnimation("Opacity", animation);
    }

    private (double Width, double Height, double Radius) Dimensions(PillShape shape, double collapsedWidth)
    {
        return shape switch
        {
            PillShape.Media => (_stageWidth, OpenHeight, _openRadius),
            PillShape.General => (_stageWidth, _generalHeight, _openRadius),
            PillShape.Privacy => (PrivacyWidth, PrivacyHeight, _privacyRadius),
            _ => (collapsedWidth, CollapsedHeight, _collapsedRadius),
        };
    }

    private bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, _root))
        {
            if (source is ButtonBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>A green camera dot and an orange mic dot, each shown only while that device is in use.</summary>
    private StackPanel MakeDots(double size)
    {
        var camera = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(CameraGreen), Visibility = Visibility.Collapsed };
        var mic = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(MicOrange), Visibility = Visibility.Collapsed };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = size - 1,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(camera);
        panel.Children.Add(mic);
        _dotSets.Add((panel, camera, mic));
        return panel;
    }

    private Button MakeControl(int codePoint, string name, Func<Task<bool>> action, out FontIcon icon)
    {
        icon = Glyph(codePoint, 16, _whiteBrush);
        var button = new Button
        {
            Content = icon,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        AutomationProperties.SetName(button, name);
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Error("Dynaland: \"" + name + "\" failed.", ex);
            }
        };
        return button;
    }

    private DispatcherQueueTimer MakeTimer(TimeSpan interval, bool repeating, Action tick)
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += (_, _) =>
        {
            if (_closed)
            {
                return;
            }

            try
            {
                tick();
            }
            catch (Exception ex)
            {
                Log.Error("Dynaland: an update failed.", ex);
            }
        };
        return timer;
    }

    // ---------------------------------------------------------------- showing and hiding

    private void UpdateVisibility()
    {
        bool want = _started && !_closed && _host.Settings.Current.EnableDynaland && !_host.FullScreenAppInFront;
        if (want == _shown)
        {
            return;
        }

        _shown = want;
        if (want)
        {
            if (!_servicesStarted)
            {
                _servicesStarted = true;
                _media.Start();
                _privacy.Start();
            }

            _volumeKnown = false;
            _volumeTicks = 0;
            _volumePoll.Start();
            _placed = default;
            Place();
            AppWindow.Show(false);
            User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            Refresh();
        }
        else
        {
            _volumePoll.Stop();
            CollapseNow();
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
        }

        // The top strip follows whether Dynaland is on.
        _host.LayoutCorners();
    }

    private void Heal()
    {
        UpdateVisibility();
        _host.LayoutCorners();

        // Not over an app's menu opened from a tray tile.
        if (_shown && !MenuHeld())
        {
            User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    private void OnSettingsChanged()
    {
        if (_closed)
        {
            return;
        }

        ApplySize();
        ApplyWidth();
        ApplyLook(EffectiveStyle(), !_host.DynalandOnTop());
        UpdateVisibility();
        FillTray();
        UpdateDots();
        _placed = default;
        _host.LayoutCorners();
        Refresh();
    }

    /// <summary>The size setting: Dynaland scaled as a whole (window, pill, pages, cut-out).</summary>
    private void ApplySize()
    {
        double size = _host.Settings.Current.DynalandSize / 100.0;
        if (size == _size)
        {
            return;
        }

        _size = size;
        _zoom.ScaleX = size;
        _zoom.ScaleY = size;
        _cutter.RadiusScale = size;
        _cutter.Invalidate();
        _placed = default;
    }

    private double WidthSetting()
    {
        return Math.Clamp(_host.Settings.Current.DynalandWidth, ShellSettings.MinDynalandWidth, ShellSettings.MaxDynalandWidth);
    }

    /// <summary>The width setting against the standard 400 DIP (0.9 to 1.4).</summary>
    private double WidthRatio => WidthSetting() / StandardStageWidth;

    /// <summary>
    /// The width setting: the open pages, the stage, the window and the pill's centre follow,
    /// and so does the closed pill (its width with nothing playing, and how far it may grow
    /// with music), live; the open pages show it on the next hover.
    /// </summary>
    private void ApplyWidth()
    {
        double width = WidthSetting();
        if (width == _stageWidth)
        {
            return;
        }

        _stageWidth = width;
        _maxCollapsedWidth = Math.Round(MaxCollapsedWidth * WidthRatio);
        _idleWidth = Math.Round(IdleWidth * WidthRatio);
        _miniText.SetMaxWidth(Math.Round(MiniTextMaxWidth * WidthRatio));
        _root.Width = DesignWidth;
        _stage.Width = width;
        _expandedHit.Width = width;
        _mediaContent.Width = width;
        _generalContent.Width = width;
        _shapeVisual.Size = new Vector2((float)width, (float)StageHeight);

        // The tray rows hold as many tiles as fit the new width.
        _trayShownKeys = new List<string>();
        FillTray();

        // The pill keeps its shape, centred on the new stage.
        (double w, double h, double r) = Dimensions(_pill, _collapsedWidth);
        AnimatePill(w, h, r);
        SettleRegion();
        _cutter.Invalidate();
        _placed = default;
        Log.Info("Dynaland: width " + width.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            + " DIP; closed " + _idleWidth.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            + " DIP, with music up to " + _maxCollapsedWidth.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " DIP.");

        // The closed pill takes its new width at once (Refresh animates it).
        Refresh();
    }

    /// <summary>The shape in use: the chosen one, or the island while Dynaland floats below a top bar.</summary>
    private DynalandStyle EffectiveStyle()
    {
        return _pastBar ? DynalandStyle.Island : _host.Settings.Current.DynalandStyle;
    }

    /// <summary>
    /// Notch (default): hanging from the top edge (the pill and the cut run past the edge), its
    /// sides curving out into the edge at the top, round bottom corners. Island (iPhone-inspired): floating 8 DIP below the edge,
    /// round all around: a full capsule collapsed, large round corners open. Applied at once,
    /// without animating; the window keeps its size (it always has room for the gap).
    /// </summary>
    private void ApplyLook(DynalandStyle style, bool atBottom)
    {
        if (style == _style && atBottom == _atBottom)
        {
            return;
        }

        _style = style;
        _atBottom = atBottom;
        bool island = style == DynalandStyle.Island;
        _gap = island ? IslandGap : 0;
        _topExtension = island ? 0 : TopExtension;
        _collapsedRadius = island ? CollapsedHeight / 2 : CollapsedRadius;
        _openRadius = island ? IslandOpenRadius : MediaRadius;
        _privacyRadius = island ? IslandPrivacyRadius : PrivacyRadius;

        // At the bottom everything is mirrored: the stage, the small views and the outlines sit
        // at the bottom, and the notch reaches past the bottom edge instead of the top one.
        VerticalAlignment toEdge = atBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        _stage.VerticalAlignment = toEdge;
        _stage.Margin = atBottom ? new Thickness(0, 0, 0, _gap) : new Thickness(0, _gap, 0, 0);
        _collapsedContent.VerticalAlignment = toEdge;
        _privacyContent.VerticalAlignment = toEdge;
        _mediaContent.VerticalAlignment = toEdge;
        _generalContent.VerticalAlignment = toEdge;

        // The island's corners are all round, so the open views keep their rows clear of them;
        // the notch's square side is at the screen's edge, so less room is needed there.
        Thickness open = island
            ? new Thickness(20, 10, 14, 10)
            : atBottom ? new Thickness(18, 14, 12, 6) : new Thickness(18, 6, 12, 14);
        _mediaContent.Padding = open;
        _generalContent.Padding = open;
        if (_leftEar is not null && _rightEar is not null && _leftEarFollow is not null && _rightEarFollow is not null)
        {
            _leftEar.IsVisible = !island;
            _rightEar.IsVisible = !island;
            float r = (float)EarRadius;
            foreach ((CompositionEllipseGeometry circle, bool leftEar) in _earCircles)
            {
                circle.Center = new Vector2(leftEar ? 0 : r + 1, atBottom ? 0 : r);
            }

            float earTop = atBottom ? (float)(StageHeight - EarRadius) : 0;
            _leftEarFollow.SetScalarParameter("y", earTop);
            _rightEarFollow.SetScalarParameter("y", earTop);
            _leftEar.StartAnimation("Offset", _leftEarFollow);
            _rightEar.StartAnimation("Offset", _rightEarFollow);
        }

        Show(_earHit, !island && _leftEar is not null);
        _earHit.VerticalAlignment = toEdge;
        Thickness reach = atBottom ? new Thickness(0, 0, 0, -_topExtension) : new Thickness(0, -_topExtension, 0, 0);
        _collapsedHit.VerticalAlignment = toEdge;
        _collapsedHit.Margin = reach;
        _collapsedHit.Height = CollapsedHeight + _topExtension;
        _expandedHit.VerticalAlignment = toEdge;
        _expandedHit.Margin = reach;

        foreach (string property in new[] { "Size", "Offset", "CornerRadius" })
        {
            _geometry.StopAnimation(property);
        }

        (double w, double h, double radius) = Dimensions(_pill, _collapsedWidth);
        _geometry.Size = new Vector2((float)w, (float)(h + _topExtension));
        _geometry.Offset = PillOffset(w, h);
        _geometry.CornerRadius = new Vector2((float)radius);
        SettleRegion();
        _cutter.Invalidate();
        Log.Info("Dynaland: " + (island ? "island" : "notch") + " shape, " + (atBottom ? "bottom" : "top") + " edge.");
    }

    /// <summary>
    /// Where the pill's shape starts in the stage for a pill of this size: centred, and at the
    /// stage's top (reaching past it by the notch's extension) or, at the bottom edge, at its bottom.
    /// </summary>
    private Vector2 PillOffset(double width, double height)
    {
        float y = _atBottom ? (float)(StageHeight - height) : (float)-_topExtension;
        return new Vector2((float)((_stageWidth - width) / 2), y);
    }

    private void OnFullScreenChanged()
    {
        if (!_closed)
        {
            UpdateVisibility();
        }
    }

    private bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        if (message == User32.WM_DISPLAYCHANGE
            || message == User32.WM_DPICHANGED
            || (message == User32.WM_SETTINGCHANGE && wParam == (nint)User32.SPI_SETWORKAREA))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _placed = default;
                Place();
            });
        }

        return SeeThrough.TryHandleErase(_hwnd, message, wParam, out result);
    }

    // ---------------------------------------------------------------- what the pill shows

    private bool PrivacyShown => _host.Settings.Current.ShowPrivacyDots && _privacy.Current.Any;

    /// <summary>
    /// Hovered: open, on the player while music plays (unless the arrow went to the general
    /// page), else on the general page. Not hovered: open only for a moment for live things
    /// (a new track, a new camera / microphone use).
    /// </summary>
    private PillShape DesiredShape()
    {
        if (!_shown)
        {
            return PillShape.Collapsed;
        }

        bool media = _media.Current.HasMedia;
        bool privacy = PrivacyShown;
        if (_hovering)
        {
            return media && !_onGeneralPage ? PillShape.Media : PillShape.General;
        }

        if (_peek == PillShape.Media && media)
        {
            return PillShape.Media;
        }

        if (_peek == PillShape.Privacy && privacy)
        {
            return PillShape.Privacy;
        }

        return PillShape.Collapsed;
    }

    private CollapsedForm DesiredForm()
    {
        if (_volumeKnown && Environment.TickCount64 < _volumeUntil)
        {
            return CollapsedForm.Volume;
        }

        return _media.Current.HasMedia ? CollapsedForm.Media : CollapsedForm.Idle;
    }

    /// <summary>Brings the pill to what it should show now, animating when that changed.</summary>
    private void Refresh()
    {
        if (_closed)
        {
            return;
        }

        CollapsedForm form = DesiredForm();
        Show(_idleRow, form == CollapsedForm.Idle);
        Show(_miniRow, form == CollapsedForm.Media);
        Show(_volumeRow, form == CollapsedForm.Volume);

        StackPanel row = form switch
        {
            CollapsedForm.Media => _miniRow,
            CollapsedForm.Volume => _volumeRow,
            _ => _idleRow,
        };
        row.Measure(new Windows.Foundation.Size(double.PositiveInfinity, CollapsedHeight));
        double minimum = form == CollapsedForm.Idle ? _idleWidth : MinBusyWidth;
        double width = Math.Clamp(Math.Ceiling(row.DesiredSize.Width) + (2 * CollapsedSidePad), minimum, _maxCollapsedWidth);
        double oldWidth = _collapsedWidth;
        bool widthChanged = Math.Abs(width - oldWidth) >= 0.5;
        _collapsedWidth = width;
        _collapsedContent.Width = width;

        PillShape shape = DesiredShape();
        PillShape old = _pill;
        if (shape != old)
        {
            _pill = shape;
            HoldRegion(oldWidth);
            (double w, double h, double r) = Dimensions(shape, width);
            AnimatePill(w, h, r);
            Fade(VisualOf(old), FadeOf(old), 0f, 0, 110);
            Fade(VisualOf(shape), FadeOf(shape), 1f, 90, 200);
            _mediaContent.IsHitTestVisible = shape == PillShape.Media;
            _generalContent.IsHitTestVisible = shape == PillShape.General;
            if (shape == PillShape.Media)
            {
                UpdateTimeline();
            }
        }
        else if (shape == PillShape.Collapsed && widthChanged)
        {
            HoldRegion(oldWidth);
            AnimatePill(width, CollapsedHeight, _collapsedRadius);
        }

        if (_pill == PillShape.Media)
        {
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }

        if (!_hovering && !_pointerInside)
        {
            _cursorTimer.Stop();
        }
    }

    private Visual VisualOf(PillShape shape) => shape switch
    {
        PillShape.Media => _mediaVisual,
        PillShape.Privacy => _privacyVisual,
        PillShape.General => _generalVisual,
        _ => _collapsedVisual,
    };

    private ScalarKeyFrameAnimation FadeOf(PillShape shape) => shape switch
    {
        PillShape.Media => _mediaFade,
        PillShape.Privacy => _privacyFade,
        PillShape.General => _generalFade,
        _ => _collapsedFade,
    };

    /// <summary>During an animation the window keeps both where the pill was and where it goes.</summary>
    private void HoldRegion(double previousCollapsedWidth)
    {
        _collapsedHit.Width = Math.Max(_collapsedHit.Width, Math.Max(previousCollapsedWidth, _collapsedWidth));
        (double w, double h, _) = Dimensions(_pill, _collapsedWidth);
        if (_pill != PillShape.Collapsed)
        {
            bool shown = _expandedHit.Visibility == Visibility.Visible;
            _expandedHit.Width = Math.Max(shown ? _expandedHit.Width : 0, w);
            _expandedHit.Height = Math.Max(shown ? _expandedHit.Height : 0, h + _topExtension);
            Show(_expandedHit, true);
        }

        UpdateEarHit();
        _regionTimer.Stop();
        _regionTimer.Start();
    }

    /// <summary>The notch's top band: as wide as the widest outline, plus a curve on each side.</summary>
    private void UpdateEarHit()
    {
        double widest = Math.Max(_collapsedHit.Width, _expandedHit.Visibility == Visibility.Visible ? _expandedHit.Width : 0);
        _earHit.Width = widest + (2 * EarRadius);
    }

    /// <summary>After an animation: the window's outline matches the pill again.</summary>
    private void SettleRegion()
    {
        _collapsedHit.Width = _collapsedWidth;
        if (_pill == PillShape.Collapsed)
        {
            Show(_expandedHit, false);
            UpdateEarHit();
            return;
        }

        (double w, double h, _) = Dimensions(_pill, _collapsedWidth);
        _expandedHit.Width = w;
        _expandedHit.Height = h + _topExtension;
        Show(_expandedHit, true);
        UpdateEarHit();
    }

    /// <summary>Back to collapsed without animating (when hiding).</summary>
    private void CollapseNow()
    {
        foreach (DispatcherQueueTimer timer in new[] { _hoverTimer, _leaveTimer, _cursorTimer, _regionTimer, _peekTimer, _tick })
        {
            timer.Stop();
        }

        _hovering = false;
        _pointerInside = false;
        _peek = PillShape.Collapsed;
        _pill = PillShape.Collapsed;
        _onGeneralPage = false;
        _mediaContent.IsHitTestVisible = false;
        _generalContent.IsHitTestVisible = false;
        foreach (string property in new[] { "Size", "Offset", "CornerRadius" })
        {
            _geometry.StopAnimation(property);
        }

        _geometry.Size = new Vector2((float)_collapsedWidth, (float)(CollapsedHeight + _topExtension));
        _geometry.Offset = PillOffset(_collapsedWidth, CollapsedHeight);
        _geometry.CornerRadius = new Vector2((float)_collapsedRadius);
        foreach (Visual visual in new[] { _collapsedVisual, _mediaVisual, _privacyVisual, _generalVisual })
        {
            visual.StopAnimation("Opacity");
        }

        _collapsedVisual.Opacity = 1;
        _mediaVisual.Opacity = 0;
        _privacyVisual.Opacity = 0;
        _generalVisual.Opacity = 0;
        SettleRegion();
    }

    /// <summary>While hovering: notices that the pointer left the pill (a missed exit event too); 380 ms later it closes.</summary>
    private void CheckCursor()
    {
        if (!_hovering && !_pointerInside)
        {
            _cursorTimer.Stop();
            return;
        }

        if (_hovering && (_seeking || _trayDragKey is not null || MenuHeld()))
        {
            // A drag on the seek bar, or an app's menu from a tray tile, keeps Dynaland open.
            _leaveTimer.Stop();
            return;
        }

        if (!User32.GetCursorPos(out POINT point))
        {
            return;
        }

        if (Contains(PillScreenRect(_pill), point))
        {
            _leaveTimer.Stop();
        }
        else
        {
            _pointerInside = false;
            _hoverTimer.Stop();
            if (_hovering && !_leaveTimer.IsRunning)
            {
                _leaveTimer.Start();
            }
        }
    }

    private static bool Contains(RECT rect, POINT point)
    {
        return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    /// <summary>Where the pill of the given shape is on screen (physical pixels), from the screen's edge.</summary>
    private RECT PillScreenRect(PillShape shape)
    {
        double scale = (_root.XamlRoot?.RasterizationScale ?? 1) * _size;
        (double w, double h, _) = Dimensions(shape, _collapsedWidth);
        int stageLeft = _placed.Left + (int)Math.Round((Pad + EarRadius) * scale);
        int centre = stageLeft + (int)Math.Round(_stageWidth * scale / 2);
        int half = (int)Math.Round(w * scale / 2);
        bool island = _style == DynalandStyle.Island;
        if (_atBottom)
        {
            // Mirrored: the notch reaches the screen's bottom edge (the window's bottom).
            return new RECT
            {
                Left = centre - half,
                Top = _placed.Bottom - (int)Math.Round((Pad + _gap + h) * scale),
                Right = centre + half,
                Bottom = island ? _placed.Bottom - (int)Math.Round((Pad + _gap) * scale) : _placed.Bottom,
            };
        }

        return new RECT
        {
            Left = centre - half,
            Top = island ? _placed.Top + (int)Math.Round((Pad + _gap) * scale) : _placed.Top,
            Right = centre + half,
            Bottom = _placed.Top + (int)Math.Round((Pad + _gap + h) * scale),
        };
    }

    private void AnimatePill(double width, double height, double radius)
    {
        StartVector(_sizeAnimation, "Size", new Vector2((float)width, (float)(height + _topExtension)));
        StartVector(_offsetAnimation, "Offset", PillOffset(width, height));
        StartVector(_radiusAnimation, "CornerRadius", new Vector2((float)radius));
    }

    private void StartVector(Vector2KeyFrameAnimation animation, string property, Vector2 value)
    {
        animation.InsertKeyFrame(1f, value, _ease);
        animation.Duration = TimeSpan.FromMilliseconds(AnimateMs);
        _geometry.StartAnimation(property, animation);
    }

    private void Peek(PillShape shape)
    {
        _peek = shape;
        _peekTimer.Stop();
        _peekTimer.Start();
        Refresh();
    }

    // ---------------------------------------------------------------- hovering and the pages

    /// <summary>The pointer rests on the pill (or it was clicked): open, on the player first while music plays.</summary>
    private void StartHover()
    {
        if (!_shown || _closed || _host.Custom.IsOn)
        {
            // In custom mode a click picks Dynaland; it does not open.
            return;
        }

        _hovering = true;
        _onGeneralPage = false;
        _leaveTimer.Stop();
        _cursorTimer.Start();
        UpdateArrows();
        Refresh();
    }

    /// <summary>The pointer left: close, and start on the player again next time.</summary>
    private void EndHover()
    {
        _menuHolding = false;
        _hovering = false;
        _onGeneralPage = false;
        Refresh();
    }

    /// <summary>The arrows: player to general page and back.</summary>
    private Task<bool> ShowPage(bool general)
    {
        _onGeneralPage = general;
        UpdateArrows();
        Refresh();
        return Task.FromResult(true);
    }

    /// <summary>The way back to the player exists only while there is music.</summary>
    private void UpdateArrows()
    {
        Show(_toMedia, _media.Current.HasMedia);
    }

    private static void SmallArrow(Button button, FontIcon icon)
    {
        button.Width = 28;
        button.Height = 28;
        button.CornerRadius = new CornerRadius(14);
        icon.FontSize = 12;
    }

    /// <summary>Who uses the camera and the microphone, on the general page.</summary>
    private void FillUses()
    {
        _useRows.Children.Clear();
        var camera = new List<string>();
        var microphone = new List<string>();
        foreach ((string name, bool isCamera) in _uses)
        {
            (isCamera ? camera : microphone).Add(name);
        }

        if (camera.Count > 0)
        {
            _useRows.Children.Add(UseRow(CameraGreen, Strings.DynCamera, camera));
        }

        if (microphone.Count > 0)
        {
            _useRows.Children.Add(UseRow(MicOrange, Strings.DynMicrophone, microphone));
        }

        Show(_useRows, _uses.Count > 0);
        Show(_noUses, _uses.Count == 0);
    }

    private Grid UseRow(Color colour, string device, List<string> apps)
    {
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(colour),
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock text = Text(12, FontWeights.Normal, _whiteBrush);
        text.Text = device + ": " + string.Join(", ", apps);
        text.Margin = new Thickness(10, 0, 0, 0);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(dot);
        row.Children.Add(text);
        return row;
    }

    // ---------------------------------------------------------------- tray

    /// <summary>
    /// The tray icons as tiles, up to ten in a row and two rows (a last tile says how many more),
    /// and the general page grown to fit them; nothing (and no growth) without icons. When only
    /// pictures or tooltips changed, the tiles are updated where they are (a click in progress
    /// is never lost to a rebuild).
    /// </summary>
    private void FillTray()
    {
        if (_closed)
        {
            return;
        }

        // On the bar instead (Settings: tray on the bar): none here. Else in the kept order.
        IReadOnlyList<TrayIcon> icons = _host.Settings.Current.TrayOnBar
            ? Array.Empty<TrayIcon>()
            : _host.BarLayout.ArrangeTray(_tray.Icons);
        if (_trayDragKey is not null)
        {
            // Not while a tile is being dragged: the drop fills again.
            return;
        }

        double room = _stageWidth - _generalContent.Padding.Left - _generalContent.Padding.Right;
        int perRow = Math.Max(1, (int)Math.Floor((room + TraySpacing) / (TrayTile + TraySpacing)));
        int most = perRow * MaxTrayRows;
        int shown = icons.Count > most ? most - 1 : icons.Count;
        int more = icons.Count - shown;

        var shownKeys = new List<string>(shown);
        _trayOrdered = new List<TrayIcon>(shown);
        for (int i = 0; i < shown; i++)
        {
            shownKeys.Add(icons[i].Key);
            _trayOrdered.Add(icons[i]);
        }

        // Tiles of icons that are gone go too.
        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (TrayIcon icon in icons)
        {
            alive.Add(icon.Key);
        }

        foreach (string key in new List<string>(_trayTiles.Keys))
        {
            if (!alive.Contains(key))
            {
                _trayTiles.Remove(key);
            }
        }

        if (more == _trayMore && SameKeys(shownKeys, _trayShownKeys))
        {
            for (int i = 0; i < shown; i++)
            {
                TrayTileFor(icons[i]);
            }

            return;
        }

        foreach (UIElement oldRow in _trayRows.Children)
        {
            if (oldRow is Panel panel)
            {
                panel.Children.Clear();
            }
        }

        _trayRows.Children.Clear();
        StackPanel? row = null;
        for (int i = 0; i < shown; i++)
        {
            if (i % perRow == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TraySpacing };
                _trayRows.Children.Add(row);
            }

            row!.Children.Add(TrayTileFor(icons[i]));
        }

        if (more > 0 && row is not null)
        {
            TextBlock moreText = Text(12, FontWeights.SemiBold, _dimBrush);
            moreText.Text = "+" + more.ToString(CultureInfo.InvariantCulture);
            moreText.Width = TrayTile;
            moreText.TextAlignment = TextAlignment.Center;
            moreText.VerticalAlignment = VerticalAlignment.Center;
            ToolTipService.SetToolTip(moreText, Fmt.F(Strings.DynMoreTray, more));
            row.Children.Add(moreText);
        }

        _trayShownKeys = shownKeys;
        _trayMore = more;

        int rows = _trayRows.Children.Count;
        Show(_traySection, rows > 0);
        SetGeneralHeight(OpenHeight + (rows > 0 ? TrayTitleHeight + (rows * TrayRowHeight) : 0));
    }

    private static bool SameKeys(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The tile of one tray icon, made the first time and brought up to date after that: the
    /// app's own picture (or its first letter), its tooltip. Click, double-click, or right-click for its menu.
    /// </summary>
    private Button TrayTileFor(TrayIcon icon)
    {
        if (!_trayTiles.TryGetValue(icon.Key, out TrayTileView? view))
        {
            var tile = new Button
            {
                Width = TrayTile,
                Height = TrayTile,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(Radii.Small),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            string key = icon.Key;
            tile.Click += (_, _) =>
            {
                if (_traySuppressClick)
                {
                    // The end of a drag, not a click.
                    _traySuppressClick = false;
                    return;
                }

                ClickTray(key, right: false);
            };
            AttachTrayDrag(tile, key);
            tile.RightTapped += (_, e) =>
            {
                e.Handled = true;
                ClickTray(key, right: true);
            };
            view = new TrayTileView(tile);
            _trayTiles[key] = view;
        }

        string name = icon.Name.Length > 0 ? icon.Name : Strings.TrayIcon;
        bool nameChanged = !string.Equals(name, view.Name, StringComparison.Ordinal);
        if (view.Stamp != icon.PictureStamp || view.Button.Content is null || (icon.Picture is null && nameChanged))
        {
            view.Stamp = icon.PictureStamp;
            view.Button.Content = TrayPictureFor(icon, name);
        }

        if (nameChanged)
        {
            view.Name = name;
            ToolTipService.SetToolTip(view.Button, name);
            AutomationProperties.SetName(view.Button, name.Replace("\n", ", ", StringComparison.Ordinal));
        }

        view.Icon = icon;
        return view.Button;
    }

    // ---------------------------------------------------------------- dragging tray tiles

    /// <summary>
    /// A tray tile can be dragged to another place: it follows the pointer, an accent line marks
    /// the place, and on release the order is kept (the same order the bar's tray bubble uses).
    /// Below the drag distance it stays an ordinary click.
    /// </summary>
    private void AttachTrayDrag(Button tile, string key)
    {
        tile.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            if (_host.Custom.IsOn || !e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _trayPressKey = key;
            _trayPressPointer = e.Pointer.PointerId;
            _trayPressAt = e.GetCurrentPoint(_trayArea).Position;
        }), true);

        tile.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            if (_trayPressKey != key || e.Pointer.PointerId != _trayPressPointer)
            {
                return;
            }

            Windows.Foundation.Point at = e.GetCurrentPoint(_trayArea).Position;
            if (_trayDragKey is null)
            {
                if (Math.Abs(at.X - _trayPressAt.X) < TrayDragStartDip && Math.Abs(at.Y - _trayPressAt.Y) < TrayDragStartDip)
                {
                    return;
                }

                _trayDragKey = key;
                _traySuppressClick = true;
                tile.Opacity = 0.7;
                tile.RenderTransform = new TranslateTransform();
                if (VisualTreeHelper.GetParent(tile) is UIElement row)
                {
                    Canvas.SetZIndex(row, 1);
                }
            }

            if (tile.RenderTransform is TranslateTransform move)
            {
                move.X = at.X - _trayPressAt.X;
                move.Y = at.Y - _trayPressAt.Y;
            }

            ShowTrayDrop(at);
        }), true);

        tile.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => EndTrayDrag(tile, key, apply: true)), true);
        tile.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => EndTrayDrag(tile, key, apply: true)), true);
    }

    /// <summary>The place a drop at <paramref name="at"/> (in the tray area) would take: before or after the nearest tile.</summary>
    private void ShowTrayDrop(Windows.Foundation.Point at)
    {
        _trayDropIndex = -1;
        double best = double.MaxValue;
        Windows.Foundation.Rect bestRect = default;
        bool after = false;
        for (int i = 0; i < _trayOrdered.Count; i++)
        {
            if (!_trayTiles.TryGetValue(_trayOrdered[i].Key, out TrayTileView? view) || view.Button.XamlRoot is null)
            {
                continue;
            }

            Windows.Foundation.Point origin = view.Button.TransformToVisual(_trayArea).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (view.Button.RenderTransform is TranslateTransform shift)
            {
                // The dragged tile's own place, not where it floats.
                origin = new Windows.Foundation.Point(origin.X - shift.X, origin.Y - shift.Y);
            }

            var rect = new Windows.Foundation.Rect(origin.X, origin.Y, TrayTile, TrayTile);
            double dx = at.X - (rect.X + (rect.Width / 2));
            double dy = at.Y - (rect.Y + (rect.Height / 2));
            double distance = (dx * dx) + (dy * dy);
            if (distance < best)
            {
                best = distance;
                bestRect = rect;
                after = dx > 0;
                _trayDropIndex = after ? i + 1 : i;
            }
        }

        if (_trayDropIndex < 0)
        {
            _trayMarker.Visibility = Visibility.Collapsed;
            return;
        }

        double x = after ? bestRect.Right + (TraySpacing / 2) : bestRect.Left - (TraySpacing / 2);
        Canvas.SetLeft(_trayMarker, x - (_trayMarker.Width / 2));
        Canvas.SetTop(_trayMarker, bestRect.Top + 4);
        _trayMarker.Visibility = Visibility.Visible;
    }

    private void EndTrayDrag(Button tile, string key, bool apply)
    {
        if (_trayPressKey != key)
        {
            return;
        }

        _trayPressKey = null;
        if (_trayDragKey is null)
        {
            return;
        }

        _trayDragKey = null;
        _trayMarker.Visibility = Visibility.Collapsed;
        tile.Opacity = 1;
        tile.RenderTransform = null;
        if (VisualTreeHelper.GetParent(tile) is UIElement row)
        {
            Canvas.SetZIndex(row, 0);
        }

        // A click that comes with this release is the drag's end; one that does not come is no
        // reason to swallow the next real click.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _traySuppressClick = false);

        if (apply && _trayDropIndex >= 0)
        {
            int from = _trayOrdered.FindIndex(icon => string.Equals(icon.Key, key, StringComparison.Ordinal));
            if (from >= 0)
            {
                TrayIcon icon = _trayOrdered[from];
                int index = _trayDropIndex > from ? _trayDropIndex - 1 : _trayDropIndex;
                if (index != from)
                {
                    _host.BarLayout.PlaceTrayIcon(icon, index, _trayOrdered);
                    Log.Info("Dynaland: tray icon moved to place " + (index + 1) + ".");
                }
            }
        }

        _trayDropIndex = -1;
        FillTray();
    }

    /// <summary>The app's picture at Windows' own tray size (16), else the name's first letter.</summary>
    private FrameworkElement TrayPictureFor(TrayIcon icon, string name)
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
                    Source = StartMenuWindow.ToBitmap(pixels),
                };
            }
            catch (Exception ex)
            {
                Log.Warn("Dynaland: a tray icon's picture could not be shown.", ex);
            }
        }

        TextBlock letter = Text(13, FontWeights.SemiBold, _whiteBrush);
        letter.Text = name.Substring(0, 1).ToUpperInvariant();
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        return letter;
    }

    /// <summary>
    /// A tray tile was clicked. Right: the app gets the click (its menu opens at the pointer) and
    /// Dynaland stays open while that menu is up. Left: the app gets the click at once and
    /// Dynaland waits Windows' double-click time, so a second click becomes a double click (apps
    /// that open on a double click, as on Windows' own tray); then it closes.
    /// </summary>
    private void ClickTray(string key, bool right)
    {
        if (!_trayTiles.TryGetValue(key, out TrayTileView? view) || view.Icon is not TrayIcon icon)
        {
            return;
        }

        _leaveTimer.Stop();
        if (right)
        {
            _trayCloseTimer.Stop();
            _lastTrayKey = null;
            _menuHolding = true;
            _menuHoldUntil = Environment.TickCount64 + MenuAppearMs;
            _tray.Click(icon, TrayClick.Right);
            return;
        }

        long now = Environment.TickCount64;
        if (string.Equals(_lastTrayKey, key, StringComparison.Ordinal) && now - _lastTrayClickAt <= _doubleClickMs)
        {
            _lastTrayKey = null;
            _trayCloseTimer.Stop();
            _tray.Click(icon, TrayClick.Double);
            EndHover();
            return;
        }

        _lastTrayKey = key;
        _lastTrayClickAt = now;
        _tray.Click(icon, TrayClick.Left);
        _trayCloseTimer.Stop();
        _trayCloseTimer.Start();
    }

    /// <summary>How long Dynaland waits for an app's menu to appear after a right-click.</summary>
    private const int MenuAppearMs = 1500;

    /// <summary>
    /// True while Dynaland holds open for an app's menu: a moment after the right-click, then as
    /// long as a menu (Windows' standard menu window) is on screen. Clears itself after.
    /// </summary>
    private bool MenuHeld()
    {
        if (!_menuHolding)
        {
            return false;
        }

        if (Environment.TickCount64 < _menuHoldUntil || AnyMenuOpen())
        {
            return true;
        }

        _menuHolding = false;
        return false;
    }

    private static bool AnyMenuOpen()
    {
        nint window = 0;
        for (int guard = 0; guard < 32; guard++)
        {
            window = User32.FindWindowEx(0, window, "#32768", null);
            if (window == 0)
            {
                return false;
            }

            if (User32.IsWindowVisible(window))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The general page's height; while it is open, the pill follows.</summary>
    private void SetGeneralHeight(double height)
    {
        if (Math.Abs(height - _generalHeight) < 0.5)
        {
            return;
        }

        _generalHeight = height;
        _generalContent.Height = height;
        if (_pill == PillShape.General)
        {
            HoldRegion(_collapsedWidth);
            (double w, double h, double r) = Dimensions(PillShape.General, _collapsedWidth);
            AnimatePill(w, h, r);
        }
    }

    private void OpenNotifications()
    {
        // Close first, then Windows' own notification centre with its own list.
        _leaveTimer.Stop();
        EndHover();
        if (!KeyChord.SendWin('N'))
        {
            Log.Warn("Dynaland: Windows did not take the Win+N key press.");
        }
    }

    // ---------------------------------------------------------------- music

    private void ApplyMedia()
    {
        MediaSnapshot media = _media.Current;
        bool has = media.HasMedia;

        if (has)
        {
            _miniText.Text = media.Artist.Length > 0 ? media.Title + " - " + media.Artist : media.Title;
            _miniState.Glyph = ((char)(media.IsPlaying ? 0xEC4F : 0xE769)).ToString();
            _miniState.Foreground = media.IsPlaying ? _accentBrush : _dimBrush;
            Show(_miniArt, _artFound);
            _title.Text = media.Title;
            _artist.Text = media.Artist;

            // The scrolling titles stop while the music is paused.
            _miniText.Paused = !media.IsPlaying;
            _title.Paused = !media.IsPlaying;
            _artist.Paused = !media.IsPlaying;
            Show(_artist.Element, media.Artist.Length > 0);
            Set(_appName, media.AppName);
            _previous.IsEnabled = media.CanPrevious;
            _next.IsEnabled = media.CanNext;
            _playPause.IsEnabled = media.CanPlayPause;
            _playPauseIcon.Glyph = ((char)(media.IsPlaying ? 0xE769 : 0xE768)).ToString();
        }

        UpdateTimeline();
        UpdateName();
        UpdateArrows();

        if (has && (media.TrackKey != _artKey || (!_artFound && media.Art is not null)))
        {
            _artKey = media.TrackKey;
            _ = LoadArtAsync(media);
        }
        else if (!has && _artKey.Length > 0)
        {
            _artKey = string.Empty;
            _artVersion++;
            SetArt(null, null);
        }

        // A new track that plays: show it for a moment.
        if (has && media.IsPlaying && media.TrackKey != _peekedTrack)
        {
            _peekedTrack = media.TrackKey;
            Peek(PillShape.Media);
            return;
        }

        Refresh();
    }

    private void UpdateTimeline()
    {
        MediaSnapshot media = _media.Current;
        bool show = media.HasMedia && media.HasTimeline;
        Show(_timeline, show);
        if (!show || _seeking)
        {
            // While dragging, the bar shows the place under the pointer.
            return;
        }

        // Just after a seek, the chosen place until the app reports its new position.
        TimeSpan position = Environment.TickCount64 < _seekHoldUntil ? _seekTarget : media.PositionNow;
        double done = media.Duration.TotalSeconds <= 0 ? 0 : Math.Clamp(position.TotalSeconds / media.Duration.TotalSeconds, 0, 1);
        ShowTimeline(done, position, media.Duration);
        UpdateSeekThumb();
    }

    private void ShowTimeline(double done, TimeSpan position, TimeSpan duration)
    {
        Set(_elapsed, Format(position));
        Set(_total, Format(duration));
        _doneColumn.Width = new GridLength(Math.Max(done, 0.0001), GridUnitType.Star);
        _leftColumn.Width = new GridLength(Math.Max(1 - done, 0.0001), GridUnitType.Star);
        _seekThumbMove.X = (done * _seekArea.ActualWidth) - (_seekThumb.Width / 2);
    }

    /// <summary>The thumb shows while the pointer is over the bar or dragging, when the app allows seeking.</summary>
    private void UpdateSeekThumb()
    {
        bool shown = (_seekHover || _seeking) && _media.Current.CanSeek;
        Show(_seekThumb, shown);
    }

    private void PreviewSeek(double x)
    {
        double width = _seekArea.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _seekFraction = Math.Clamp(x / width, 0, 1);
        TimeSpan duration = _media.Current.Duration;
        ShowTimeline(_seekFraction, TimeSpan.FromSeconds(duration.TotalSeconds * _seekFraction), duration);
        UpdateSeekThumb();
    }

    /// <summary>The press or drag ended: the playing app jumps to the chosen place.</summary>
    private void FinishSeek()
    {
        _seeking = false;
        _seekTarget = TimeSpan.FromSeconds(_media.Current.Duration.TotalSeconds * _seekFraction);
        _seekHoldUntil = Environment.TickCount64 + 1500;
        _ = _media.SeekAsync(_seekTarget);
        UpdateSeekThumb();
    }

    private async Task LoadArtAsync(MediaSnapshot media)
    {
        int version = ++_artVersion;
        IRandomAccessStreamReference? art = media.Art;
        if (art is null)
        {
            SetArt(null, null);
            return;
        }

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 128 };
            using (IRandomAccessStreamWithContentType stream = await art.OpenReadAsync())
            {
                await bitmap.SetSourceAsync(stream);
            }

            if (version != _artVersion || _closed)
            {
                return;
            }

            Color? accent;
            using (IRandomAccessStreamWithContentType stream = await art.OpenReadAsync())
            {
                accent = await TrackAccent.FromStreamAsync(stream);
            }

            if (version != _artVersion || _closed)
            {
                return;
            }

            SetArt(bitmap, accent);
            if (!_accentLogged)
            {
                _accentLogged = true;
                Log.Info("Dynaland: cover read; accent " + (accent is Color c
                    ? "#" + c.R.ToString("X2", CultureInfo.InvariantCulture) + c.G.ToString("X2", CultureInfo.InvariantCulture) + c.B.ToString("X2", CultureInfo.InvariantCulture)
                    : "neutral (the cover has no strong colour)") + ".");
            }
        }
        catch (Exception ex)
        {
            if (version == _artVersion)
            {
                SetArt(null, null);
            }

            Log.Warn("Dynaland: the cover of \"" + media.Title + "\" could not be read.", ex);
        }
    }

    private void SetArt(BitmapImage? bitmap, Color? accent)
    {
        _artFound = bitmap is not null;
        _miniArtBrush.ImageSource = bitmap;
        _bigArtBrush.ImageSource = bitmap;
        _bigArt.Background = bitmap is not null ? (Brush)_bigArtBrush : _noArtBrush;
        Show(_noArtIcon, bitmap is null);
        Show(_miniArt, _media.Current.HasMedia && bitmap is not null);
        _accentBrush.Color = accent ?? Neutral;
        Refresh();
    }

    // ---------------------------------------------------------------- volume

    /// <summary>Four times a second; a change shows the level for 1.8 s. The first reading only sets the start.</summary>
    private void PollVolume()
    {
        _volumeTicks++;
        if (_volumeTicks % 20 == 1 || !_volume.IsAvailable)
        {
            // The default device can change.
            _volume.Connect();
        }

        if (!_volume.TryRead(out float level, out bool muted))
        {
            _volumeKnown = false;
            return;
        }

        int percent = Math.Clamp((int)Math.Round(level * 100), 0, 100);
        if (!_volumeKnown)
        {
            _volumeKnown = true;
            _volumePercent = percent;
            _volumeMuted = muted;
            ShowVolumeLevel();
            return;
        }

        if (percent == _volumePercent && muted == _volumeMuted)
        {
            return;
        }

        _volumePercent = percent;
        _volumeMuted = muted;
        ShowVolumeLevel();
        _volumeUntil = Environment.TickCount64 + VolumeShowMs;
        _volumeTimer.Stop();
        _volumeTimer.Start();
        Refresh();
    }

    private void ShowVolumeLevel()
    {
        int glyph = _volumeMuted || _volumePercent == 0 ? 0xE74F
            : _volumePercent > 66 ? 0xE995
            : _volumePercent > 33 ? 0xE994
            : 0xE993;
        _volumeIcon.Glyph = ((char)glyph).ToString();
        Set(_volumeText, _volumeMuted ? Strings.DynMuted : _volumePercent.ToString(CultureInfo.CurrentCulture));
        double done = _volumeMuted ? 0 : _volumePercent / 100.0;
        _volumeDone.Width = new GridLength(Math.Max(done, 0.0001), GridUnitType.Star);
        _volumeLeft.Width = new GridLength(Math.Max(1 - done, 0.0001), GridUnitType.Star);
    }

    // ---------------------------------------------------------------- camera and microphone

    private void OnPrivacyChanged(PrivacyState state)
    {
        string signature = state.Signature;
        bool started = false;
        foreach (string part in signature.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_privacySignature.Contains(part + "|", StringComparison.Ordinal))
            {
                started = true;
                break;
            }
        }

        _privacySignature = signature;
        var uses = new List<(string Name, bool Camera)>();
        foreach (PrivacyUse use in state.Camera)
        {
            uses.Add((NameOf(use), true));
        }

        foreach (PrivacyUse use in state.Microphone)
        {
            uses.Add((NameOf(use), false));
        }

        _uses = uses;

        if (!_privacyLogged || started)
        {
            _privacyLogged = true;
            Log.Info("Privacy: " + (uses.Count == 0
                ? "nothing uses the camera or the microphone."
                : DescribeUses(uses) + "."));
        }

        UpdateDots();
        FillUses();
        if (started && _host.Settings.Current.ShowPrivacyDots)
        {
            Peek(PillShape.Privacy);
            return;
        }

        Refresh();
    }

    private static string DescribeUses(List<(string Name, bool Camera)> uses)
    {
        var camera = new List<string>();
        var microphone = new List<string>();
        foreach ((string name, bool isCamera) in uses)
        {
            (isCamera ? camera : microphone).Add(name);
        }

        var parts = new List<string>();
        if (camera.Count > 0)
        {
            parts.Add(Fmt.F(Strings.DynCameraUses, string.Join(", ", camera)));
        }

        if (microphone.Count > 0)
        {
            parts.Add(Fmt.F(Strings.DynMicrophoneUses, string.Join(", ", microphone)));
        }

        return string.Join(" \u00B7 ", parts);
    }

    private void UpdateDots()
    {
        bool show = _host.Settings.Current.ShowPrivacyDots;
        PrivacyState state = _privacy.Current;
        bool camera = show && state.Camera.Count > 0;
        bool mic = show && state.Microphone.Count > 0;
        foreach ((StackPanel panel, Ellipse cameraDot, Ellipse micDot) in _dotSets)
        {
            Show(cameraDot, camera);
            Show(micDot, mic);
            Show(panel, camera || mic);
        }

        Set(_privacyText, DescribeUses(_uses));
        UpdateName();
    }

    /// <summary>A readable name for an app in Windows' camera / microphone records.</summary>
    private string NameOf(PrivacyUse use)
    {
        if (_names.TryGetValue(use.Key, out string? known))
        {
            return known;
        }

        string name;
        try
        {
            if (use.Packaged)
            {
                name = string.Empty;
                foreach (AppEntry app in _host.Catalog.Apps)
                {
                    if (app.AppUserModelId is string id && id.StartsWith(use.Key + "!", StringComparison.OrdinalIgnoreCase))
                    {
                        name = app.Name;
                        break;
                    }
                }

                if (name.Length == 0)
                {
                    // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera".
                    string family = use.Key.Split('_')[0];
                    int dot = family.LastIndexOf('.');
                    name = dot >= 0 && dot < family.Length - 1 ? family[(dot + 1)..] : family;
                }
            }
            else
            {
                string path = use.ExePath;
                string description = File.Exists(path) ? (FileVersionInfo.GetVersionInfo(path).FileDescription ?? string.Empty).Trim() : string.Empty;
                name = description.Length > 0 ? description : Path.GetFileNameWithoutExtension(path);
            }
        }
        catch (Exception)
        {
            name = use.Key;
        }

        _names[use.Key] = name;
        return name;
    }

    /// <summary>A readable name for the app Windows names the media session after.</summary>
    private string AppNameFor(string appId)
    {
        if (appId.Length == 0)
        {
            return Strings.DynAnApp;
        }

        try
        {
            foreach (AppEntry app in _host.Catalog.Apps)
            {
                if (app.AppUserModelId is string id && string.Equals(id, appId, StringComparison.OrdinalIgnoreCase))
                {
                    return app.Name;
                }
            }

            string name = appId;
            int bang = name.LastIndexOf('!');
            if (bang >= 0 && bang < name.Length - 1)
            {
                name = name[(bang + 1)..];
            }

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                foreach (AppEntry app in _host.Catalog.Apps)
                {
                    if (app.TargetPath is string target
                        && string.Equals(Path.GetFileName(target), name, StringComparison.OrdinalIgnoreCase))
                    {
                        return app.Name;
                    }
                }

                name = name[..^4];
            }

            // The browsers name their sessions with a short ID.
            return name switch
            {
                "MSEdge" => "Microsoft Edge",
                "Chrome" => "Google Chrome",
                _ => name,
            };
        }
        catch (Exception)
        {
            return appId;
        }
    }

    /// <summary>What screen readers hear for the pill.</summary>
    private void UpdateName()
    {
        MediaSnapshot media = _media.Current;
        string text = "Dynaland.";
        if (media.HasMedia)
        {
            string title = media.Artist.Length > 0 ? Fmt.F(Strings.DynTitleBy, media.Title, media.Artist) : media.Title;
            text += " " + Fmt.F(media.IsPlaying ? Strings.DynPlaying : Strings.DynPaused, title, media.AppName);
        }

        if (_uses.Count > 0)
        {
            text += " " + Fmt.F(Strings.DynInUseNowName, DescribeUses(_uses));
        }

        AutomationProperties.SetName(_root, text + " " + Strings.DynPointToOpen);
    }

    /// <summary>One tray tile and what it shows now.</summary>
    private sealed class TrayTileView
    {
        internal TrayTileView(Button button)
        {
            Button = button;
        }

        internal Button Button { get; }

        internal TrayIcon? Icon { get; set; }

        internal int Stamp { get; set; } = -1;

        internal string Name { get; set; } = string.Empty;
    }
}
