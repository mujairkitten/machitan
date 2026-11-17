using System;
using System.Diagnostics;
using System.IO;
using Machitan.Interop;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System.Power;
using Windows.UI.ViewManagement;

namespace Machitan;

/// <summary>
/// Batch 0 proof window. It answers one question: does desktop acrylic render on this
/// machine with Windows App SDK 2.6.5, and through which route. Each test in the list uses
/// a different route so a failure can be pinned down.
/// Every line shown is read live from the system; nothing is hard-coded.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int WidthDip = 780;
    private const int HeightDip = 760;

    private const int ModeStockAcrylic = 0;
    private const int ModeWindowAcrylic = 1;
    private const int ModeMica = 2;
    private const int ModeBoxAcrylic = 3;

    private readonly nint _hwnd;
    private PersistentAcrylicBackdrop? _acrylic;
    private string _hostBackdropResult = "not sent (Windows App SDK is left to do it)";
    private bool _isFocused = true;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _ready = true;
        ResizeToDip(WidthDip, HeightDip);
        Activated += OnActivated;
        RefreshStatus();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _isFocused = args.WindowActivationState != WindowActivationState.Deactivated;
        RefreshStatus();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // The list raises this once while the XAML is still loading.
        if (!_ready)
        {
            return;
        }

        ApplyMode();
        RefreshStatus();
    }

    private void ApplyMode()
    {
        int mode = ModeBox.SelectedIndex;

        // Clear the previous test.
        _acrylic = null;
        BoxBackdrop.SystemBackdrop = null;
        BoxHost.Visibility = Visibility.Collapsed;
        Root.Background = null;

        switch (mode)
        {
            case ModeWindowAcrylic:
                _acrylic = new PersistentAcrylicBackdrop { TintStrength = ReadSliderStrength() };
                SystemBackdrop = _acrylic;
                break;

            case ModeMica:
                SystemBackdrop = new MicaBackdrop();
                break;

            case ModeBoxAcrylic:
                // Plain window, acrylic only inside the rounded box.
                SystemBackdrop = null;
                Root.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 32, 32, 32));
                _acrylic = new PersistentAcrylicBackdrop { TintStrength = ReadSliderStrength() };
                BoxHost.Visibility = Visibility.Visible;
                BoxBackdrop.SystemBackdrop = _acrylic;
                break;

            default:
                SystemBackdrop = new DesktopAcrylicBackdrop();
                break;
        }

        TintSlider.IsEnabled = mode is ModeWindowAcrylic or ModeBoxAcrylic;
    }

    private void OnTintChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // The slider can raise this while the XAML is still loading.
        if (!_ready)
        {
            return;
        }

        if (_acrylic is not null)
        {
            _acrylic.TintStrength = ReadSliderStrength();
        }

        RefreshStatus();
    }

    private void OnForceHostToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        int value = ForceHostToggle.IsOn ? 1 : 0;
        int hr = Dwmapi.DwmSetWindowAttribute(_hwnd, Dwmapi.DWMWA_USE_HOSTBACKDROPBRUSH, ref value, sizeof(int));

        _hostBackdropResult =
            (ForceHostToggle.IsOn ? "asked for ON" : "asked for OFF") +
            ", Windows answered 0x" + hr.ToString("X8") +
            (hr == 0 ? " (accepted)" : " (refused)");

        RefreshStatus();
    }

    private float ReadSliderStrength()
    {
        return (float)(TintSlider.Value / 100.0);
    }

    /// <summary>AppWindow.Resize takes physical pixels, so scale DIPs by the window's DPI.</summary>
    private void ResizeToDip(int widthDip, int heightDip)
    {
        uint dpi = User32.GetDpiForWindow(_hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        double scale = dpi / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(widthDip * scale),
            (int)Math.Round(heightDip * scale)));
    }

    private void RefreshStatus()
    {
        int tint = (int)Math.Round(TintSlider.Value);

        BackdropLine.Text = ModeBox.SelectedIndex switch
        {
            ModeWindowAcrylic => "Test 2: whole window, Machitan acrylic, tint strength " + tint + "%",
            ModeMica => "Test 3: whole window, Mica",
            ModeBoxAcrylic => "Test 4: rounded box, Machitan acrylic, tint strength " + tint + "% (window itself is plain)",
            _ => "Test 1: whole window, stock DesktopAcrylicBackdrop (dense dark tint)",
        };

        HostLine.Text = "Host backdrop request: " + _hostBackdropResult;

        FocusLine.Text = _isFocused
            ? "This window: focused"
            : "This window: not focused";

        SupportLine.Text = "Acrylic supported by this system: " + ReadAcrylicSupport();
        EffectsLine.Text = "Windows transparency effects: " + ReadTransparencyEffects();
        EnergyLine.Text = "Energy saver: " + ReadEnergySaver();
        VersionLine.Text =
            "Windows App SDK runtime " + ReadWindowsAppRuntimeVersion() +
            "  |  .NET " + Environment.Version +
            "  |  Windows build " + Environment.OSVersion.Version.Build;
    }

    private static string ReadAcrylicSupport()
    {
        try
        {
            return DesktopAcrylicController.IsSupported() ? "yes" : "no";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ReadTransparencyEffects()
    {
        try
        {
            return new UISettings().AdvancedEffectsEnabled
                ? "on"
                : "off (acrylic shows as a solid colour until this is turned on in Settings > Personalisation > Colours)";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ReadEnergySaver()
    {
        try
        {
            return PowerManager.EnergySaverStatus switch
            {
                EnergySaverStatus.On => "on (Windows turns acrylic solid while energy saver is on)",
                EnergySaverStatus.Off => "off",
                _ => "not available on this device",
            };
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>Self-contained builds ship the runtime DLL next to the exe; read its real version.</summary>
    private static string ReadWindowsAppRuntimeVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.dll");
            if (!File.Exists(path))
            {
                return "unknown";
            }

            string? version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
