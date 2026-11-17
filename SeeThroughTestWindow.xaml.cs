using System;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace Machitan;

/// <summary>
/// Test window for Batch 2 ("Machitan.exe --seethrough"). It answers one question: can a
/// WinUI window on this machine be see-through outside its shapes, with smooth edges, and
/// let clicks in the gaps reach what is behind? The segmented bar and the floating Start
/// menu boxes both need that.
///
/// Switches:
///   A: a backdrop that draws nothing, so WinUI drops its solid window fill.
///   B: Windows alpha: DWM blur-behind with a region off the window. Nothing is blurred,
///      but DWM starts honouring transparent pixels. This was missing in the first test.
///   C: layered window style (the first test's approach; kept to compare).
///   D: click-through gaps: the window is cut to the three shapes, 2 px wider than each,
///      so the smooth edges stay inside and the gaps between shapes are not part of the window.
/// </summary>
public sealed partial class SeeThroughTestWindow : Window
{
    private const int WidthDip = 1180;
    private const int HeightDip = 180;
    private const int RegionSlackPx = 2;

    private readonly nint _hwnd;
    private WindowSubclass? _subclass;
    private bool _ready;

    public SeeThroughTestWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        RemoveWindowOutline();

        _subclass = new WindowSubclass(_hwnd, OnWindowMessage);
        Closed += (_, _) =>
        {
            _subclass?.Dispose();
            _subclass = null;
        };

        AcrylicCapsule.SystemBackdrop = new PersistentAcrylicBackdrop { TintStrength = 0.5f };

