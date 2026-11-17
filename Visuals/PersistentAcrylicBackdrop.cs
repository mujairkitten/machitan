using System;
using Machitan.Core;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Visuals;

/// <summary>
/// Desktop acrylic that stays on when its window is not the focused window, with a tint the
/// app controls. The stock <see cref="DesktopAcrylicBackdrop"/> follows window activation and
/// falls back to a solid colour when the window loses focus, and its dark tint is so dense it
/// reads as almost solid. A shell bar is almost never the focused window, so this backdrop
/// drives its own controller and reports the window as always input-active.
/// One instance serves one window or one element.
/// Everything it touches stays referenced until exit (see <see cref="BackdropKeeper"/>).
/// </summary>
public sealed partial class PersistentAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private float _tintStrength = 0.3f;
    private bool _useDarkTint = true;

    /// <summary>
    /// How much the tint covers the blur: 0 = blur only, 1 = solid tint colour.
    /// Can be changed while the backdrop is live.
    /// </summary>
    public float TintStrength
    {
        get => _tintStrength;
        set
        {
            _tintStrength = Math.Clamp(value, 0f, 1f);
            ApplyTint();
        }
    }

    /// <summary>Dark tint (the default) or light tint. Can be changed while the backdrop is live.</summary>
    public bool UseDarkTint
    {
        get => _useDarkTint;
        set
        {
            _useDarkTint = value;
            ApplyTint();
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        if (_controller is not null)
        {
            // Already serving a window; a second target would share one controller.
            return;
        }

        BackdropKeeper.Hold(connectedTarget);

        _configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = _useDarkTint ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        BackdropKeeper.Hold(_configuration);

        _controller = new DesktopAcrylicController();
        BackdropKeeper.Hold(_controller);
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(connectedTarget);
        ApplyTint();
    }

    /// <summary>
    /// This backdrop keeps its own configuration (always input-active), so Windows' default
    /// one is not used. Not calling the base also avoids an "invalid parameter: target" error
    /// the base raises on every window activation.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        if (_controller is null)
        {
            return;
        }

        // On the UI thread, here and now; the objects themselves stay held (see BackdropKeeper).
        try
        {
            _controller.RemoveSystemBackdropTarget(disconnectedTarget);
            _controller.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("An acrylic backdrop did not detach cleanly.", ex);
        }

        _controller = null;
        _configuration = null;
    }

    private void ApplyTint()
    {
        if (_controller is null)
        {
            return;
        }

        if (_configuration is not null)
        {
            _configuration.Theme = _useDarkTint ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        }

        Windows.UI.Color tint = _useDarkTint
            ? ColorHelper.FromArgb(255, 32, 32, 32)
            : ColorHelper.FromArgb(255, 243, 243, 243);

        _controller.TintColor = tint;
        _controller.FallbackColor = tint;
        _controller.TintOpacity = _tintStrength;
        _controller.LuminosityOpacity = _tintStrength;
    }
}
