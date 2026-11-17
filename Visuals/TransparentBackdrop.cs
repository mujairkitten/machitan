using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Visuals;

/// <summary>
/// A backdrop that draws nothing: a fully see-through brush.
/// Setting any backdrop makes WinUI drop its solid window fill; this one then leaves the
/// space empty, so only the XAML content is visible. The window itself must also be a
/// layered window whose background erase is handled (see SeeThroughTestWindow).
/// Its target and brush stay referenced until exit (see <see cref="BackdropKeeper"/>).
/// </summary>
public sealed partial class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? _compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        // The target takes a brush from the system compositor, which needs a system
        // dispatcher queue on this thread.
        DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
        _compositor ??= new Windows.UI.Composition.Compositor();

        BackdropKeeper.Hold(connectedTarget);
        Windows.UI.Composition.CompositionColorBrush brush = _compositor.CreateColorBrush(ColorHelper.FromArgb(0, 0, 0, 0));
        BackdropKeeper.Hold(brush);
        connectedTarget.SystemBackdrop = brush;
    }

    /// <summary>Nothing to configure; not calling the base avoids its "invalid parameter: target" error.</summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }
}