        PlaceInCentre();
        Root.Loaded += (_, _) => ApplyClickThrough();
        _ready = true;
        ApplyOptions();
    }

    private void OnOptionToggled(object sender, RoutedEventArgs e)
    {
        // The switches raise this once while the XAML is still loading.
        if (!_ready)
        {
            return;
        }

        ApplyOptions();
    }

    private void ApplyOptions()
    {
        try
        {
            SystemBackdrop = BackdropToggle.IsOn ? new TransparentBackdrop() : null;
            ApplyDwmAlpha(DwmAlphaToggle.IsOn);
            ApplyLayered(LayeredToggle.IsOn);
            ApplyClickThrough();

            Log.Info(
                "See-through test: A backdrop " + OnOff(BackdropToggle.IsOn) +
                ", B Windows alpha " + OnOff(DwmAlphaToggle.IsOn) +
                ", C layered " + OnOff(LayeredToggle.IsOn) +
                ", D click-through " + OnOff(ClickThroughToggle.IsOn) + ".");
        }
        catch (Exception ex)
        {
            Log.Error("See-through test: applying the options failed.", ex);
        }
    }

    private static string OnOff(bool value)
    {
        return value ? "on" : "off";
    }

    /// <summary>Windows 11 draws a thin outline and rounds the corners of top-level windows; turn both off.</summary>
    private void RemoveWindowOutline()
    {
        int none = Dwmapi.DWMWA_COLOR_NONE;
        int borderResult = Dwmapi.DwmSetWindowAttribute(_hwnd, Dwmapi.DWMWA_BORDER_COLOR, ref none, sizeof(int));

        int square = Dwmapi.DWMWCP_DONOTROUND;
        int cornerResult = Dwmapi.DwmSetWindowAttribute(_hwnd, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));

        if (borderResult != 0 || cornerResult != 0)
        {
            Log.Warn(
                "See-through test: Windows refused to drop the window outline (0x" + borderResult.ToString("X8") +
                ", 0x" + cornerResult.ToString("X8") + ").");
        }
    }

    private void ApplyDwmAlpha(bool enable)
    {
        nint offWindow = enable ? Gdi32.CreateRectRgn(-2, -2, -1, -1) : 0;
        try
        {
            var blur = new DWM_BLURBEHIND
            {
                dwFlags = enable ? Dwmapi.DWM_BB_ENABLE | Dwmapi.DWM_BB_BLURREGION : Dwmapi.DWM_BB_ENABLE,
                fEnable = enable ? 1 : 0,
                hRgnBlur = offWindow,
            };

            int hr = Dwmapi.DwmEnableBlurBehindWindow(_hwnd, ref blur);
            if (hr != 0)
            {
                Log.Warn("See-through test: Windows refused the alpha request (0x" + hr.ToString("X8") + ").");
            }
        }
        finally
        {
            // DWM keeps its own copy; the region stays ours to free.
            if (offWindow != 0)
            {
                Gdi32.DeleteObject(offWindow);
            }
        }
    }

    private void ApplyLayered(bool enable)
    {
        long exStyle = User32.GetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE);
        exStyle = enable ? exStyle | User32.WS_EX_LAYERED : exStyle & ~User32.WS_EX_LAYERED;
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE, (nint)exStyle);

        if (enable)
        {
            // A layered window shows nothing until its attributes are set once.
            User32.SetLayeredWindowAttributes(_hwnd, 0, 255, User32.LWA_ALPHA);
        }
    }

    /// <summary>Cuts the window to the three shapes (each 2 px wider), or removes the cut.</summary>
    private void ApplyClickThrough()
    {
        try
        {
            if (!ClickThroughToggle.IsOn)
            {
                User32.SetWindowRgn(_hwnd, 0, true);
                return;
            }

            if (Root.XamlRoot is null)
            {
                // Not laid out yet; Root.Loaded calls this again.
                return;
            }

            double scale = Root.XamlRoot.RasterizationScale;
            nint total = Gdi32.CreateRectRgn(0, 0, 0, 0);
            if (total == 0)
            {
                return;
            }

            AddShape(total, SolidCapsule, 32, scale);
            AddShape(total, AcrylicHost, 32, scale);
            AddShape(total, SwitchBox, 16, scale);

            if (User32.SetWindowRgn(_hwnd, total, true) == 0)
            {
                // The system took no ownership, so the region is still ours to free.
                Gdi32.DeleteObject(total);
                Log.Warn("See-through test: the click-through region was refused.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("See-through test: the click-through region failed.", ex);
        }
    }

    private void AddShape(nint total, FrameworkElement element, double cornerRadiusDip, double scale)
    {
        Rect bounds = element.TransformToVisual(Root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        int left = (int)Math.Floor(bounds.Left * scale) - RegionSlackPx;
        int top = (int)Math.Floor(bounds.Top * scale) - RegionSlackPx;
        int right = (int)Math.Ceiling(bounds.Right * scale) + RegionSlackPx + 1;
        int bottom = (int)Math.Ceiling(bounds.Bottom * scale) + RegionSlackPx + 1;
        int ellipse = (int)Math.Round(((cornerRadiusDip * scale) + RegionSlackPx) * 2);

        nint shape = Gdi32.CreateRoundRectRgn(left, top, right, bottom, ellipse, ellipse);
        if (shape == 0)
        {
            return;
        }

        Gdi32.CombineRgn(total, total, shape, Gdi32.RGN_OR);
        Gdi32.DeleteObject(shape);
    }

    private bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;

        if (message == User32.WM_ERASEBKGND)
        {
            // Erase to black. Under "Windows alpha", black with zero alpha is see-through;
            // without it, this is the black the first test showed.
            if (User32.GetClientRect(_hwnd, out RECT client))
            {
                User32.FillRect(wParam, ref client, Gdi32.GetStockObject(Gdi32.BLACK_BRUSH));
                result = 1;
                return true;
            }
        }

        return false;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>Centre of the primary screen. AppWindow sizes are physical pixels.</summary>
    private void PlaceInCentre()
    {
        uint dpi = User32.GetDpiForWindow(_hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        double scale = dpi / 96.0;
        int width = (int)Math.Round(WidthDip * scale);
        int height = (int)Math.Round(HeightDip * scale);

        RectInt32 area = DisplayArea.Primary.WorkArea;
        int x = area.X + ((area.Width - width) / 2);
        int y = area.Y + ((area.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }
}
