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
using DataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using DataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using DataPackageView = Windows.ApplicationModel.DataTransfer.DataPackageView;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using StandardDataFormats = Windows.ApplicationModel.DataTransfer.StandardDataFormats;
using IStorageItem = Windows.Storage.IStorageItem;
using StorageFile = Windows.Storage.StorageFile;
using StorageFolder = Windows.Storage.StorageFolder;
using StorageItemThumbnail = Windows.Storage.FileProperties.StorageItemThumbnail;
using ThumbnailMode = Windows.Storage.FileProperties.ThumbnailMode;
using ThumbnailOptions = Windows.Storage.FileProperties.ThumbnailOptions;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Machitan.Widgets;

/// <summary>
/// Dynaland: a black pill at the top of the bar's screen, in one of two shapes: a notch (the
/// default; hanging from the top edge, its sides curving out into the edge at the top, round
/// bottom corners) or an iPhone-inspired island (floating just below the edge, round all around).
/// Idle it is a small empty pill and does nothing (no clock: the info line owns the time).
/// It grows only for live things: music (the cover, the title and a note in the cover's
/// colour; on hover, or for 4 seconds when a new track starts, the full view with app,
/// cover, title, artist, controls, position and the volume), a volume change (the level for 1.8 s), and
/// camera or microphone use (a green camera dot and an orange mic dot on the pill in every
/// state; for 4 seconds when a use starts, and on hover, an "In use now" view naming the
/// apps). v0.9.3: the brightness level, the charger plugged in or out, "Copied" or "Cut", and a
/// new notification opened a little for 5 s, while Windows' own volume and brightness flyout and
/// its notification popups are hidden (WindowsFlyouts; each a setting, Settings > Dynaland).
/// Hovering opens Dynaland itself: with music playing, on the player first, with an arrow to
/// the general page; otherwise straight on the general page ("In use now" for the camera and
/// microphone, the tray, and Windows' own notifications as Windows' notification centre holds
/// them, with a button for that centre), with an arrow back to the
/// player while music plays, and a button to the file shelf. Moving away closes it.
/// The shelf (ShelfStore): files and folders, web links and text dragged onto the pill are
/// parked there (it opens on the shelf while they are dragged over), or pasted with its Paste
/// button; a click opens a file or a link and copies a note, a drag takes one out, the X takes
/// it off; Ctrl or Shift + click picks several (as in File Explorer), to drag out (files) or
/// take off together. Of files only their paths are kept; one moved or deleted since says so.
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

    /// <summary>The player's volume line, right below the music controls: its height and the gap above it.</summary>
    private const double VolumeLineHeight = 24;
    private const double VolumeLineGap = 2;

    /// <summary>One notch of the mouse wheel over the volume line moves it this many percent.</summary>
    private const int VolumeWheelStep = 2;

    // The tray on the general page: a title, then up to two rows of icon tiles.
    private const double TrayTitleHeight = 24;
    private const double TrayRowHeight = 36;
    private const int MaxTrayRows = 2;
    private const double TrayTile = 32;
    private const double TraySpacing = 4;

    /// <summary>The stage: room for the tallest view (the general page with two rows of tray icons).</summary>
    // The notification tray on the general page (CHANGELOG Parts 24 and 25): Windows' own list,
    // as tall as its rows up to MaxListHeight, then it scrolls. Rows are laid out as in
    // Windows' own panel (icon, app and time; pictures; title and text). The title row is a
    // little taller for "Clear all".
    private const double NoteTitleExtra = 6;
    private const double NoteSpacing = 4;
    private const double NotesMargin = 4;
    private const double MaxListHeight = 300;
    private const double NoteLineHeight = 20;
    private const double NoteMessageHeight = 36;
    private const double NoteHeadHeight = 18;
    private const double NoteIconSize = 16;
    private const int NoteIconPixels = 32;
    private const double NoteCloseRoom = 22;
    private const double NoteLogoSize = 40;
    private const double NoteHeroHeight = 96;
    private const double NoteInlineHeight = 120;
    private const double NotesMostHeight = NoteTitleExtra + NotesMargin + MaxListHeight;

    // The file shelf's page.
    private const double ShelfTopHeight = 28;
    private const double ShelfGap = 4;
    private const double ShelfRowHeight = 40;
    private const double ShelfRowSpacing = 2;
    private const double ShelfIconDip = 24;

    /// <summary>The shelf's link and note rows' icons, and its Paste button (Segoe Fluent Icons).</summary>
    private const int ShelfLinkGlyph = 0xE71B;
    private const int ShelfNoteGlyph = 0xE70B;
    private const int ShelfPasteGlyph = 0xE77F;
    private const double ShelfEmptyHeight = 54;

    /// <summary>The open pages' padding, top and bottom together (every look: see ApplyLook).</summary>
    private const double ShelfPadding = 20;

    /// <summary>After a drag leaves the pill (or only crosses from one part of it to another), how long before the shelf closes.</summary>
    private const int DropLeaveMs = 150;

    private const double StageHeight = OpenHeight + TrayTitleHeight + (MaxTrayRows * TrayRowHeight) + NotesMostHeight;
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

    // Trapezoid notch (Settings), closed pill only: how far each slanted side reaches out at
    // the screen's edge, the round bottom corners, and the curves into the screen's edge (DIP).
    // A slanted side turns only about 56 degrees at each corner, so the radii are bigger than
    // the standard notch's 12 and 10, for corners just as soft (Sam, 2026-11-26: "the trapezoid
    // shape's corners are too sharp" at 8 and 8).
    private const double TrapezoidSlant = 22;
    private const double TrapezoidRadius = 22;
    private const double TrapezoidFlare = 18;

    /// <summary>The one-piece notch reaches this far past the screen's edge, so no soft edge shows there.</summary>
    private const double SilhouetteBleed = 4;

    // Dynaland's opening curve (a little past the end and back), for Windows' compositor and
    // for the one-piece notch drawn frame by frame.
    private const float EaseX1 = 0.2f;
    private const float EaseY1 = 0.9f;
    private const float EaseX2 = 0.25f;
    private const float EaseY2 = 1.08f;

    /// <summary>Island shape: the gap between the screen's top edge and the floating pill.</summary>
    private const double IslandGap = 8;

    /// <summary>Island shape: corner radii of the open views (the collapsed pill is a full capsule).</summary>
    private const double IslandOpenRadius = 30;
    private const double IslandPrivacyRadius = 28;

    private const int AnimateMs = 300;
    private const int PeekMs = 4000;
    private const int VolumeShowMs = 1800;

    /// <summary>The brightness level on the closed pill, like the volume's.</summary>
    private const int BrightnessShowMs = 1800;

    /// <summary>The charger plugged in or out, on the closed pill.</summary>
    private const int PowerShowMs = 3000;

    /// <summary>"Copied" or "Cut" on the closed pill.</summary>
    private const int ClipboardShowMs = 1800;

    /// <summary>A new notification, opened a little this long (Windows' own popup shows about 5 s).</summary>
    private const int NotePeekMs = 5000;
    private const double NotePeekWidth = 360;
    private const double NotePeekHeight = 96;

    /// <summary>A notification's big picture (or its first picture below the text) under the words; the view grows for it.</summary>
    private const double NotePeekPictureHeight = 110;

    /// <summary>After a popup, Windows may add more notifications to it without showing it again: looked for this long.</summary>
    private const long PopupSessionMs = 8000;

    /// <summary>A notification at least this old and not shown came with no popup Machitan saw (a test line).</summary>
    private const double UnpairedNoteSeconds = 3;

    /// <summary>How long a hidden popup's notification is looked for in Windows' list (WindowsFlyouts waits 2.5 s).</summary>
    private const long NoteWantedMs = 2400;

    /// <summary>A notification Windows created this long ago at most can be the one its popup was for.</summary>
    private const double NewNoteSeconds = 15;

    /// <summary>Windows' brightness flyout and its WMI report come this close together (Sam's PC: within about 300 ms).</summary>
    private const long BrightnessPairMs = 1000;

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
    private readonly StackPanel _brightnessRow;
    private readonly ColumnDefinition _brightnessDone;
    private readonly ColumnDefinition _brightnessLeft;
    private readonly TextBlock _brightnessText;
    private readonly StackPanel _powerRow;
    private readonly FontIcon _powerIcon;
    private readonly TextBlock _powerText;
    private readonly StackPanel _clipRow;
    private readonly FontIcon _clipIcon;
    private readonly TextBlock _clipText;

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

    /// <summary>
    /// The player's volume line, right below the music controls and as wide as they are:
    /// Windows' default speakers, as in Win+A (the same VolumeService that shows a volume change
    /// on the pill). The mute button (its icon shows the level, or muted) and a bar like the
    /// position (a press or drag sets it, at once; the wheel nudges it). Hidden while Windows has
    /// no speakers or headphones.
    /// </summary>
    private readonly Grid _playerVolume;
    private readonly FontIcon _playerMuteIcon;
    private readonly Grid _playerVolumeArea;
    private readonly Ellipse _playerVolumeThumb;
    private readonly TranslateTransform _playerVolumeThumbMove = new();
    private readonly ColumnDefinition _playerVolumeDone;
    private readonly ColumnDefinition _playerVolumeLeft;
    private bool _playerVolumeDragging;
    private bool _playerVolumeHover;

    // In use now, open (for a moment when a use starts).
    private readonly Grid _privacyContent;

    /// <summary>A new notification, opened a little (Windows' own popup for it hidden).</summary>
    private readonly Grid _noteContent;
    private readonly Border _notePeekIcon;
    private readonly TextBlock _notePeekApp;
    private readonly TextBlock _notePeekTitle;
    private readonly TextBlock _notePeekBody;

    /// <summary>The notification's own picture beside the words (a person's photo, round when it asks), as in the list.</summary>
    private readonly Border _notePeekLogo;

    /// <summary>The notification's big picture, or its first picture below the text.</summary>
    private readonly Border _notePeekPicture;
    private readonly TextBlock _privacyText;

    // General page, open.
    private readonly Grid _generalContent;

    // The notification tray: Windows' own list (NotificationService), never kept.
    private readonly NotificationService _notes;
    private readonly StackPanel _noteRows;
    private readonly ScrollViewer _noteScroll;
    private readonly TextBlock _noteMessage;
    private readonly Button _clearAll;
    private readonly Dictionary<string, ImageSource> _noteIcons = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<NotificationEntry> _notesShown = Array.Empty<NotificationEntry>();
    private int _picturesShown;
    private double _trayExtra;
    private double _notesExtra;
    private double _noteMessageHeight;
    private readonly StackPanel _useRows;
    private readonly TextBlock _noUses;
    private readonly Button _toGeneral;
    private readonly Button _toMedia;

    // The file shelf, open (from the general page, or while files are dragged onto the pill).
    private readonly Grid _shelfContent;
    private readonly StackPanel _shelfRows;
    private readonly ScrollViewer _shelfScroll;
    private readonly TextBlock _shelfEmpty;
    private readonly TextBlock _shelfNote;
    private readonly Border _shelfDropHint;
    private readonly Dictionary<string, ImageSource> _shelfIcons = new(StringComparer.OrdinalIgnoreCase);
    private double _shelfHeight = OpenHeight;

    /// <summary>The player's height: taller while the song has synced lyrics.</summary>
    private double _mediaHeight = OpenHeight;
    private string _shelfShown = string.Empty;
    private string _shelfMessage = string.Empty;
    private int _shelfVersion;
    private bool _onShelfPage;

    /// <summary>Files are being dragged over the pill: it is open on the shelf, ready for the drop.</summary>
    private bool _dropping;

    /// <summary>
    /// Rows picked with Ctrl or Shift + click, as in File Explorer: a drag of one of them takes
    /// all the picked ones that are still there; the title row's button takes them off the
    /// shelf. Forgotten when Dynaland closes.
    /// </summary>
    private readonly HashSet<string> _shelfPicked = new(StringComparer.OrdinalIgnoreCase);
    private string? _shelfAnchor;
    private string? _shelfHovered;

    /// <summary>The rows shown now, by key (a file's path), with what each is.</summary>
    private readonly Dictionary<string, (Grid Row, ShelfKind Kind)> _shelfRowOf = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the drag out under way takes, for after it.</summary>
    private List<string>? _shelfDragged;

    /// <summary>A shelf row is being dragged out: Dynaland does not take it back if it is let go over the pill.</summary>
    private bool _draggingOut;
    private readonly TextBlock _shelfCount;
    private readonly Button _shelfSelectAll;
    private readonly FontIcon _shelfSelectAllIcon;
    private readonly Button _shelfRemovePicked;
    private readonly Button _shelfClearAll;
    private readonly SolidColorBrush _shelfHoverBrush = new(ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _shelfPickedBrush = new(ColorHelper.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _shelfPickedHoverBrush = new(ColorHelper.FromArgb(0x4A, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _shelfStillBrush = new(Colors.Transparent);

    private readonly SolidColorBrush _accentBrush = new(Neutral);
    private readonly SolidColorBrush _whiteBrush = new(Colors.White);
    private readonly SolidColorBrush _dimBrush = new(Dim);

    // Composition: held for the window's whole life (never left to .NET's finalizer).
    private readonly Compositor _compositor;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionSpriteShape _shape;
    private readonly ShapeVisual _shapeVisual;
    private readonly ContainerVisual _pillHost;

    // The notch in one piece (see UpdateSilhouetteMode): a single XAML path in the stage, below
    // the content, drawn again each frame while the pill animates. Used for the trapezoid and
    // whenever Dynaland is see-through; otherwise the composition pill and curves as always.
    private readonly Canvas _silhouetteLayer;
    private readonly ShapePath _silhouette;
    private readonly SolidColorBrush _silhouetteFill = new(Colors.Black);
    private bool _silhouetteOn;
    private bool _silhouetteFrames;
    private SilhouetteShape _silhouetteNow;
    private SilhouetteShape _silhouetteFrom;
    private SilhouetteShape _silhouetteTo;
    private long _silhouetteStart;
    private bool _trapezoid;
    private int _transparency;

    /// <summary>The composition pill's black (see-through for the island when Dynaland is).</summary>
    private readonly CompositionColorBrush _pillFill;

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
    private readonly Visual _shelfVisual;
    private readonly ScalarKeyFrameAnimation _shelfFade;
    private readonly Visual _noteVisual;
    private readonly ScalarKeyFrameAnimation _noteFade;

    private readonly DispatcherQueueTimer _heal;
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _hoverTimer;
    private readonly DispatcherQueueTimer _leaveTimer;
    private readonly DispatcherQueueTimer _cursorTimer;
    private readonly DispatcherQueueTimer _regionTimer;
    private readonly DispatcherQueueTimer _peekTimer;
    private readonly DispatcherQueueTimer _volumeTimer;
    private readonly DispatcherQueueTimer _volumePoll;
    private readonly DispatcherQueueTimer _dropLeaveTimer;

    /// <summary>While a hidden popup's notification is looked for: Windows' list read again every half second.</summary>
    private readonly DispatcherQueueTimer _noteRetry;
    private readonly MediaService _media;
    private readonly PrivacyMonitor _privacy;

    /// <summary>Windows' own volume and brightness flyout and notification popups, hidden while Dynaland shows them.</summary>
    private readonly WindowsFlyouts _flyouts;

    /// <summary>Brightness changes as Windows reports them, for the brightness level.</summary>
    private readonly BrightnessWatch _brightness;

    /// <summary>Ids of new notifications already shown (only those Windows still holds: no history).</summary>
    private readonly HashSet<uint> _noteShownIds = new();
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

    /// <summary>The general page's height now: taller by the tray's rows and the notification tray.</summary>
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
    private int _brightnessPercent = -1;
    private long _brightnessAt = long.MinValue / 2;
    private long _levelFlyoutAt = long.MinValue / 2;
    private long _brightnessUntil;

    /// <summary>-1 unknown (or no battery), 0 on battery, 1 plugged in.</summary>
    private int _powerState = -1;
    private int _powerPercent = -1;
    private bool _powerCharging;
    private long _powerUntil;
    private uint _clipSequence;
    private bool _clipKnown;
    private int _clipTries;
    private long _clipUntil;
    private long _noteWantedUntil;
    private long _popupSessionUntil;
    private uint? _notePeekId;
    private int _notePeekMore;
    private int _notePeekPicturesVersion = -1;
    private double _notePeekHeight = NotePeekHeight;

    /// <summary>Test lines only (v0.9.3 test build): notifications already noted as having come with no popup.</summary>
    private readonly HashSet<uint> _noteUnpairedLogged = new();
    private int _noteTestLines;
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

        // The one-piece notch: in a canvas, so nothing clips its curves past the stage.
        _silhouette = new ShapePath
        {
            Fill = _silhouetteFill,
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        _silhouetteLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _silhouetteLayer.Children.Add(_silhouette);
        _stage.Children.Add(_silhouetteLayer);
        _silhouetteNow = new SilhouetteShape(_idleWidth, CollapsedHeight, CollapsedRadius, 0, EarRadius);
        _silhouetteFrom = _silhouetteNow;
        _silhouetteTo = _silhouetteNow;

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
        _miniText = new Marquee(MiniTextSize, FontWeights.SemiBold, _whiteBrush, maxWidth: Math.Round(MiniTextMaxWidth * WidthRatio));
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

        // Brightness, like the volume (Windows' brightness flyout hidden; WindowsFlyouts).
        _brightnessDone = new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) };
        _brightnessLeft = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        Grid brightnessBar = MakeBar(_brightnessDone, _brightnessLeft, _whiteBrush);
        brightnessBar.Width = 120;
        _brightnessText = Text(12, FontWeights.SemiBold, _whiteBrush);
        _brightnessText.MinWidth = 28;
        _brightnessText.TextAlignment = TextAlignment.Right;
        _brightnessRow = Row();
        _brightnessRow.Spacing = 10;
        _brightnessRow.Children.Add(Glyph(0xE706, 14, _whiteBrush));
        _brightnessRow.Children.Add(brightnessBar);
        _brightnessRow.Children.Add(_brightnessText);
        _brightnessRow.Children.Add(MakeDots(6));

        // The charger plugged in or out: the battery's glyph, what it does now, and the level.
        _powerIcon = Glyph(0xE83F, 14, _whiteBrush);
        _powerText = Text(12, FontWeights.SemiBold, _whiteBrush);
        _powerRow = Row();
        _powerRow.Children.Add(_powerIcon);
        _powerRow.Children.Add(_powerText);
        _powerRow.Children.Add(MakeDots(6));

        // Copied or cut: only the kind of what is on the clipboard is looked at, never the content.
        _clipIcon = Glyph(0xE8C8, 14, _whiteBrush);
        _clipText = Text(12, FontWeights.SemiBold, _whiteBrush);
        _clipRow = Row();
        _clipRow.Children.Add(_clipIcon);
        _clipRow.Children.Add(_clipText);
        _clipRow.Children.Add(MakeDots(6));

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
        _collapsedContent.Children.Add(_brightnessRow);
        _collapsedContent.Children.Add(_powerRow);
        _collapsedContent.Children.Add(_clipRow);
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

        // The controls, with the volume line below them (added further down): the column is as
        // wide as the controls, and the volume line stretches to that.
        var controlColumn = new StackPanel
        {
            Spacing = VolumeLineGap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        controlColumn.Children.Add(controls);

        var mediaPanel = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        mediaPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mediaPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mediaPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(words, 1);
        Grid.SetColumn(controlColumn, 2);
        mediaPanel.Children.Add(_bigArt);
        mediaPanel.Children.Add(words);
        mediaPanel.Children.Add(controlColumn);

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

        // ---- music, open: the volume line below the controls (Windows' default speakers, as in Win+A)
        Button playerMute = MakeControl(0xE767, Strings.QsMute, ToggleMuteAsync, out _playerMuteIcon);
        playerMute.Width = VolumeLineHeight;
        playerMute.Height = VolumeLineHeight;
        playerMute.CornerRadius = new CornerRadius(VolumeLineHeight / 2);
        _playerMuteIcon.FontSize = 13;
        _playerVolumeDone = new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) };
        _playerVolumeLeft = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        Grid playerVolumeBar = MakeBar(_playerVolumeDone, _playerVolumeLeft, _accentBrush);
        _playerVolumeThumb = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = _whiteBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _playerVolumeThumbMove,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _playerVolumeArea = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        _playerVolumeArea.Children.Add(playerVolumeBar);
        _playerVolumeArea.Children.Add(_playerVolumeThumb);
        AutomationProperties.SetName(_playerVolumeArea, Strings.QsVolume);

        // The thumb's place depends on the bar's width, known only once it is laid out.
        _playerVolumeArea.SizeChanged += (_, _) => ShowPlayerVolume();
        _playerVolumeArea.PointerEntered += (_, _) =>
        {
            _playerVolumeHover = true;
            UpdatePlayerVolumeThumb();
        };
        _playerVolumeArea.PointerExited += (_, _) =>
        {
            _playerVolumeHover = false;
            UpdatePlayerVolumeThumb();
        };
        _playerVolumeArea.PointerPressed += (_, e) =>
        {
            if (!_volume.IsAvailable)
            {
                return;
            }

            _playerVolumeDragging = _playerVolumeArea.CapturePointer(e.Pointer);
            SetVolumeFromBar(e.GetCurrentPoint(_playerVolumeArea).Position.X);
            e.Handled = true;
        };
        _playerVolumeArea.PointerMoved += (_, e) =>
        {
            if (_playerVolumeDragging)
            {
                SetVolumeFromBar(e.GetCurrentPoint(_playerVolumeArea).Position.X);
            }
        };
        _playerVolumeArea.PointerReleased += (_, e) =>
        {
            if (_playerVolumeDragging)
            {
                _playerVolumeArea.ReleasePointerCapture(e.Pointer);
                _playerVolumeDragging = false;
                UpdatePlayerVolumeThumb();
            }
        };
        _playerVolumeArea.PointerCaptureLost += (_, _) =>
        {
            _playerVolumeDragging = false;
            UpdatePlayerVolumeThumb();
        };

        // No width of its own: it stretches to the controls' width above it.
        _playerVolume = new Grid
        {
            ColumnSpacing = 4,
            Height = VolumeLineHeight,
            Background = new SolidColorBrush(Colors.Transparent),
            Visibility = Visibility.Collapsed,
        };
        _playerVolume.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _playerVolume.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_playerVolumeArea, 1);
        _playerVolume.Children.Add(playerMute);
        _playerVolume.Children.Add(_playerVolumeArea);
        controlColumn.Children.Add(_playerVolume);
        _playerVolume.PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(_playerVolume).Properties.MouseWheelDelta;
            if (delta != 0 && _volume.IsAvailable && _volumeKnown)
            {
                SetPlayerVolume(_volumePercent + (Math.Sign(delta) * VolumeWheelStep));
            }

            e.Handled = true;
        };

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

        // The lyrics, between the cover row and the timeline (the player grows for them).
        StackPanel lyricArea = MakeLyricArea();
        Grid.SetRow(lyricArea, 2);
        _mediaContent.Children.Add(lyricArea);
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

        // ---- a new notification, opened a little: the app, the title, two lines of the text
        _notePeekIcon = new Border { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        _notePeekApp = Text(11, FontWeights.Normal, _dimBrush);
        var noteHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        noteHead.Children.Add(_notePeekIcon);
        noteHead.Children.Add(_notePeekApp);
        _notePeekTitle = Text(13, FontWeights.SemiBold, _whiteBrush);
        _notePeekBody = Text(12, FontWeights.Normal, _dimBrush);
        _notePeekBody.TextWrapping = TextWrapping.Wrap;
        _notePeekBody.MaxLines = 2;
        var noteWords = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        noteWords.Children.Add(_notePeekTitle);
        noteWords.Children.Add(_notePeekBody);
        _notePeekLogo = new Border
        {
            Width = NoteLogoSize,
            Height = NoteLogoSize,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        var noteMiddle = new Grid { ColumnSpacing = 10 };
        noteMiddle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noteMiddle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(noteWords, 1);
        noteMiddle.Children.Add(_notePeekLogo);
        noteMiddle.Children.Add(noteWords);
        _notePeekPicture = new Border { Height = NotePeekPictureHeight, Visibility = Visibility.Collapsed };
        var noteStack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        noteStack.Children.Add(noteHead);
        noteStack.Children.Add(noteMiddle);
        noteStack.Children.Add(_notePeekPicture);
        _noteContent = new Grid
        {
            Width = NotePeekWidth,
            Height = NotePeekHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _noteContent.Children.Add(noteStack);
        _stage.Children.Add(_noteContent);

        // ---- general page, open
        TextBlock usesTitle = Text(13, FontWeights.SemiBold, _whiteBrush);
        usesTitle.Text = Strings.DynInUseNow;
        _toMedia = MakeControl(0xE76B, Strings.DynBackToMusic, () => ShowPage(general: false), out FontIcon toMediaIcon);
        SmallArrow(_toMedia, toMediaIcon);
        Button toShelf = MakeControl(0xE8F1, Strings.DynToShelf, ShowShelf, out FontIcon toShelfIcon);
        SmallArrow(toShelf, toShelfIcon);
        var generalButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        generalButtons.Children.Add(toShelf);
        generalButtons.Children.Add(_toMedia);
        var generalTop = new Grid { Height = 28 };
        generalTop.Children.Add(usesTitle);
        generalTop.Children.Add(generalButtons);

        _useRows = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        _noUses = Text(12, FontWeights.Normal, _dimBrush);
        _noUses.Text = Strings.DynNothingInUse;
        _noUses.Margin = new Thickness(0, 2, 0, 0);

        TextBlock notificationsTitle = Text(13, FontWeights.SemiBold, _whiteBrush);
        notificationsTitle.Text = Strings.DynNotifications;

        // The notification tray: Windows' own list as it is now (see FillNotes).
        _notes = new NotificationService(DispatcherQueue);
        _clearAll = new Button
        {
            Content = Strings.DynClearAll,
            Height = 24,
            MinHeight = 0,
            FontSize = 12,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(Radii.Small),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = _dimBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_clearAll, Strings.DynClearAll);
        _clearAll.Click += (_, _) => _notes.ClearAll();
        var notificationsTop = new Grid { Height = 24 };
        notificationsTop.Children.Add(notificationsTitle);
        notificationsTop.Children.Add(_clearAll);

        _noteRows = new StackPanel { Spacing = NoteSpacing, Margin = new Thickness(0, 0, 6, 0) };
        _noteRows.SizeChanged += (_, _) => UpdateNotesHeight();
        _noteScroll = new ScrollViewer
        {
            Content = _noteRows,
            MaxHeight = MaxListHeight,
            Margin = new Thickness(0, NotesMargin, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
        };
        _noteMessage = Text(12, FontWeights.Normal, _dimBrush);
        _noteMessage.TextWrapping = TextWrapping.Wrap;
        _noteMessage.MaxLines = 2;
        _noteMessage.Margin = new Thickness(0, 2, 6, 0);
        _noteMessage.Visibility = Visibility.Collapsed;

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
        notificationsPanel.Children.Add(notificationsTop);
        notificationsPanel.Children.Add(_noteScroll);
        notificationsPanel.Children.Add(_noteMessage);
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

        // ---- the file shelf, open
        TextBlock shelfTitle = Text(13, FontWeights.SemiBold, _whiteBrush);
        shelfTitle.Text = Strings.DynShelf;
        _shelfCount = Text(12, FontWeights.Normal, _dimBrush);
        _shelfCount.Visibility = Visibility.Collapsed;
        var shelfTitles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        shelfTitles.Children.Add(shelfTitle);
        shelfTitles.Children.Add(_shelfCount);

        // Clear all (while there is something), take the picked ones off (while some are picked),
        // select all or none (two rows or more), back.
        _shelfClearAll = new Button
        {
            Content = Strings.DynClearAll,
            Height = 24,
            MinHeight = 0,
            FontSize = 12,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(Radii.Small),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = _dimBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_shelfClearAll, Strings.DynShelfClearAll);
        _shelfClearAll.Click += (_, _) => ClearShelf();
        _shelfRemovePicked = MakeControl(0xE894, Strings.DynShelfRemoveSelected, RemovePicked, out FontIcon removePickedIcon);
        SmallArrow(_shelfRemovePicked, removePickedIcon);
        _shelfRemovePicked.Visibility = Visibility.Collapsed;
        _shelfSelectAll = MakeControl(0xE8B3, Strings.DynShelfSelectAll, ToggleSelectAll, out _shelfSelectAllIcon);
        SmallArrow(_shelfSelectAll, _shelfSelectAllIcon);
        _shelfSelectAll.Visibility = Visibility.Collapsed;
        Button shelfBack = MakeControl(0xE76B, Strings.DynBackToGeneral, () => ShowPage(general: true), out FontIcon shelfBackIcon);
        SmallArrow(shelfBack, shelfBackIcon);
        Button shelfPaste = MakeControl(ShelfPasteGlyph, Strings.DynShelfPaste, PasteToShelfAsync, out FontIcon shelfPasteIcon);
        SmallArrow(shelfPaste, shelfPasteIcon);
        var shelfButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        shelfButtons.Children.Add(shelfPaste);
        shelfButtons.Children.Add(_shelfClearAll);
        shelfButtons.Children.Add(_shelfRemovePicked);
        shelfButtons.Children.Add(_shelfSelectAll);
        shelfButtons.Children.Add(shelfBack);
        var shelfTop = new Grid { Height = ShelfTopHeight };
        shelfTop.Children.Add(shelfTitles);
        shelfTop.Children.Add(shelfButtons);

        // A full shelf, a drop without files, or a shelf file that could not be read: said here.
        _shelfNote = Text(12, FontWeights.Normal, _dimBrush);
        _shelfNote.TextWrapping = TextWrapping.Wrap;
        _shelfNote.MaxLines = 2;
        _shelfNote.Margin = new Thickness(0, 2, 6, 0);
        _shelfNote.Visibility = Visibility.Collapsed;

        _shelfEmpty = Text(12, FontWeights.Normal, _dimBrush);
        _shelfEmpty.Text = Strings.DynShelfEmpty;
        _shelfEmpty.TextWrapping = TextWrapping.Wrap;
        _shelfEmpty.MaxLines = 3;
        _shelfEmpty.VerticalAlignment = VerticalAlignment.Top;
        _shelfEmpty.Margin = new Thickness(0, 2, 6, 0);

        _shelfRows = new StackPanel { Spacing = ShelfRowSpacing, Margin = new Thickness(0, 0, 6, 0) };
        _shelfScroll = new ScrollViewer
        {
            Content = _shelfRows,
            MaxHeight = MaxListHeight,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
        };

        // While files are dragged over: where to let go.
        FontIcon dropGlyph = Glyph(0xE896, 20, _whiteBrush);
        dropGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        TextBlock dropText = Text(13, FontWeights.SemiBold, _whiteBrush);
        dropText.Text = Strings.DynShelfDrop;
        dropText.HorizontalAlignment = HorizontalAlignment.Center;
        var dropStack = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dropStack.Children.Add(dropGlyph);
        dropStack.Children.Add(dropText);
        _shelfDropHint = new Border
        {
            Child = dropStack,
            CornerRadius = new CornerRadius(Radii.Medium),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xF0, 0x16, 0x16, 0x16)),
            Margin = new Thickness(0, 0, 6, 0),
            Visibility = Visibility.Collapsed,
        };

        var shelfBody = new Grid { Margin = new Thickness(0, ShelfGap, 0, 0) };
        shelfBody.Children.Add(_shelfEmpty);
        shelfBody.Children.Add(_shelfScroll);
        shelfBody.Children.Add(_shelfDropHint);

        _shelfContent = new Grid
        {
            Width = _stageWidth,
            Height = OpenHeight,
            Padding = new Thickness(18, 6, 12, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _shelfContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _shelfContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _shelfContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_shelfNote, 1);
        Grid.SetRow(shelfBody, 2);
        _shelfContent.Children.Add(shelfTop);
        _shelfContent.Children.Add(_shelfNote);
        _shelfContent.Children.Add(shelfBody);
        _stage.Children.Add(_shelfContent);

        // ---- the pill (Composition)
        _collapsedVisual = ElementCompositionPreview.GetElementVisual(_collapsedContent);
        _mediaVisual = ElementCompositionPreview.GetElementVisual(_mediaContent);
        _privacyVisual = ElementCompositionPreview.GetElementVisual(_privacyContent);
        _generalVisual = ElementCompositionPreview.GetElementVisual(_generalContent);
        _shelfVisual = ElementCompositionPreview.GetElementVisual(_shelfContent);
        _noteVisual = ElementCompositionPreview.GetElementVisual(_noteContent);
        _compositor = _collapsedVisual.Compositor;
        _geometry = _compositor.CreateRoundedRectangleGeometry();
        _geometry.CornerRadius = new Vector2((float)CollapsedRadius);
        _geometry.Size = new Vector2((float)_idleWidth, (float)(CollapsedHeight + TopExtension));
        _geometry.Offset = new Vector2((float)((_stageWidth - _idleWidth) / 2), (float)-TopExtension);
        _shape = _compositor.CreateSpriteShape(_geometry);
        _pillFill = _compositor.CreateColorBrush(Colors.Black);
        _shape.FillBrush = _pillFill;
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
        _shelfFade = _compositor.CreateScalarKeyFrameAnimation();
        _noteFade = _compositor.CreateScalarKeyFrameAnimation();
        _mediaVisual.Opacity = 0;
        _privacyVisual.Opacity = 0;
        _generalVisual.Opacity = 0;
        _shelfVisual.Opacity = 0;
        _noteVisual.Opacity = 0;

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

        // Files dragged onto the pill: it opens on the shelf, and letting go parks them there.
        // The drag also "leaves" when it only crosses from one part of the pill to another, so
        // the shelf closes only a moment after the last leave without a new move over it.
        _root.AllowDrop = true;
        _root.DragEnter += OnShelfDragOver;
        _root.DragOver += OnShelfDragOver;
        _root.DragLeave += (_, _) =>
        {
            if (_dropping)
            {
                _dropLeaveTimer!.Stop();
                _dropLeaveTimer.Start();
            }
        };
        _root.Drop += OnShelfDrop;

        _heal = MakeTimer(TimeSpan.FromSeconds(2), repeating: true, Heal);
        _tick = MakeTimer(TimeSpan.FromMilliseconds(500), repeating: true, UpdateTimeline);
        _lyricsTick = MakeTimer(TimeSpan.FromMilliseconds(200), repeating: true, TickLyrics);
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
        _dropLeaveTimer = MakeTimer(TimeSpan.FromMilliseconds(DropLeaveMs), repeating: false, EndDrop);
        _noteRetry = MakeTimer(TimeSpan.FromMilliseconds(500), repeating: true, () =>
        {
            _notes.ReadNow();
            ShowNewNote();
        });

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
        _notes.Changed += FillNotes;
        _notes.Changed += OnNotesChangedForPeek;
        _flyouts = new WindowsFlyouts(DispatcherQueue);
        _flyouts.LevelFlyoutShown += OnLevelFlyoutShown;
        _flyouts.PopupHidden += OnPopupHidden;
        _brightness = new BrightnessWatch(DispatcherQueue);
        _brightness.Changed += OnBrightnessChanged;
        _host.Shelf.Changed += FillShelf;

        _host.Settings.Changed += OnSettingsChanged;
        _host.FullScreenChanged += OnFullScreenChanged;
        _host.Custom.Changed += OnCustomChanged;
        Closed += (_, _) =>
        {
            _host.Custom.Changed -= OnCustomChanged;
            _closed = true;
            StopSilhouetteFrames();
            foreach (DispatcherQueueTimer timer in new[]
                     {
                         _heal, _tick, _hoverTimer, _leaveTimer, _cursorTimer, _regionTimer, _peekTimer, _volumeTimer, _volumePoll,
                         _trayCloseTimer, _dropLeaveTimer, _noteRetry,
                     })
            {
                timer.Stop();
            }

            _lyricsTick?.Stop();
            _lyricsCancel?.Cancel();
            _host.Settings.Changed -= OnSettingsChanged;
            _host.FullScreenChanged -= OnFullScreenChanged;
            _media.Changed -= ApplyMedia;
            _media.Dispose();
            _privacy.Changed -= OnPrivacyChanged;
            _privacy.Dispose();
            _notes.Changed -= FillNotes;
            _notes.Changed -= OnNotesChangedForPeek;
            _notes.Dispose();
            _flyouts.Dispose();
            _brightness.Dispose();
            _host.Shelf.Changed -= FillShelf;
            _tray.Changed -= FillTray;
            _host.BarLayout.Changed -= FillTray;
            _subclass?.Dispose();
            _subclass = null;
        };

        UpdateDots();
        FillUses();
        FillNotes();
        FillShelf();
        ApplyLook(EffectiveStyle(), !_host.DynalandOnTop());
        ApplyTransparency();
        ApplyMedia();
    }

    private enum PillShape
    {
        Collapsed,
        Media,
        Privacy,
        General,
        Shelf,
        Note,
    }

    private enum CollapsedForm
    {
        Idle,
        Media,
        Volume,
        Brightness,
        Power,
        Clipboard,
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
    /// The transparency setting (0 to 50 %): only the black goes see-through; the text and
    /// pictures on it stay solid. The notch is then drawn in one piece (no darker seams where its
    /// parts would overlap); the island is one shape already.
    /// </summary>
    private void ApplyTransparency()
    {
        int wanted = Math.Clamp(_host.Settings.Current.DynalandTransparency, 0, ShellSettings.MaxDynalandTransparency);
        if (wanted == _transparency)
        {
            return;
        }

        _transparency = wanted;
        UpdateSilhouetteMode();
        Log.Info("Dynaland: " + (wanted == 0 ? "solid black." : wanted.ToString(CultureInfo.InvariantCulture) + "% see-through."));
    }

    /// <summary>
    /// Picks how the notch is drawn. The composition pill and its two curves (the default) are
    /// separate pieces that overlap by 1 DIP, which only an opaque black hides; so for the
    /// trapezoid, and whenever Dynaland is see-through, the notch is one XAML path instead
    /// (DrawSilhouette): one fill, no overlaps, no seams. The island is always the composition
    /// pill (one shape). Also sets the black's transparency on both.
    /// </summary>
    private void UpdateSilhouetteMode()
    {
        bool island = _style == DynalandStyle.Island;
        bool on = !island && (_transparency > 0 || _trapezoid);
        Color black = ColorHelper.FromArgb((byte)Math.Round(255 * (1 - (_transparency / 100.0))), 0, 0, 0);
        _pillFill.Color = black;
        _silhouetteFill.Color = black;

        _shapeVisual.IsVisible = !on;
        if (_leftEar is not null && _rightEar is not null)
        {
            _leftEar.IsVisible = !island && !on;
            _rightEar.IsVisible = !island && !on;
        }

        Show(_earHit, !island && (on || _leftEar is not null));
        Show(_silhouetteLayer, on);

        // At the bottom edge the drawing is mirrored: the notch hangs from the screen's bottom edge.
        _silhouette.RenderTransform = new ScaleTransform { ScaleY = _atBottom ? -1 : 1, CenterY = StageHeight / 2 };

        _silhouetteOn = on;
        (double w, double h, double r) = Dimensions(_pill, _collapsedWidth);
        SetSilhouette(w, h, r, animate: false);
    }

    /// <summary>
    /// The one-piece notch goes to this pill size, with the pill (the same 300 ms and curve as
    /// Windows' compositor uses for it), or at once. Only the closed pill of the trapezoid has
    /// slanted sides; the open views stay square-sided with the default curves.
    /// </summary>
    private void SetSilhouette(double width, double height, double radius, bool animate)
    {
        bool slanted = _trapezoid && height <= CollapsedHeight + 0.5;
        var target = new SilhouetteShape(
            width,
            height,
            slanted ? TrapezoidRadius : radius,
            slanted ? TrapezoidSlant : 0,
            slanted ? TrapezoidFlare : EarRadius);

        if (!_silhouetteOn || !animate)
        {
            StopSilhouetteFrames();
            _silhouetteNow = target;
            _silhouetteTo = target;
            DrawSilhouette();
            return;
        }

        _silhouetteFrom = _silhouetteNow;
        _silhouetteTo = target;
        _silhouetteStart = Stopwatch.GetTimestamp();
        if (!_silhouetteFrames)
        {
            _silhouetteFrames = true;
            CompositionTarget.Rendering += OnSilhouetteFrame;
        }
    }

    private void StopSilhouetteFrames()
    {
        if (_silhouetteFrames)
        {
            _silhouetteFrames = false;
            CompositionTarget.Rendering -= OnSilhouetteFrame;
        }
    }

    private void OnSilhouetteFrame(object? sender, object e)
    {
        if (_closed || !_silhouetteOn)
        {
            StopSilhouetteFrames();
            return;
        }

        double t = Math.Clamp(Stopwatch.GetElapsedTime(_silhouetteStart).TotalMilliseconds / AnimateMs, 0, 1);
        _silhouetteNow = t >= 1 ? _silhouetteTo : SilhouetteShape.Blend(_silhouetteFrom, _silhouetteTo, EaseAt(t));
        DrawSilhouette();
        if (t >= 1)
        {
            StopSilhouetteFrames();
        }
    }

    /// <summary>Dynaland's opening curve at time <paramref name="t"/> (0 to 1), as Windows' compositor runs it.</summary>
    private static double EaseAt(double t)
    {
        // The curve's x rises steadily from 0 to 1: find the point at time t, then its y.
        double low = 0;
        double high = 1;
        double s = t;
        for (int i = 0; i < 24; i++)
        {
            s = (low + high) / 2;
            if (Bezier(s, EaseX1, EaseX2) < t)
            {
                low = s;
            }
            else
            {
                high = s;
            }
        }

        return Bezier(s, EaseY1, EaseY2);
    }

    private static double Bezier(double s, double p1, double p2)
    {
        double u = 1 - s;
        return (3 * u * u * s * p1) + (3 * u * s * s * p2) + (s * s * s);
    }

    /// <summary>
    /// The notch as one outline, drawn for the top edge (mirrored at the bottom): the top runs
    /// past the screen's edge; each side comes down from the edge with a curve into it (radius
    /// Flare), straight (slanting out by Slant at the edge for the trapezoid, upright otherwise),
    /// then turns into the bottom edge with a round corner (radius Radius), every curve meeting
    /// its straight parts smoothly. The pill's width is its width at the bottom. A curve of
    /// radius R reaches R * k along each line it joins, k being the tangent of half its turn
    /// (1 for an upright side, less for a slanted one); the curves are kept small enough that
    /// they never cross (each at most half the side, and the corners at most half the bottom).
    /// </summary>
    private void DrawSilhouette()
    {
        if (!_silhouetteOn)
        {
            return;
        }

        double w = Math.Max(1, _silhouetteNow.Width);
        double h = Math.Max(1, _silhouetteNow.Height);
        double s = Math.Max(0, _silhouetteNow.Slant);
        double length = Math.Sqrt((s * s) + (h * h));
        double k = h / (length + s);

        double r = Math.Clamp(_silhouetteNow.Radius, 0, Math.Min(length / 2, w / 2) / k);
        double f = Math.Clamp(_silhouetteNow.Flare, 0, length / 2 / k);

        double left = (_stageWidth - w) / 2;
        double right = left + w;
        double dx = s / length;
        double dy = h / length;

        // Along the left side from its top end (left - s, 0): where the curve into the edge meets
        // it (at a), and where the bottom corner starts (at b).
        double a = f * (length - s) / h;
        double b = ((length * (h - r)) + (r * s)) / h;
        double sideTopX = left - s;
        double flareX = sideTopX + (a * dx) - (f * dy);
        double flareSideX = sideTopX + (a * dx);
        double flareSideY = a * dy;
        double cornerSideX = sideTopX + (b * dx);
        double cornerSideY = b * dy;
        double bottomX = cornerSideX + (r * dy);

        double Mirror(double x) => left + right - x;

        var figure = new PathFigure
        {
            StartPoint = new Windows.Foundation.Point(flareX, -SilhouetteBleed),
            IsClosed = true,
            IsFilled = true,
            Segments = new PathSegmentCollection(),
        };
        void Line(double x, double y) => figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x, y) });
        void Arc(double x, double y, double radius, SweepDirection sweep) => figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(x, y),
            Size = new Windows.Foundation.Size(radius, radius),
            SweepDirection = sweep,
        });

        // Clockwise: along the top, down the right side, along the bottom, up the left side.
        Line(Mirror(flareX), -SilhouetteBleed);
        Line(Mirror(flareX), 0);
        Arc(Mirror(flareSideX), flareSideY, f, SweepDirection.Counterclockwise);
        Line(Mirror(cornerSideX), cornerSideY);
        Arc(Mirror(bottomX), h, r, SweepDirection.Clockwise);
        Line(bottomX, h);
        Arc(cornerSideX, cornerSideY, r, SweepDirection.Clockwise);
        Line(flareSideX, flareSideY);
        Arc(flareX, 0, f, SweepDirection.Counterclockwise);

        var geometry = new PathGeometry { Figures = new PathFigureCollection() };
        geometry.Figures.Add(figure);
        _silhouette.Data = geometry;
    }

    /// <summary>One shape of the one-piece notch (DIP): the bottom width, height, bottom corners, slant, and curves into the edge.</summary>
    private readonly record struct SilhouetteShape(double Width, double Height, double Radius, double Slant, double Flare)
    {
        internal static SilhouetteShape Blend(SilhouetteShape from, SilhouetteShape to, double k) => new(
            from.Width + ((to.Width - from.Width) * k),
            from.Height + ((to.Height - from.Height) * k),
            from.Radius + ((to.Radius - from.Radius) * k),
            from.Slant + ((to.Slant - from.Slant) * k),
            from.Flare + ((to.Flare - from.Flare) * k));
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
            return compositor.CreateCubicBezierEasingFunction(new Vector2(EaseX1, EaseY1), new Vector2(EaseX2, EaseY2));
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
            PillShape.Media => (_stageWidth, _mediaHeight, _openRadius),
            PillShape.General => (_stageWidth, _generalHeight, _openRadius),
            PillShape.Shelf => (_stageWidth, _shelfHeight, _openRadius),
            PillShape.Privacy => (PrivacyWidth, PrivacyHeight, _privacyRadius),
            PillShape.Note => (NotePeekWidth, _notePeekHeight, _privacyRadius),
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

        // The volume keys are Machitan's only while Dynaland is there to show the level.
        _host.VolumeKeys.SetDynalandShown(want);

        // Windows' notifications are read only while Dynaland is on screen.
        _notes.Active = want;

        // Windows' own flyouts are hidden only while Dynaland is there to show them instead.
        UpdateFlyouts();
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
            _clipKnown = false;
            _powerState = -1;
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
        ApplyTransparency();
        UpdateVisibility();
        UpdateFlyouts();
        FillTray();
        UpdateDots();
        FollowLyrics(_media.Current);
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
        _idleWidth = Math.Round(IdleWidth * WidthRatio);

        // The music line's widest (wider while a lyric line shows).
        ApplyLyricWidth();
        _root.Width = DesignWidth;
        _stage.Width = width;
        _expandedHit.Width = width;
        _mediaContent.Width = width;
        _generalContent.Width = width;
        _shelfContent.Width = width;
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
    /// sides curving out into the edge at the top, round bottom corners; as a trapezoid, the
    /// closed pill's sides slant out towards the edge (see DrawSilhouette). Island (iPhone-inspired): floating 8 DIP below the edge,
    /// round all around: a full capsule collapsed, large round corners open. Applied at once,
    /// without animating; the window keeps its size (it always has room for the gap).
    /// </summary>
    private void ApplyLook(DynalandStyle style, bool atBottom)
    {
        bool trapezoid = style == DynalandStyle.Notch
            && _host.Settings.Current.DynalandNotchShape == DynalandNotchShape.Trapezoid;
        if (style == _style && atBottom == _atBottom && trapezoid == _trapezoid)
        {
            return;
        }

        _style = style;
        _atBottom = atBottom;
        _trapezoid = trapezoid;

        // A lyric line's widest depends on the shape (the trapezoid's slanted sides need room).
        ApplyLyricWidth();
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
        _noteContent.VerticalAlignment = toEdge;
        _mediaContent.VerticalAlignment = toEdge;
        _generalContent.VerticalAlignment = toEdge;
        _shelfContent.VerticalAlignment = toEdge;

        // The island's corners are all round, so the open views keep their rows clear of them;
        // the notch's square side is at the screen's edge, so less room is needed there.
        Thickness open = island
            ? new Thickness(20, 10, 14, 10)
            : atBottom ? new Thickness(18, 14, 12, 6) : new Thickness(18, 6, 12, 14);
        _mediaContent.Padding = open;
        _generalContent.Padding = open;
        _shelfContent.Padding = open;
        _noteContent.Padding = island
            ? new Thickness(22, 10, 22, 10)
            : atBottom ? new Thickness(20, 12, 20, 6) : new Thickness(20, 6, 20, 12);
        if (_leftEar is not null && _rightEar is not null && _leftEarFollow is not null && _rightEarFollow is not null)
        {
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
        UpdateSilhouetteMode();
        SettleRegion();
        _cutter.Invalidate();
        Log.Info("Dynaland: " + (island ? "island" : trapezoid ? "trapezoid notch" : "notch") + " shape, " + (atBottom ? "bottom" : "top") + " edge.");

        // A lyric line showing takes its widest for the new shape.
        if (_lyricWide)
        {
            Refresh();
        }
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
    /// Files dragged over it: open on the shelf. Hovered: open, on the player while music plays
    /// (unless the arrow went to the general page), else on the general page, or on the shelf
    /// when its button was used. Not hovered: open only for a moment for live things (a new
    /// track, a new camera / microphone use).
    /// </summary>
    private PillShape DesiredShape()
    {
        if (!_shown)
        {
            return PillShape.Collapsed;
        }

        if (_dropping)
        {
            return PillShape.Shelf;
        }

        bool media = _media.Current.HasMedia;
        bool privacy = PrivacyShown;
        if (_hovering)
        {
            if (_onShelfPage)
            {
                return PillShape.Shelf;
            }

            return media && !_onGeneralPage ? PillShape.Media : PillShape.General;
        }

        if (_peek == PillShape.Note)
        {
            return PillShape.Note;
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

    /// <summary>A short moment (volume, brightness, charger, copy) while it runs; else the music, else idle.</summary>
    private CollapsedForm DesiredForm()
    {
        long now = Environment.TickCount64;
        if (_volumeKnown && now < _volumeUntil)
        {
            return CollapsedForm.Volume;
        }

        if (_brightnessPercent >= 0 && now < _brightnessUntil)
        {
            return CollapsedForm.Brightness;
        }

        if (_powerState >= 0 && now < _powerUntil)
        {
            return CollapsedForm.Power;
        }

        if (now < _clipUntil)
        {
            return CollapsedForm.Clipboard;
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
        Show(_brightnessRow, form == CollapsedForm.Brightness);
        Show(_powerRow, form == CollapsedForm.Power);
        Show(_clipRow, form == CollapsedForm.Clipboard);

        StackPanel row = form switch
        {
            CollapsedForm.Media => _miniRow,
            CollapsedForm.Volume => _volumeRow,
            CollapsedForm.Brightness => _brightnessRow,
            CollapsedForm.Power => _powerRow,
            CollapsedForm.Clipboard => _clipRow,
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
            if (shape == PillShape.Shelf)
            {
                // The shelf as it is now: its height first (the pill opens to it), its rows a moment later.
                FillShelf();
            }

            _pill = shape;
            HoldRegion(oldWidth);
            (double w, double h, double r) = Dimensions(shape, width);
            AnimatePill(w, h, r);
            Fade(VisualOf(old), FadeOf(old), 0f, 0, 110);
            Fade(VisualOf(shape), FadeOf(shape), 1f, 90, 200);
            _mediaContent.IsHitTestVisible = shape == PillShape.Media;
            _generalContent.IsHitTestVisible = shape == PillShape.General;
            _shelfContent.IsHitTestVisible = shape == PillShape.Shelf;
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

        // Windows' notification list: every second while the page with it is open.
        _notes.Fast = _pill == PillShape.General;

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
        PillShape.Shelf => _shelfVisual,
        PillShape.Note => _noteVisual,
        _ => _collapsedVisual,
    };

    private ScalarKeyFrameAnimation FadeOf(PillShape shape) => shape switch
    {
        PillShape.Media => _mediaFade,
        PillShape.Privacy => _privacyFade,
        PillShape.General => _generalFade,
        PillShape.Shelf => _shelfFade,
        PillShape.Note => _noteFade,
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

    /// <summary>
    /// The notch's top band: as wide as the widest outline, plus a curve on each side. For the
    /// trapezoid it is as tall as the closed pill and reaches past its slanted sides.
    /// </summary>
    private void UpdateEarHit()
    {
        bool expanded = _expandedHit.Visibility == Visibility.Visible;
        double open = expanded ? _expandedHit.Width + (2 * EarRadius) : 0;
        double closed = _collapsedHit.Width + (2 * (_trapezoid ? Math.Ceiling(TrapezoidReach()) + 1 : EarRadius));
        _earHit.Width = Math.Max(open, closed);
        _earHit.Height = _trapezoid ? CollapsedHeight : EarRadius;
    }

    /// <summary>
    /// How far the trapezoid's top reaches past the closed pill's bottom on each side (DIP): its
    /// slant and the curve into the screen's edge (see DrawSilhouette).
    /// </summary>
    private static double TrapezoidReach()
    {
        double length = Math.Sqrt((TrapezoidSlant * TrapezoidSlant) + (CollapsedHeight * CollapsedHeight));
        return TrapezoidSlant + (TrapezoidFlare * CollapsedHeight / (length + TrapezoidSlant));
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
        foreach (DispatcherQueueTimer timer in new[] { _hoverTimer, _leaveTimer, _cursorTimer, _regionTimer, _peekTimer, _tick, _dropLeaveTimer })
        {
            timer.Stop();
        }

        _hovering = false;
        _pointerInside = false;
        _peek = PillShape.Collapsed;
        _pill = PillShape.Collapsed;
        _onGeneralPage = false;
        _onShelfPage = false;
        _dropping = false;
        _shelfMessage = string.Empty;
        _shelfPicked.Clear();
        _shelfAnchor = null;
        Show(_shelfDropHint, false);
        _mediaContent.IsHitTestVisible = false;
        _generalContent.IsHitTestVisible = false;
        _shelfContent.IsHitTestVisible = false;
        foreach (string property in new[] { "Size", "Offset", "CornerRadius" })
        {
            _geometry.StopAnimation(property);
        }

        _geometry.Size = new Vector2((float)_collapsedWidth, (float)(CollapsedHeight + _topExtension));
        _geometry.Offset = PillOffset(_collapsedWidth, CollapsedHeight);
        _geometry.CornerRadius = new Vector2((float)_collapsedRadius);
        SetSilhouette(_collapsedWidth, CollapsedHeight, _collapsedRadius, animate: false);
        foreach (Visual visual in new[] { _collapsedVisual, _mediaVisual, _privacyVisual, _generalVisual, _shelfVisual, _noteVisual })
        {
            visual.StopAnimation("Opacity");
        }

        _collapsedVisual.Opacity = 1;
        _mediaVisual.Opacity = 0;
        _privacyVisual.Opacity = 0;
        _generalVisual.Opacity = 0;
        _shelfVisual.Opacity = 0;
        _noteVisual.Opacity = 0;
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
        SetSilhouette(width, height, radius, animate: true);
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
        _peekTimer.Interval = TimeSpan.FromMilliseconds(shape == PillShape.Note ? NotePeekMs : PeekMs);
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

        // On a new notification: its list (the general page), and the notification is done.
        _onGeneralPage = _pill == PillShape.Note;
        if (_peek == PillShape.Note)
        {
            _peek = PillShape.Collapsed;
            _peekTimer.Stop();
        }

        _onShelfPage = false;
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
        _onShelfPage = false;

        // A note about the last drop, and the picked rows, are for that visit only.
        _shelfMessage = string.Empty;
        _shelfPicked.Clear();
        _shelfAnchor = null;
        Refresh();
    }

    /// <summary>The arrows: player to general page and back; the shelf back to the general page.</summary>
    private Task<bool> ShowPage(bool general)
    {
        _onShelfPage = false;
        _onGeneralPage = general;
        UpdateArrows();
        Refresh();
        return Task.FromResult(true);
    }

    /// <summary>The general page's shelf button.</summary>
    private Task<bool> ShowShelf()
    {
        _onShelfPage = true;
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
        _trayExtra = rows > 0 ? TrayTitleHeight + (rows * TrayRowHeight) : 0;
        SetGeneralHeight(OpenHeight + _trayExtra + _notesExtra);
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
    /// <summary>The player's height; while it is open, the pill follows.</summary>
    private void SetMediaHeight(double height)
    {
        if (Math.Abs(height - _mediaHeight) < 0.5)
        {
            return;
        }

        _mediaHeight = height;
        _mediaContent.Height = height;
        if (_pill == PillShape.Media)
        {
            HoldRegion(_collapsedWidth);
            (double w, double h, double r) = Dimensions(PillShape.Media, _collapsedWidth);
            AnimatePill(w, h, r);
        }
    }

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

    // ---------------------------------------------------------------- the notification tray

    /// <summary>
    /// The notification tray on the general page: Windows' own list as it is now, newest first
    /// (nothing kept: what Windows no longer holds is gone here too). The list scrolls past
    /// <see cref="MaxListHeight"/>. "Clear all" and each row's X act in Windows too. Rows are
    /// drawn again when the list changes or a notification's pictures arrive.
    /// </summary>
    private void FillNotes()
    {
        if (_closed)
        {
            return;
        }

        NotificationAccess access = _notes.Access;
        IReadOnlyList<NotificationEntry> entries = access == NotificationAccess.Allowed
            ? _notes.Current
            : Array.Empty<NotificationEntry>();

        // Not read yet, or no reader at all: only the button to Windows' own centre.
        string message = access switch
        {
            NotificationAccess.Allowed => entries.Count == 0 ? Strings.DynNoNotifications : string.Empty,
            NotificationAccess.NotAllowed => Strings.DynNotificationsNotAllowed,
            _ => string.Empty,
        };
        Set(_noteMessage, message);
        Show(_noteMessage, message.Length > 0);
        Show(_clearAll, entries.Count > 0);
        _noteMessageHeight = message.Length == 0
            ? 0
            : access == NotificationAccess.NotAllowed ? NoteMessageHeight : NoteLineHeight;

        if (!ReferenceEquals(entries, _notesShown) || _notes.PicturesVersion != _picturesShown)
        {
            _notesShown = entries;
            _picturesShown = _notes.PicturesVersion;
            _noteRows.Children.Clear();
            foreach (NotificationEntry entry in entries)
            {
                _noteRows.Children.Add(NoteRow(entry));
            }
        }

        Show(_noteScroll, entries.Count > 0);
        UpdateNotesHeight();
    }

    /// <summary>
    /// The page's height for the notification tray: the list as tall as its rows (they differ
    /// with pictures and text), up to <see cref="MaxListHeight"/>. Called again whenever the
    /// rows' height changes (a new row, a picture's place).
    /// </summary>
    private void UpdateNotesHeight()
    {
        if (_closed)
        {
            return;
        }

        bool list = _noteScroll.Visibility == Visibility.Visible;
        double listHeight = list ? Math.Min(_noteRows.ActualHeight, MaxListHeight) : 0;
        _notesExtra = NoteTitleExtra + (list ? NotesMargin + listHeight : 0) + _noteMessageHeight;
        SetGeneralHeight(OpenHeight + _trayExtra + _notesExtra);
    }

    /// <summary>
    /// One notification, as Windows' own panel lays it out: the app's small icon, its name and
    /// the time on top; the big picture ("hero"), when there is one; the picture beside the
    /// text (round when the notification asks), the title, the text and the small credit line;
    /// then any pictures below the text. A click opens the app; the X dismisses it (in Windows
    /// too).
    /// </summary>
    private Grid NoteRow(NotificationEntry entry)
    {
        NotificationPictureView? pictures = _notes.PicturesFor(entry.Id);
        string attribution = pictures?.Attribution ?? string.Empty;

        // The top line: the app's icon (as Start shows it), its name, the time.
        FrameworkElement icon = NoteAppIcon(entry) is ImageSource iconSource
            ? new Image { Source = iconSource, Width = NoteIconSize, Height = NoteIconSize, Stretch = Stretch.Uniform }
            : Glyph(0xE7E7, 12, _dimBrush);
        icon.VerticalAlignment = VerticalAlignment.Center;
        TextBlock app = Text(11, FontWeights.Normal, _dimBrush);
        app.Text = entry.AppName;
        TextBlock time = Text(11, FontWeights.Normal, _dimBrush);
        time.Text = NoteTime(entry.Time);
        time.Margin = new Thickness(0, 0, NoteCloseRoom, 0);
        var head = new Grid { ColumnSpacing = 8, Height = NoteHeadHeight };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(app, 1);
        Grid.SetColumn(time, 2);
        head.Children.Add(icon);
        head.Children.Add(app);
        head.Children.Add(time);

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(head);
        if (pictures?.Hero is ImageSource hero)
        {
            content.Children.Add(PictureBox(hero, NoteHeroHeight));
        }

        // The words, with the picture beside them when there is one.
        string body = BodyWithout(entry.Body, attribution);
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Title.Length > 0)
        {
            TextBlock title = Text(12, FontWeights.SemiBold, _whiteBrush);
            title.Text = entry.Title;
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxLines = 2;
            words.Children.Add(title);
        }

        if (body.Length > 0)
        {
            TextBlock text = Text(12, FontWeights.Normal, _dimBrush);
            text.Text = body;
            text.TextWrapping = TextWrapping.Wrap;
            text.MaxLines = 3;
            words.Children.Add(text);
        }

        if (attribution.Length > 0)
        {
            TextBlock credit = Text(11, FontWeights.Normal, _dimBrush);
            credit.Text = attribution;
            words.Children.Add(credit);
        }

        var middle = new Grid { ColumnSpacing = 10 };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (pictures is not null && pictures.Logo is ImageSource logo)
        {
            var fill = new ImageBrush { ImageSource = logo, Stretch = Stretch.UniformToFill };
            FrameworkElement beside = pictures.LogoCircle
                ? new Ellipse { Width = NoteLogoSize, Height = NoteLogoSize, Fill = fill }
                : new Rectangle { Width = NoteLogoSize, Height = NoteLogoSize, RadiusX = 4, RadiusY = 4, Fill = fill };
            beside.VerticalAlignment = VerticalAlignment.Top;
            middle.Children.Add(beside);
        }

        Grid.SetColumn(words, 1);
        middle.Children.Add(words);
        if (words.Children.Count > 0 || middle.Children.Count > 1)
        {
            content.Children.Add(middle);
        }

        if (pictures is not null)
        {
            foreach (ImageSource picture in pictures.Inline)
            {
                content.Children.Add(PictureBox(picture, NoteInlineHeight));
            }
        }

        var open = new Button
        {
            Content = content,
            MinHeight = 0,
            Padding = new Thickness(10, 8, 10, 10),
            CornerRadius = new CornerRadius(Radii.Small),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var said = new List<string>(4);
        foreach (string part in new[] { entry.AppName, entry.Title, body, attribution })
        {
            if (part.Length > 0)
            {
                said.Add(part);
            }
        }

        AutomationProperties.SetName(open, string.Join(", ", said));
        string full = entry.Title.Length > 0 && body.Length > 0
            ? entry.Title + "\n" + body
            : entry.Title + body;
        if (full.Length > 0)
        {
            ToolTipService.SetToolTip(open, full);
        }

        open.Click += (_, _) => OpenNoteApp(entry);

        var dismiss = new Button
        {
            Content = Glyph(0xE711, 10, _whiteBrush),
            Width = 24,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 5, 4, 0),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(dismiss, entry.AppName.Length > 0 ? Fmt.F(Strings.DynDismissFrom, entry.AppName) : Strings.DynDismiss);
        ToolTipService.SetToolTip(dismiss, Strings.DynDismiss);
        uint id = entry.Id;
        dismiss.Click += (_, _) => _notes.Dismiss(id);

        var row = new Grid();
        row.Children.Add(open);
        row.Children.Add(dismiss);
        return row;
    }

    /// <summary>A notification's picture across the row, cut to fill its box (rounded like Windows').</summary>
    private static Rectangle PictureBox(ImageSource source, double height)
    {
        return new Rectangle
        {
            Height = height,
            RadiusX = 6,
            RadiusY = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Fill = new ImageBrush { ImageSource = source, Stretch = Stretch.UniformToFill },
        };
    }

    /// <summary>Windows' text may hold the credit line too; it shows once, below.</summary>
    private static string BodyWithout(string body, string attribution)
    {
        var lines = new List<string>();
        foreach (string line in body.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && !string.Equals(trimmed, attribution, StringComparison.Ordinal))
            {
                lines.Add(trimmed);
            }
        }

        return string.Join(" ", lines);
    }

    /// <summary>
    /// The app's own icon, as Start shows it (a desktop app's shortcut icon, a store app's
    /// unplated logo); else the picture Windows gives for the app; null when neither. Kept per
    /// app (icons of apps, not of notifications).
    /// </summary>
    private ImageSource? NoteAppIcon(NotificationEntry entry)
    {
        if (entry.AppId.Length == 0)
        {
            return null;
        }

        if (_noteIcons.TryGetValue(entry.AppId, out ImageSource? known))
        {
            return known;
        }

        ImageSource? icon = null;
        try
        {
            AppEntry? app = FindApp(entry.AppId);
            if (app?.Icon is IconPixels pixels)
            {
                icon = StartMenuWindow.ToBitmap(pixels);
            }
            else if (app?.LogoBytes is byte[] bytes)
            {
                icon = NotificationService.BitmapFrom(bytes, NoteIconPixels);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Dynaland: an app icon for the notifications could not be made.", ex);
        }

        if (icon is not null)
        {
            _noteIcons[entry.AppId] = icon;
            return icon;
        }

        // Start's list not ready yet, or an app it does not list: tried again next time.
        return _notes.PictureFor(entry.AppId);
    }

    /// <summary>The app in Start's list with this app ID (a store app's, or a shortcut's own).</summary>
    private AppEntry? FindApp(string appId)
    {
        foreach (AppEntry candidate in _host.Catalog.Apps)
        {
            if (string.Equals(candidate.AppUserModelId, appId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.ShortcutAppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Today: the time; before: the date (Windows' language).</summary>
    private static string NoteTime(DateTimeOffset time)
    {
        DateTime local = time.LocalDateTime;
        return local.Date == DateTime.Today
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("d", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// A click on a notification opens its app when Start knows it (Windows gives only the app
    /// itself the notification's own target). Otherwise Windows' notification centre opens,
    /// where the notification itself can be clicked.
    /// </summary>
    private void OpenNoteApp(NotificationEntry entry)
    {
        AppEntry? app = entry.AppId.Length > 0 ? FindApp(entry.AppId) : null;
        if (app is null)
        {
            Log.Info("Dynaland: a notification's app is not in Start's list; Windows' notification centre opens instead.");
            OpenNotifications();
            return;
        }

        _leaveTimer.Stop();
        EndHover();
        if (Launcher.Start(app.LaunchTarget))
        {
            Log.Info("Dynaland: a notification's app was opened.");
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

    // ---------------------------------------------------------------- the file shelf

    /// <summary>True when a drag over Dynaland carries what it can park: files or folders, a web link, or text (not one of its own rows going out).</summary>
    private bool CanTakeDrop(DragEventArgs e)
    {
        DataPackageView view = e.DataView;
        return _shown && !_closed && !_host.Custom.IsOn && !_draggingOut
            && (view.Contains(StandardDataFormats.StorageItems)
                || view.Contains(StandardDataFormats.WebLink)
                || view.Contains(StandardDataFormats.Text));
    }

    /// <summary>
    /// What parking tells the app the files come from: a copy when it allows one, else a
    /// link. Either way nothing is copied (only the paths are kept) and nothing is moved.
    /// </summary>
    private static DataPackageOperation DropOperation(DragEventArgs e)
    {
        if ((e.AllowedOperations & DataPackageOperation.Copy) != 0)
        {
            return DataPackageOperation.Copy;
        }

        return (e.AllowedOperations & DataPackageOperation.Link) != 0 ? DataPackageOperation.Link : DataPackageOperation.None;
    }

    /// <summary>Files, a link or text dragged over the pill: open on the shelf, ready for the drop.</summary>
    private void OnShelfDragOver(object sender, DragEventArgs e)
    {
        DataPackageOperation operation = CanTakeDrop(e) ? DropOperation(e) : DataPackageOperation.None;
        e.AcceptedOperation = operation;
        e.Handled = true;
        if (operation == DataPackageOperation.None)
        {
            return;
        }

        e.DragUIOverride.Caption = Strings.DynShelfAdd;
        _dropLeaveTimer.Stop();
        if (!_dropping)
        {
            _dropping = true;
            UpdateShelfLook();
            Refresh();
        }
    }

    /// <summary>The drag left the pill (or was cancelled) without a drop: back to what it showed before.</summary>
    private void EndDrop()
    {
        if (!_dropping)
        {
            return;
        }

        _dropping = false;
        UpdateShelfLook();
        Refresh();
    }

    /// <summary>
    /// The drop: files' and folders' paths, a web link, or text go on the shelf (items without
    /// a path on this PC, such as a file still inside an archive, and those past a full shelf
    /// are said so), and the shelf stays open as when hovered, until the pointer leaves.
    /// </summary>
    private async void OnShelfDrop(object sender, DragEventArgs e)
    {
        try
        {
            _dropLeaveTimer.Stop();
            e.Handled = true;
            if (!CanTakeDrop(e))
            {
                EndDrop();
                return;
            }

            e.AcceptedOperation = DropOperation(e);
            ShelfParcel parcel;
            DragOperationDeferral deferral = e.GetDeferral();
            try
            {
                parcel = await ReadParcelAsync(e.DataView);
            }
            finally
            {
                deferral.Complete();
            }

            if (_closed)
            {
                return;
            }

            Park(parcel, "dropped");

            // Open on the shelf, as when hovered, until the pointer leaves.
            _dropping = false;
            _hovering = true;
            _onShelfPage = true;
            _onGeneralPage = false;
            _leaveTimer.Stop();
            _cursorTimer.Start();
            UpdateArrows();
            FillShelf();
            Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("Dynaland: the dropped files could not be parked.", ex);
            EndDrop();
        }
    }

    /// <summary>
    /// The shelf page as it is now: its height at once (from how many are parked), its rows a
    /// moment later, once Windows said which files are still there (looked at off the UI
    /// thread: a file on a network drive that is gone can take a while to answer).
    /// </summary>
    private void FillShelf()
    {
        if (_closed)
        {
            return;
        }

        UpdateShelfLook();
        UpdateShelfPicks();
        _ = FillShelfRowsAsync();
    }

    /// <summary>What each row is now: a file, a folder, gone (moved or deleted), a link, or a note.</summary>
    private enum ShelfKind
    {
        Gone,
        File,
        Folder,
        Link,
        Note,
    }

    private async Task FillShelfRowsAsync()
    {
        try
        {
            int version = ++_shelfVersion;
            ShelfItem[] items = new List<ShelfItem>(_host.Shelf.Items).ToArray();
            ShelfKind[] kinds = await Task.Run(() =>
            {
                var found = new ShelfKind[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    string value = items[i].Value;
                    found[i] = items[i].Kind switch
                    {
                        ShelfItemKind.Link => ShelfKind.Link,
                        ShelfItemKind.Note => ShelfKind.Note,
                        _ => File.Exists(value) ? ShelfKind.File
                            : Directory.Exists(value) ? ShelfKind.Folder
                            : ShelfKind.Gone,
                    };
                }

                return found;
            });

            if (_closed || version != _shelfVersion)
            {
                return;
            }

            // Drawn again only when the list or a file's presence changed.
            var signature = new System.Text.StringBuilder();
            var keys = new string[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                keys[i] = items[i].Key;
                signature.Append((int)kinds[i]).Append(keys[i]).Append('\n');
            }

            string shown = signature.ToString();
            if (shown == _shelfShown)
            {
                return;
            }

            _shelfShown = shown;
            _shelfRows.Children.Clear();
            _shelfRowOf.Clear();
            _shelfHovered = null;
            for (int i = 0; i < items.Length; i++)
            {
                _shelfRows.Children.Add(ShelfRow(items[i], kinds[i]));
            }

            // Picks of what left the shelf are let go.
            _shelfPicked.IntersectWith(keys);
            if (_shelfAnchor is not null && !_shelfPicked.Contains(_shelfAnchor) && !_shelfRowOf.ContainsKey(_shelfAnchor))
            {
                _shelfAnchor = null;
            }

            UpdateShelfPicks();

            // The icons of what left the shelf are let go.
            var kept = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            foreach (string key in new List<string>(_shelfIcons.Keys))
            {
                if (!kept.Contains(key))
                {
                    _shelfIcons.Remove(key);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dynaland: the shelf could not be shown.", ex);
        }
    }

    /// <summary>
    /// Around the rows: the drop hint while files are dragged over, the empty shelf's text, a
    /// note (a full shelf, a drop without files, a shelf file that could not be read); and the
    /// page's height: as tall as its rows, up to <see cref="MaxListHeight"/> (then they scroll),
    /// and never smaller than the other pages.
    /// </summary>
    private void UpdateShelfLook()
    {
        int count = _host.Shelf.Items.Count;
        string note = _host.Shelf.IsReadOnlyBecauseOfBadFile ? Strings.DynShelfNotSaved : _shelfMessage;
        Set(_shelfNote, note);
        Show(_shelfNote, note.Length > 0);
        Show(_shelfDropHint, _dropping);
        Show(_shelfEmpty, count == 0 && !_dropping);
        Show(_shelfScroll, count > 0);
        Show(_shelfClearAll, count > 0);

        double list = count == 0
            ? ShelfEmptyHeight
            : Math.Min((count * ShelfRowHeight) + ((count - 1) * ShelfRowSpacing), MaxListHeight);
        double height = ShelfPadding + ShelfTopHeight + (note.Length > 0 ? NoteMessageHeight : 0) + ShelfGap + list;
        SetShelfHeight(Math.Max(OpenHeight, height));
    }

    /// <summary>The shelf page's height; while it is open, the pill follows.</summary>
    private void SetShelfHeight(double height)
    {
        if (Math.Abs(height - _shelfHeight) < 0.5)
        {
            return;
        }

        _shelfHeight = height;
        _shelfContent.Height = height;
        if (_pill == PillShape.Shelf)
        {
            HoldRegion(_collapsedWidth);
            (double w, double h, double r) = Dimensions(PillShape.Shelf, _collapsedWidth);
            AnimatePill(w, h, r);
        }
    }

    /// <summary>
    /// One parked item. A file or folder: its icon as Windows shows it, its name, and its folder;
    /// a click opens it; a drag takes it out (into a folder, a chat, any app that takes files) by
    /// Windows' own rules, as from File Explorer: moved on the same drive, copied to another
    /// (Ctrl copies, Shift moves), together with the other picked files when it is picked. A
    /// link: its page title (or address) and its site; a click opens it in the browser; a drag
    /// gives the link. A note: its first line and how many lines; a click copies it; a drag
    /// gives the text. The X takes it off the shelf (a file stays where it is). Ctrl or Shift +
    /// click picks it. A file moved or deleted since says so and can only be picked or taken off.
    /// </summary>
    private Grid ShelfRow(ShelfItem item, ShelfKind kind)
    {
        string key = item.Key;
        bool present = kind != ShelfKind.Gone;
        bool folder = kind == ShelfKind.Folder;
        bool words = kind is ShelfKind.Link or ShelfKind.Note;
        (string name, string place) = ShelfTexts(item, kind);

        var icon = new Grid { Width = ShelfIconDip, Height = ShelfIconDip, VerticalAlignment = VerticalAlignment.Center };
        if (words)
        {
            FontIcon mark = Glyph(kind == ShelfKind.Link ? ShelfLinkGlyph : ShelfNoteGlyph, 18, _dimBrush);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Children.Add(mark);
        }
        else
        {
            // The type's glyph until Windows' own icon (or picture) is there.
            FontIcon glyph = Glyph(folder ? 0xE8B7 : 0xE8A5, 18, _dimBrush);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            var picture = new Image
            {
                Width = ShelfIconDip,
                Height = ShelfIconDip,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.Children.Add(glyph);
            icon.Children.Add(picture);
            if (present)
            {
                _ = LoadShelfIconAsync(item.Value, folder, picture, glyph);
            }
        }

        TextBlock title = Text(13, FontWeights.Normal, present ? _whiteBrush : _dimBrush);
        title.Text = name;
        TextBlock detail = Text(11, FontWeights.Normal, _dimBrush);
        detail.Text = place;
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(detail);

        Button remove = MakeControl(0xE711, Fmt.F(Strings.DynShelfRemove, name), () => Task.FromResult(_host.Shelf.Remove(key)), out FontIcon removeIcon);
        SmallArrow(remove, removeIcon);
        remove.VerticalAlignment = VerticalAlignment.Center;

        var row = new Grid
        {
            Height = ShelfRowHeight,
            ColumnSpacing = 10,
            Padding = new Thickness(6, 0, 0, 0),
            CornerRadius = new CornerRadius(Radii.Small),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(icon);
        row.Children.Add(texts);
        row.Children.Add(remove);
        AutomationProperties.SetName(row, name + ", " + place);

        _shelfRowOf[key] = (row, kind);
        row.PointerEntered += (_, _) =>
        {
            _shelfHovered = key;
            PaintShelfRow(key);
        };
        row.PointerExited += (_, _) =>
        {
            if (string.Equals(_shelfHovered, key, StringComparison.OrdinalIgnoreCase))
            {
                _shelfHovered = null;
            }

            PaintShelfRow(key);
        };
        row.Tapped += (_, e) =>
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject))
            {
                return;
            }

            e.Handled = true;
            ClickShelfRow(item, present);
        };
        PaintShelfRow(key);
        if (!present)
        {
            return row;
        }

        row.CanDrag = true;
        if (words)
        {
            row.DragStarting += (_, e) => StartWordsDrag(item, e);
            row.DropCompleted += (_, _) => _draggingOut = false;
            return row;
        }

        string path = item.Value;
        row.DragStarting += async (_, e) =>
        {
            List<(string Path, ShelfKind Kind)> taking = ToTakeOut(path, kind);
            DragOperationDeferral deferral = e.GetDeferral();
            try
            {
                // One that cannot be had now (gone a moment ago) is left out; the others go.
                var items = new List<IStorageItem>();
                var paths = new List<string>();
                foreach ((string one, ShelfKind oneKind) in taking)
                {
                    try
                    {
                        IStorageItem storage;
                        if (oneKind == ShelfKind.Folder)
                        {
                            storage = await StorageFolder.GetFolderFromPathAsync(one);
                        }
                        else
                        {
                            storage = await StorageFile.GetFileFromPathAsync(one);
                        }

                        items.Add(storage);
                        paths.Add(one);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Dynaland: a shelf item was left out of a drag (it could not be had).", ex);
                    }
                }

                if (items.Count == 0)
                {
                    e.Cancel = true;
                    return;
                }

                e.Data.SetStorageItems(items);
                e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
                _shelfDragged = paths;
                _draggingOut = true;
                Log.Info("Dynaland: " + items.Count.ToString(CultureInfo.InvariantCulture) + " shelf item(s) being dragged out.");
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                Log.Warn("Dynaland: shelf items could not be dragged out.", ex);
            }
            finally
            {
                deferral.Complete();
            }
        };
        row.DropCompleted += (_, _) =>
        {
            _draggingOut = false;
            List<string> dragged = _shelfDragged ?? new List<string> { path };
            _shelfDragged = null;
            _ = ForgetMovedAsync(dragged);
        };
        return row;
    }

    /// <summary>A row's two lines: a file's name and folder; a link's title (or address) and site; a note's first line and how many lines.</summary>
    private static (string Name, string Place) ShelfTexts(ShelfItem item, ShelfKind kind)
    {
        switch (kind)
        {
            case ShelfKind.Link:
                if (!Uri.TryCreate(item.Value, UriKind.Absolute, out Uri? uri))
                {
                    return (item.Value, string.Empty);
                }

                string shown = item.Title.Length > 0 ? item.Title : (uri.Host + uri.PathAndQuery).TrimEnd('/');
                return (shown, Fmt.F(Strings.DynShelfLink, uri.Host));
            case ShelfKind.Note:
                string text = item.Value.Replace("\r\n", "\n", StringComparison.Ordinal);
                string first = string.Empty;
                foreach (string line in text.Split('\n'))
                {
                    if (line.Trim().Length > 0)
                    {
                        first = line.Trim();
                        break;
                    }
                }

                if (first.Length > 200)
                {
                    first = first[..200];
                }

                int lines = text.TrimEnd().Split('\n').Length;
                return (first, lines > 1 ? Fmt.F(Strings.DynShelfNoteLines, lines) : Strings.DynShelfNote);
            default:
                bool present = kind != ShelfKind.Gone;
                return (ShelfName(item.Value), present ? Path.GetDirectoryName(item.Value) ?? string.Empty : Strings.DynShelfMissing);
        }
    }

    /// <summary>A link or a note dragged out: the link (and its address as text), or the text; copied, never taken off the shelf.</summary>
    private void StartWordsDrag(ShelfItem item, DragStartingEventArgs e)
    {
        try
        {
            bool link = item.Kind == ShelfItemKind.Link;
            if (link && Uri.TryCreate(item.Value, UriKind.Absolute, out Uri? uri))
            {
                e.Data.SetWebLink(uri);
                if (item.Title.Length > 0)
                {
                    e.Data.Properties.Title = item.Title;
                }
            }

            e.Data.SetText(item.Value);
            e.AllowedOperations = link ? DataPackageOperation.Copy | DataPackageOperation.Link : DataPackageOperation.Copy;
            _draggingOut = true;
            Log.Info("Dynaland: a shelf " + (link ? "link" : "note") + " being dragged out.");
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            Log.Warn("Dynaland: a shelf link or note could not be dragged out.", ex);
        }
    }

    /// <summary>
    /// A click on a row. Ctrl (or Shift with nothing picked yet) + click picks it or lets it go;
    /// Shift + click picks every row from the last one picked to this one (Ctrl + Shift adds them
    /// to the picked ones). A plain click opens a file or a link (and Dynaland closes), or copies
    /// a note (Dynaland stays); a gone file does nothing.
    /// The keys are read as they are now: Dynaland never takes the keyboard.
    /// </summary>
    private void ClickShelfRow(ShelfItem item, bool present)
    {
        string key = item.Key;
        bool ctrl = KeyIsDown(User32.VK_CONTROL);
        bool shift = KeyIsDown(User32.VK_SHIFT);
        if (shift && _shelfAnchor is not null && _shelfRowOf.ContainsKey(_shelfAnchor))
        {
            if (!ctrl)
            {
                _shelfPicked.Clear();
            }

            List<string> keys = ShelfKeys();
            int from = ShelfIndex(keys, _shelfAnchor);
            int to = ShelfIndex(keys, key);
            if (from >= 0 && to >= 0)
            {
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                {
                    _shelfPicked.Add(keys[i]);
                }
            }

            UpdateShelfPicks();
            return;
        }

        if (ctrl || shift)
        {
            if (!_shelfPicked.Remove(key))
            {
                _shelfPicked.Add(key);
            }

            _shelfAnchor = key;
            UpdateShelfPicks();
            return;
        }

        switch (item.Kind)
        {
            case ShelfItemKind.Link:
                OpenShelfLink(item.Value);
                EndHover();
                break;
            case ShelfItemKind.Note:
                CopyShelfNote(item.Value);
                break;
            default:
                if (!present)
                {
                    return;
                }

                Log.Info("Dynaland: a shelf item was opened.");
                FileActions.Open(item.Value);
                EndHover();
                break;
        }
    }

    /// <summary>A link from the shelf in the default browser (only http and https are ever parked).</summary>
    private static void OpenShelfLink(string address)
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            Log.Info("Dynaland: a shelf link was opened.");
        }
        catch (Exception ex)
        {
            Log.Error("Dynaland: a shelf link could not be opened.", ex);
        }
    }

    /// <summary>A note from the shelf onto the clipboard, said under the shelf's title.</summary>
    private void CopyShelfNote(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            WinClipboard.SetContent(package);
            WinClipboard.Flush();
            _shelfMessage = Strings.DynShelfCopied;
            Log.Info("Dynaland: a shelf note was copied.");
        }
        catch (Exception ex)
        {
            _shelfMessage = Strings.DynShelfClipboardFailed;
            Log.Error("Dynaland: a shelf note could not be copied.", ex);
        }

        UpdateShelfLook();
    }

    /// <summary>The shelf's keys, newest first.</summary>
    private List<string> ShelfKeys()
    {
        var keys = new List<string>(_host.Shelf.Items.Count);
        foreach (ShelfItem item in _host.Shelf.Items)
        {
            keys.Add(item.Key);
        }

        return keys;
    }

    private static bool KeyIsDown(int virtualKey)
    {
        return (User32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static int ShelfIndex(IReadOnlyList<string> paths, string path)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            if (string.Equals(paths[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// What a drag from a file's row takes: all the picked files and folders still there, in the
    /// shelf's order, when it is picked (picked links and notes stay); else itself.
    /// </summary>
    private List<(string Path, ShelfKind Kind)> ToTakeOut(string path, ShelfKind kind)
    {
        var taking = new List<(string Path, ShelfKind Kind)>();
        if (_shelfPicked.Contains(path))
        {
            foreach (ShelfItem item in _host.Shelf.Items)
            {
                string one = item.Key;
                if (item.Kind == ShelfItemKind.File
                    && _shelfPicked.Contains(one)
                    && _shelfRowOf.TryGetValue(one, out (Grid Row, ShelfKind Kind) shown)
                    && shown.Kind is ShelfKind.File or ShelfKind.Folder)
                {
                    taking.Add((item.Value, shown.Kind));
                }
            }
        }

        if (taking.Count == 0)
        {
            taking.Add((path, kind));
        }

        return taking;
    }

    /// <summary>
    /// The title row and the rows after the picks changed: how many are picked, the button that
    /// takes them off (while some are), the one that selects all or none (from two rows on, or
    /// while some are picked), and each row's look.
    /// </summary>
    private void UpdateShelfPicks()
    {
        int rows = _shelfRowOf.Count;
        int picked = _shelfPicked.Count;
        bool all = rows > 0 && picked >= rows;
        Set(_shelfCount, picked > 0 ? Fmt.F(Strings.DynShelfCount, picked) : string.Empty);
        Show(_shelfCount, picked > 0);
        Show(_shelfRemovePicked, picked > 0);
        Show(_shelfSelectAll, rows >= 2 || picked > 0);
        _shelfSelectAllIcon.Glyph = ((char)(all ? 0xE8E6 : 0xE8B3)).ToString();
        AutomationProperties.SetName(_shelfSelectAll, all ? Strings.DynShelfSelectNone : Strings.DynShelfSelectAll);
        foreach (string path in _shelfRowOf.Keys)
        {
            PaintShelfRow(path);
        }
    }

    /// <summary>A row's background: brighter when picked, a little brighter under the pointer.</summary>
    private void PaintShelfRow(string path)
    {
        if (!_shelfRowOf.TryGetValue(path, out (Grid Row, ShelfKind Kind) shown))
        {
            return;
        }

        bool picked = _shelfPicked.Contains(path);
        bool hovered = string.Equals(_shelfHovered, path, StringComparison.OrdinalIgnoreCase);
        shown.Row.Background = picked
            ? hovered ? _shelfPickedHoverBrush : _shelfPickedBrush
            : hovered ? _shelfHoverBrush : _shelfStillBrush;
        AutomationProperties.SetItemStatus(shown.Row, picked ? Strings.DynShelfIsSelected : string.Empty);
    }

    /// <summary>The title row's select button: all rows, or none when all are picked.</summary>
    private Task<bool> ToggleSelectAll()
    {
        bool all = _shelfRowOf.Count > 0 && _shelfPicked.Count >= _shelfRowOf.Count;
        _shelfPicked.Clear();
        _shelfAnchor = null;
        if (!all)
        {
            foreach (string path in _shelfRowOf.Keys)
            {
                _shelfPicked.Add(path);
            }
        }

        UpdateShelfPicks();
        return Task.FromResult(true);
    }

    /// <summary>The title row's "Clear all": everything off the shelf (the files stay where they are), as the notifications' one, at once.</summary>
    private void ClearShelf()
    {
        _shelfPicked.Clear();
        _shelfAnchor = null;
        int removed = _host.Shelf.RemoveMany(ShelfKeys());
        Log.Info("Dynaland: the shelf was cleared (" + removed.ToString(CultureInfo.InvariantCulture) + " item(s) taken off).");
        UpdateShelfPicks();
    }

    /// <summary>The title row's button: the picked ones off the shelf (the files stay where they are).</summary>
    private Task<bool> RemovePicked()
    {
        var picked = new List<string>(_shelfPicked);
        _shelfPicked.Clear();
        _shelfAnchor = null;
        int removed = _host.Shelf.RemoveMany(picked);
        Log.Info("Dynaland: " + removed.ToString(CultureInfo.InvariantCulture) + " shelf item(s) taken off.");
        UpdateShelfPicks();
        return Task.FromResult(removed > 0);
    }

    /// <summary>A parked item's name: the file's or folder's own, or the drive ("D:\") for a drive.</summary>
    private static string ShelfName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }

    /// <summary>Windows' own icon for the file or folder (a picture's or a video's thumbnail), kept while it is parked.</summary>
    private async Task LoadShelfIconAsync(string path, bool folder, Image picture, FontIcon glyph)
    {
        try
        {
            if (!_shelfIcons.TryGetValue(path, out ImageSource? source))
            {
                uint pixels = (uint)Math.Ceiling(ShelfIconDip * (_root.XamlRoot?.RasterizationScale ?? 1) * _size);
                StorageItemThumbnail? thumbnail;
                if (folder)
                {
                    StorageFolder item = await StorageFolder.GetFolderFromPathAsync(path);
                    thumbnail = await item.GetThumbnailAsync(ThumbnailMode.SingleItem, pixels, ThumbnailOptions.ResizeThumbnail);
                }
                else
                {
                    StorageFile item = await StorageFile.GetFileFromPathAsync(path);
                    thumbnail = await item.GetThumbnailAsync(ThumbnailMode.SingleItem, pixels, ThumbnailOptions.ResizeThumbnail);
                }

                using (thumbnail)
                {
                    if (thumbnail is null || thumbnail.Size == 0)
                    {
                        return;
                    }

                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumbnail);
                    source = bitmap;
                    _shelfIcons[path] = bitmap;
                }
            }

            if (!_closed)
            {
                picture.Source = source;
                glyph.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Dynaland: a shelf icon could not be shown.", ex);
        }
    }

    /// <summary>What a drop or the clipboard carries for the shelf: files and folders, else a web link, else text.</summary>
    private sealed class ShelfParcel
    {
        internal List<string> Paths { get; } = new();

        /// <summary>Items without a path on this PC (a file still inside an archive, say).</summary>
        internal int NoPath { get; set; }

        internal string? Link { get; set; }

        internal string Title { get; set; } = string.Empty;

        internal string? Text { get; set; }
    }

    /// <summary>
    /// Reads a drop's or the clipboard's content: files and folders first; else a web link (with
    /// its page title when given); else text, which is a link when it is one web address alone
    /// and a note otherwise.
    /// </summary>
    private static async Task<ShelfParcel> ReadParcelAsync(DataPackageView view)
    {
        var parcel = new ShelfParcel();
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            IReadOnlyList<IStorageItem> items = await view.GetStorageItemsAsync();
            foreach (IStorageItem item in items)
            {
                if (string.IsNullOrEmpty(item.Path))
                {
                    parcel.NoPath++;
                }
                else
                {
                    parcel.Paths.Add(item.Path);
                }
            }

            return parcel;
        }

        if (view.Contains(StandardDataFormats.WebLink))
        {
            Uri? link = await view.GetWebLinkAsync();
            if (ShelfStore.TryLink(link?.OriginalString, out string address))
            {
                parcel.Link = address;
                parcel.Title = view.Properties.Title ?? string.Empty;
                return parcel;
            }
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            string text = await view.GetTextAsync() ?? string.Empty;
            if (ShelfStore.TryLink(text, out string address))
            {
                parcel.Link = address;
            }
            else
            {
                parcel.Text = text;
            }
        }

        return parcel;
    }

    /// <summary>
    /// Puts what was read on the shelf and says under its title what could not go (a full shelf,
    /// items without a path, text cut, nothing usable). The log gets how many, never a link or text.
    /// </summary>
    private void Park(ShelfParcel parcel, string how)
    {
        if (parcel.Paths.Count > 0 || parcel.NoPath > 0)
        {
            ShelfAdded result = _host.Shelf.Add(parcel.Paths);
            _shelfMessage = result.Full > 0 ? Fmt.F(Strings.DynShelfFull, ShelfStore.MaxItems, result.Full)
                : parcel.NoPath > 0 ? Fmt.F(Strings.DynShelfNoPath, parcel.NoPath)
                : string.Empty;
            Log.Info("Dynaland: " + result.Added.ToString(CultureInfo.InvariantCulture) + " item(s) " + how + " on the shelf"
                + (result.Full > 0 ? ", " + result.Full.ToString(CultureInfo.InvariantCulture) + " turned away (the shelf is full)" : string.Empty)
                + (parcel.NoPath > 0 ? ", " + parcel.NoPath.ToString(CultureInfo.InvariantCulture) + " without a path on this PC" : string.Empty) + ".");
            return;
        }

        if (parcel.Link is string link)
        {
            ShelfAdded result = _host.Shelf.AddLink(link, parcel.Title);
            _shelfMessage = result.Full > 0 ? Fmt.F(Strings.DynShelfFull, ShelfStore.MaxItems, result.Full) : string.Empty;
            Log.Info("Dynaland: a link " + how + (result.Full > 0 ? ", turned away (the shelf is full)." : " on the shelf."));
            return;
        }

        if (parcel.Text is string text && text.Trim().Length > 0)
        {
            ShelfAdded result = _host.Shelf.AddNote(text);
            bool cut = text.TrimEnd().Length > ShelfStore.MaxNoteLength;
            _shelfMessage = result.Full > 0 ? Fmt.F(Strings.DynShelfFull, ShelfStore.MaxItems, result.Full)
                : cut ? Fmt.F(Strings.DynShelfNoteCut, ShelfStore.MaxNoteLength)
                : string.Empty;
            Log.Info("Dynaland: a note (" + Math.Min(text.Length, ShelfStore.MaxNoteLength).ToString(CultureInfo.InvariantCulture) + " characters) " + how
                + (result.Full > 0 ? ", turned away (the shelf is full)." : " on the shelf."));
            return;
        }

        _shelfMessage = Strings.DynShelfNothing;
        Log.Info("Dynaland: nothing to put on the shelf (" + how + ": no files, link or text).");
    }

    /// <summary>The shelf's Paste button: the clipboard's files and folders, link or text onto the shelf.</summary>
    private async Task<bool> PasteToShelfAsync()
    {
        try
        {
            ShelfParcel parcel = await ReadParcelAsync(WinClipboard.GetContent());
            if (_closed)
            {
                return false;
            }

            Park(parcel, "pasted");
        }
        catch (Exception ex)
        {
            _shelfMessage = Strings.DynShelfClipboardFailed;
            Log.Error("Dynaland: the clipboard could not be read for the shelf.", ex);
        }

        UpdateShelfLook();
        return true;
    }

    /// <summary>
    /// After a drag out: the files that left their place (moved; Windows may finish a moment
    /// later) leave the shelf too; copies stay. Looked at for up to 10 seconds.
    /// </summary>
    private async Task ForgetMovedAsync(List<string> dragged)
    {
        try
        {
            var waiting = new List<string>(dragged);
            for (int i = 0; i < 20 && !_closed && waiting.Count > 0; i++)
            {
                string[] looking = waiting.ToArray();
                List<string> gone = await Task.Run(() =>
                {
                    var away = new List<string>();
                    foreach (string one in looking)
                    {
                        if (!File.Exists(one) && !Directory.Exists(one))
                        {
                            away.Add(one);
                        }
                    }

                    return away;
                });

                if (gone.Count > 0)
                {
                    int removed = _host.Shelf.RemoveMany(gone);
                    Log.Info("Dynaland: " + removed.ToString(CultureInfo.InvariantCulture) + " shelf item(s) moved out, so they left the shelf.");
                    waiting.RemoveAll(gone.Contains);
                }

                if (waiting.Count > 0)
                {
                    await Task.Delay(500);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Dynaland: a shelf item could not be looked at after it was dragged out.", ex);
        }
    }

    // ---------------------------------------------------------------- music

    private void ApplyMedia()
    {
        MediaSnapshot media = _media.Current;
        bool has = media.HasMedia;

        if (has)
        {
            ShowMiniText(media);
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

        FollowLyrics(media);
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

    /// <summary>A volume key Machitan handled (VolumeKeys): the new level shows now, not at the next reading.</summary>
    internal void ShowVolumeNow()
    {
        if (_shown && !_closed)
        {
            PollVolume();
        }
    }

    /// <summary>
    /// Four times a second, while Dynaland is on screen: the volume (a change shows the level for
    /// 1.8 s; the first reading only sets the start), the clipboard, and once a second the charger.
    /// </summary>
    private void PollVolume()
    {
        _volumeTicks++;
        PollClipboard();
        if (_volumeTicks % 4 == 1)
        {
            PollPower();
        }
        if (_volumeTicks % 20 == 1 || !_volume.IsAvailable)
        {
            // The default device can change.
            _volume.Connect();
        }

        if (!_volume.TryRead(out float level, out bool muted))
        {
            _volumeKnown = false;
            ShowPlayerVolume();
            return;
        }

        int percent = Math.Clamp((int)Math.Round(level * 100), 0, 100);
        if (!_volumeKnown)
        {
            _volumeKnown = true;
            _volumePercent = percent;
            _volumeMuted = muted;
            ShowVolumeLevel();
            ShowPlayerVolume();
            return;
        }

        if (percent == _volumePercent && muted == _volumeMuted)
        {
            return;
        }

        if (_playerVolumeDragging)
        {
            // Windows catching up with the drag: the bar already shows it.
            return;
        }

        _volumePercent = percent;
        _volumeMuted = muted;
        ShowVolumeLevel();
        ShowPlayerVolume();

        // Windows' flyout for this change (from a key Machitan did not see) stays hidden.
        _flyouts.LevelChanged();
        StartMoment(CollapsedForm.Volume, VolumeShowMs);
    }

    /// <summary>The player's volume line as Windows has the volume now (hidden without speakers).</summary>
    private void ShowPlayerVolume()
    {
        bool known = _volume.IsAvailable && _volumeKnown;
        Show(_playerVolume, known);
        if (!known)
        {
            return;
        }

        int glyph = _volumeMuted || _volumePercent == 0 ? 0xE74F
            : _volumePercent > 66 ? 0xE995
            : _volumePercent > 33 ? 0xE994
            : 0xE993;
        _playerMuteIcon.Glyph = ((char)glyph).ToString();

        // No number on screen; screen readers still hear the level.
        AutomationProperties.SetItemStatus(_playerVolumeArea, _volumeMuted ? Strings.DynMuted : _volumePercent.ToString(CultureInfo.CurrentCulture));
        double done = _volumeMuted ? 0 : _volumePercent / 100.0;
        _playerVolumeDone.Width = new GridLength(Math.Max(done, 0.0001), GridUnitType.Star);
        _playerVolumeLeft.Width = new GridLength(Math.Max(1 - done, 0.0001), GridUnitType.Star);
        _playerVolumeThumbMove.X = (done * _playerVolumeArea.ActualWidth) - (_playerVolumeThumb.Width / 2);
    }

    /// <summary>The thumb shows while the pointer is over the volume bar or dragging it.</summary>
    private void UpdatePlayerVolumeThumb()
    {
        Show(_playerVolumeThumb, _playerVolumeHover || _playerVolumeDragging);
    }

    /// <summary>A press or drag on the volume bar: the level under the pointer.</summary>
    private void SetVolumeFromBar(double x)
    {
        double width = _playerVolumeArea.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        SetPlayerVolume((int)Math.Round(Math.Clamp(x / width, 0, 1) * 100));
        UpdatePlayerVolumeThumb();
    }

    /// <summary>
    /// Sets Windows' volume from Dynaland's player. Like Windows' own slider, moving it while
    /// muted turns mute off. What is set here is not shown again as a change on the pill.
    /// </summary>
    private void SetPlayerVolume(int percent)
    {
        if (!_volume.IsAvailable)
        {
            return;
        }

        percent = Math.Clamp(percent, 0, 100);
        _volume.SetLevel(percent / 100f);
        if (_volumeMuted)
        {
            _volume.SetMute(false);
        }

        if (_volume.TryRead(out float level, out bool muted))
        {
            _volumeKnown = true;
            _volumePercent = Math.Clamp((int)Math.Round(level * 100), 0, 100);
            _volumeMuted = muted;
        }

        ShowVolumeLevel();
        ShowPlayerVolume();
    }

    /// <summary>The player's mute button: mute or unmute Windows' default speakers.</summary>
    private Task<bool> ToggleMuteAsync()
    {
        if (!_volume.IsAvailable)
        {
            return Task.FromResult(false);
        }

        _volume.SetMute(!_volumeMuted);
        if (_volume.TryRead(out float level, out bool muted))
        {
            _volumeKnown = true;
            _volumePercent = Math.Clamp((int)Math.Round(level * 100), 0, 100);
            _volumeMuted = muted;
        }

        ShowVolumeLevel();
        ShowPlayerVolume();
        return Task.FromResult(true);
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

    // ---------------------------------------------------------------- moments: brightness, charger, copy

    /// <summary>One short moment on the closed pill; a newer one replaces the one before.</summary>
    private void StartMoment(CollapsedForm form, int showMs)
    {
        long until = Environment.TickCount64 + showMs;
        _volumeUntil = form == CollapsedForm.Volume ? until : 0;
        _brightnessUntil = form == CollapsedForm.Brightness ? until : 0;
        _powerUntil = form == CollapsedForm.Power ? until : 0;
        _clipUntil = form == CollapsedForm.Clipboard ? until : 0;
        _volumeTimer.Stop();
        _volumeTimer.Interval = TimeSpan.FromMilliseconds(showMs + 20);
        _volumeTimer.Start();
        Refresh();
    }

    /// <summary>
    /// What Windows may not show itself now: its volume and brightness flyout, and its popups for
    /// new notifications, only while Dynaland is on screen to show them instead (each with its
    /// option; the popups only while Windows lets Machitan read the notifications).
    /// </summary>
    private void UpdateFlyouts()
    {
        if (_closed)
        {
            return;
        }

        ShellSettings settings = _host.Settings.Current;
        bool levels = _shown && settings.DynalandLevels;
        bool popups = _shown && settings.DynalandNotifications && _notes.Access == NotificationAccess.Allowed;
        _flyouts.Set(levels, popups);
        _brightness.SetWanted(levels);
    }

    /// <summary>
    /// Windows reported a new brightness level. Shown with Windows' brightness flyout (hidden):
    /// for the brightness keys, not for the screen dimming by itself, which Windows shows no flyout for.
    /// </summary>
    private void OnBrightnessChanged(int percent)
    {
        _brightnessPercent = percent;
        _brightnessAt = Environment.TickCount64;
        _flyouts.LevelChanged();
        if (_brightnessAt - _levelFlyoutAt <= BrightnessPairMs)
        {
            ShowBrightness();
        }
    }

    /// <summary>Windows showed its volume and brightness flyout (now hidden): a brightness level just reported shows now.</summary>
    private void OnLevelFlyoutShown()
    {
        _levelFlyoutAt = Environment.TickCount64;
        if (_brightnessPercent >= 0 && _levelFlyoutAt - _brightnessAt <= BrightnessPairMs)
        {
            ShowBrightness();
        }
    }

    private void ShowBrightness()
    {
        if (!_shown || _closed)
        {
            return;
        }

        Set(_brightnessText, _brightnessPercent.ToString(CultureInfo.CurrentCulture));
        double done = _brightnessPercent / 100.0;
        _brightnessDone.Width = new GridLength(Math.Max(done, 0.0001), GridUnitType.Star);
        _brightnessLeft.Width = new GridLength(Math.Max(1 - done, 0.0001), GridUnitType.Star);
        AutomationProperties.SetName(_brightnessRow, Fmt.F(Strings.DynBrightnessLevel, _brightnessPercent));
        StartMoment(CollapsedForm.Brightness, BrightnessShowMs);
    }

    /// <summary>
    /// Once a second: the charger plugged in or out shows on the pill for 3 s (with the option
    /// on, on a PC with a battery). The first reading only sets the start. Charging often begins
    /// a moment after the plug goes in, so the words follow while the moment runs.
    /// </summary>
    private void PollPower()
    {
        if (!Kernel32.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status)
            || status.ACLineStatus > 1
            || (status.BatteryFlag & Kernel32.BATTERY_FLAG_NO_BATTERY) != 0)
        {
            _powerState = -1;
            return;
        }

        int state = status.ACLineStatus;
        bool changed = _powerState >= 0 && state != _powerState;
        bool running = Environment.TickCount64 < _powerUntil;
        _powerState = state;
        _powerCharging = (status.BatteryFlag & Kernel32.BATTERY_FLAG_CHARGING) != 0;
        _powerPercent = status.BatteryLifePercent == Kernel32.BATTERY_PERCENT_UNKNOWN ? -1 : Math.Min((int)status.BatteryLifePercent, 100);
        if (!_host.Settings.Current.DynalandCharging)
        {
            return;
        }

        if (changed)
        {
            ShowPower();
            StartMoment(CollapsedForm.Power, PowerShowMs);
        }
        else if (running)
        {
            ShowPower();
            Refresh();
        }
    }

    private void ShowPower()
    {
        string words = _powerState == 1
            ? (_powerCharging ? Strings.DynCharging : Strings.DynPluggedIn)
            : Strings.DynOnBattery;
        Set(_powerText, _powerPercent >= 0 ? Fmt.F(Strings.DynPowerLevel, words, _powerPercent) : words);

        // Segoe Fluent Icons: Battery0-9 E850-E859, Battery10 E83F; BatteryCharging0-8 E85A-E862, BatteryCharging9 E83E.
        int step = _powerPercent < 0 ? 10 : Math.Clamp(_powerPercent / 10, 0, 10);
        int glyph = _powerState == 1
            ? (step >= 9 ? 0xE83E : 0xE85A + step)
            : (step >= 10 ? 0xE83F : 0xE850 + step);
        _powerIcon.Glyph = ((char)glyph).ToString();
    }

    /// <summary>
    /// Four times a second: a change of the clipboard shows "Copied" or "Cut" (with the option on).
    /// Only the kind of what is there (files, a picture, other) and whether File Explorer cut or
    /// copied it are looked at; the content is never read. The first reading only sets the start;
    /// while the app that changed it still holds the clipboard, it is tried again (up to a second).
    /// An emptied clipboard shows nothing.
    /// </summary>
    private void PollClipboard()
    {
        uint sequence = User32.GetClipboardSequenceNumber();
        if (!_clipKnown)
        {
            _clipKnown = true;
            _clipSequence = sequence;
            _clipTries = 0;
            return;
        }

        if (sequence != _clipSequence)
        {
            _clipSequence = sequence;
            _clipTries = 4;
        }

        if (_clipTries == 0)
        {
            return;
        }

        _clipTries--;
        if (!_host.Settings.Current.DynalandClipboard)
        {
            _clipTries = 0;
            return;
        }

        try
        {
            Windows.ApplicationModel.DataTransfer.DataPackageView view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (view.AvailableFormats.Count == 0)
            {
                _clipTries = 0;
                return;
            }

            bool files = view.Contains(StandardDataFormats.StorageItems);
            bool picture = !files && view.Contains(StandardDataFormats.Bitmap);
            DataPackageOperation operation = files ? view.RequestedOperation : DataPackageOperation.None;
            bool cut = (operation & DataPackageOperation.Move) != 0 && (operation & DataPackageOperation.Copy) == 0;
            _clipTries = 0;
            _clipIcon.Glyph = ((char)(cut ? 0xE8C6 : picture ? 0xE8B9 : 0xE8C8)).ToString();
            Set(_clipText, files ? (cut ? Strings.DynFilesCut : Strings.DynFilesCopied) : picture ? Strings.DynPictureCopied : Strings.DynCopied);
            if (_shown && !_closed)
            {
                StartMoment(CollapsedForm.Clipboard, ClipboardShowMs);
            }
        }
        catch (Exception)
        {
            // Another app still holds the clipboard: tried again at the next reading.
        }
    }

    // ---------------------------------------------------------------- a new notification

    /// <summary>Windows showed a popup for a new notification and it was hidden: find the notification in Windows' list now.</summary>
    private void OnPopupHidden()
    {
        long now = Environment.TickCount64;
        _noteWantedUntil = now + NoteWantedMs;
        _popupSessionUntil = now + PopupSessionMs;
        _notes.ReadNow();
        _noteRetry.Start();
        ShowNewNote();
    }

    private void OnNotesChangedForPeek()
    {
        // Windows may have changed whether Machitan can read the notifications.
        UpdateFlyouts();
        ShowNewNote();

        // A notification's pictures arrive a moment after it: the open notification gets them.
        if (_peek == PillShape.Note && _notePeekId is uint id && _notes.PicturesVersion != _notePeekPicturesVersion)
        {
            foreach (NotificationEntry entry in _notes.Current)
            {
                if (entry.Id == id)
                {
                    FillNotePeek(entry, _notePeekMore);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// While a hidden popup waits, and for 8 s after one (Windows may add more notifications to
    /// its popup without showing it again): the newest notification Windows created in the last
    /// few seconds and Dynaland has not shown yet opens a little for 5 s (with how many more came
    /// with it), and Windows' popup stays hidden. Not found in time: Windows shows its popup
    /// again (WindowsFlyouts). Only ids Windows still holds are remembered.
    /// </summary>
    private void ShowNewNote()
    {
        IReadOnlyList<NotificationEntry> current = _notes.Current;
        if (_noteShownIds.Count > 0)
        {
            var held = new HashSet<uint>();
            foreach (NotificationEntry entry in current)
            {
                held.Add(entry.Id);
            }

            _noteShownIds.IntersectWith(held);
        }

        long now = Environment.TickCount64;
        if (now > _noteWantedUntil && now > _popupSessionUntil)
        {
            _noteRetry.Stop();
            NoteUnpaired(current);
            return;
        }

        DateTimeOffset since = DateTimeOffset.Now.AddSeconds(-NewNoteSeconds);
        NotificationEntry? newest = null;
        int count = 0;
        foreach (NotificationEntry entry in current)
        {
            if (entry.Time >= since && !_noteShownIds.Contains(entry.Id))
            {
                // The list is newest first.
                newest ??= entry;
                count++;
                _noteShownIds.Add(entry.Id);
            }
        }

        if (newest is null)
        {
            return;
        }

        // The popup that waited has its notification; more may still come with it (the session).
        _noteWantedUntil = 0;
        _flyouts.PopupShownInDynaland();
        FillNotePeek(newest, count - 1);
        if (_shown && !_closed && !_hovering)
        {
            Peek(PillShape.Note);
        }
    }

    /// <summary>
    /// Test lines only (v0.9.3 test build): a notification Windows created 3 to 15 s ago that
    /// Dynaland never showed came with no popup Machitan saw (Do not disturb, an app whose
    /// popups are off, or a popup Machitan missed). Never the notification's app, title or text.
    /// </summary>
    private void NoteUnpaired(IReadOnlyList<NotificationEntry> current)
    {
        if (_noteTestLines >= 20 || !_shown || !_host.Settings.Current.DynalandNotifications || _notes.Access != NotificationAccess.Allowed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        foreach (NotificationEntry entry in current)
        {
            double age = (now - entry.Time).TotalSeconds;
            if (age >= UnpairedNoteSeconds && age <= NewNoteSeconds && !_noteShownIds.Contains(entry.Id) && _noteUnpairedLogged.Add(entry.Id))
            {
                _noteTestLines++;
                Log.Info("Dynaland (test): a new notification in Windows' list came with no popup Machitan saw ("
                    + Math.Round(age).ToString(CultureInfo.InvariantCulture) + " s old; Do not disturb, an app whose popups are off, or a popup Machitan missed).");
            }
        }

        if (_noteUnpairedLogged.Count > 200)
        {
            _noteUnpairedLogged.Clear();
        }
    }

    /// <summary>
    /// The app's icon and name (and how many more came), the notification's own picture beside
    /// the words (as in the list), the title, up to two lines of the text, and its big picture
    /// (or its first picture below the text) under them; the view grows for the picture.
    /// </summary>
    private void FillNotePeek(NotificationEntry entry, int more)
    {
        NotificationPictureView? pictures = _notes.PicturesFor(entry.Id);
        _notePeekId = entry.Id;
        _notePeekMore = more;
        _notePeekPicturesVersion = _notes.PicturesVersion;
        _notePeekIcon.Child = NoteAppIcon(entry) is ImageSource icon
            ? new Image { Source = icon, Width = 16, Height = 16, Stretch = Stretch.Uniform }
            : Glyph(0xE7E7, 12, _dimBrush);
        Set(_notePeekApp, more > 0 ? Fmt.F(Strings.DynNoteAndMore, entry.AppName, more) : entry.AppName);
        string body = BodyWithout(entry.Body, pictures?.Attribution ?? string.Empty);
        string title = entry.Title.Length > 0 ? entry.Title : body;
        if (entry.Title.Length == 0)
        {
            body = string.Empty;
        }

        Set(_notePeekTitle, title);
        Set(_notePeekBody, body);
        Show(_notePeekBody, body.Length > 0);
        AutomationProperties.SetName(_noteContent, entry.AppName + ": " + title);

        if (pictures?.Logo is ImageSource logo)
        {
            var fill = new ImageBrush { ImageSource = logo, Stretch = Stretch.UniformToFill };
            _notePeekLogo.Child = pictures.LogoCircle
                ? new Ellipse { Width = NoteLogoSize, Height = NoteLogoSize, Fill = fill }
                : new Rectangle { Width = NoteLogoSize, Height = NoteLogoSize, RadiusX = 4, RadiusY = 4, Fill = fill };
            Show(_notePeekLogo, true);
        }
        else
        {
            _notePeekLogo.Child = null;
            Show(_notePeekLogo, false);
        }

        ImageSource? big = pictures?.Hero ?? (pictures is { Inline.Count: > 0 } ? pictures.Inline[0] : null);
        _notePeekPicture.Child = big is not null ? PictureBox(big, NotePeekPictureHeight) : null;
        Show(_notePeekPicture, big is not null);

        double height = big is not null ? NotePeekHeight + NotePeekPictureHeight + 6 : NotePeekHeight;
        bool resized = Math.Abs(height - _notePeekHeight) >= 0.5;
        _notePeekHeight = height;
        _noteContent.Height = height;
        if (resized && _pill == PillShape.Note)
        {
            // The picture arrived while the notification is open: the view grows to it.
            HoldRegion(_collapsedWidth);
            AnimatePill(NotePeekWidth, height, _privacyRadius);
        }
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
        return MediaAppNames.NameFor(_host.Catalog, appId);
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
