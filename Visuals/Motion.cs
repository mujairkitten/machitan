using System;
using System.Numerics;
using Machitan.Config;
using Machitan.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Machitan.Visuals;

/// <summary>
/// Machitan's animations (Start, the tray box, the bar), drawn by Windows' compositor on the
/// element's own visual, so layout, hit testing and the keyboard are never held up. Things
/// that arrive slow down at the end and things that leave speed up, with Windows' own curves.
///
/// Settings > General > Turn off animations stops them all (off by default), and so does
/// Windows' own Animation effects setting (Settings > Accessibility > Visual effects): then
/// everything simply appears where it ends. Callers ask <see cref="IsOn"/> each time, so a
/// change applies at the next animation.
/// </summary>
internal static class Motion
{
    // Windows' entrance (decelerate) and exit (accelerate) curves, and a small overshoot for
    // something let go (an icon springing back after a press).
    private static readonly Vector2 DecelerateIn = new(0.1f, 0.9f);
    private static readonly Vector2 DecelerateOut = new(0.2f, 1f);
    private static readonly Vector2 AccelerateIn = new(0.7f, 0f);
    private static readonly Vector2 AccelerateOut = new(1f, 0.5f);
    private static readonly Vector2 SpringIn = new(0.34f, 1.4f);
    private static readonly Vector2 SpringOut = new(0.64f, 1f);

    private const float PressedScale = 0.86f;
    private const int PressMs = 90;
    private const int ReleaseMs = 260;

    private static Windows.UI.ViewManagement.UISettings? s_windowsSettings;
    private static bool s_windowsUnreadable;

    /// <summary>True when Machitan animates: not turned off in its settings, nor in Windows'.</summary>
    internal static bool IsOn(ShellSettings settings)
    {
        return !settings.TurnOffAnimations && WindowsAllows();
    }

    /// <summary>Windows' Animation effects setting; true when it cannot be read.</summary>
    internal static bool WindowsAllows()
    {
        if (s_windowsUnreadable)
        {
            return true;
        }

        try
        {
            s_windowsSettings ??= new Windows.UI.ViewManagement.UISettings();
            return s_windowsSettings.AnimationsEnabled;
        }
        catch (Exception ex)
        {
            s_windowsUnreadable = true;
            Log.Warn("Motion: Windows' Animation effects setting could not be read; Machitan's own setting decides alone.", ex);
            return true;
        }
    }

    /// <summary>
    /// The element arrives: from <paramref name="fromY"/> DIP away (down for a positive value)
    /// and see-through, to its place, fully shown, in <paramref name="ms"/>. <paramref name="done"/>
    /// runs on the element's UI thread at the end (also when a newer animation replaced this one).
    /// </summary>
    internal static void Arrive(UIElement element, float fromY, int ms, Action? done = null)
    {
        Run(element, fromY, 0f, 0f, 1f, ms, entering: true, done);
    }

    /// <summary>
    /// The element comes back from wherever it is now (reopened while still leaving): to its
    /// place, fully shown, in <paramref name="ms"/>, without jumping first.
    /// </summary>
    internal static void Return(UIElement element, int ms, Action? done = null)
    {
        Run(element, null, 0f, null, 1f, ms, entering: true, done);
    }

    /// <summary>The element leaves: moves <paramref name="toY"/> DIP and fades out in <paramref name="ms"/>.</summary>
    internal static void Leave(UIElement element, float toY, int ms, Action? done = null)
    {
        Run(element, null, toY, null, 0f, ms, entering: false, done);
    }

    /// <summary>Stops any animation and puts the element at rest: in its place, fully shown, full size.</summary>
    internal static void Reset(UIElement element)
    {
        try
        {
            Visual visual = Prepare(element);
            visual.StopAnimation("Translation.Y");
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = 1f;
            visual.Scale = Vector3.One;
        }
        catch (Exception ex)
        {
            Log.Warn("Motion: an element could not be put at rest.", ex);
        }
    }

    /// <summary>A press on a button's content: it shrinks a little, and springs back when let go.</summary>
    internal static void Press(UIElement element, bool down)
    {
        try
        {
            Visual visual = Prepare(element);
            Compositor compositor = visual.Compositor;
            KeepCentred(visual);

            Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(
                1f,
                down ? new Vector3(PressedScale, PressedScale, 1f) : Vector3.One,
                down
                    ? compositor.CreateCubicBezierEasingFunction(DecelerateIn, DecelerateOut)
                    : compositor.CreateCubicBezierEasingFunction(SpringIn, SpringOut));
            scale.Duration = TimeSpan.FromMilliseconds(down ? PressMs : ReleaseMs);
            visual.StartAnimation("Scale", scale);
        }
        catch (Exception ex)
        {
            Log.Warn("Motion: a press could not be animated.", ex);
        }
    }

    /// <summary>A new item on the bar: it grows from <paramref name="fromScale"/> and fades in.</summary>
    internal static void PopIn(UIElement element, float fromScale, int ms)
    {
        try
        {
            Visual visual = Prepare(element);
            Compositor compositor = visual.Compositor;
            KeepCentred(visual);
            visual.Scale = new Vector3(fromScale, fromScale, 1f);
            visual.Opacity = 0f;

            Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(1f, Vector3.One, compositor.CreateCubicBezierEasingFunction(SpringIn, SpringOut));
            scale.Duration = TimeSpan.FromMilliseconds(ms);

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f, compositor.CreateLinearEasingFunction());
            fade.Duration = TimeSpan.FromMilliseconds(Math.Max(1, ms * 2 / 3));

            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception ex)
        {
            Log.Warn("Motion: a new item could not be animated.", ex);
            Reset(element);
        }
    }

    /// <summary>
    /// The element just got wider or narrower (a running indicator): it stretches from
    /// <paramref name="fromRatio"/> of its new width to all of it, from its centre.
    /// </summary>
    internal static void StretchX(UIElement element, float fromRatio, int ms)
    {
        try
        {
            Visual visual = Prepare(element);
            Compositor compositor = visual.Compositor;
            KeepCentred(visual);
            visual.Scale = new Vector3(fromRatio, 1f, 1f);

            Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(1f, Vector3.One, compositor.CreateCubicBezierEasingFunction(DecelerateIn, DecelerateOut));
            scale.Duration = TimeSpan.FromMilliseconds(ms);
            visual.StartAnimation("Scale", scale);
        }
        catch (Exception ex)
        {
            Log.Warn("Motion: a width change could not be animated.", ex);
            Reset(element);
        }
    }

    private static Visual Prepare(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }

    /// <summary>Scaling happens around the element's centre, wherever layout puts it.</summary>
    private static void KeepCentred(Visual visual)
    {
        ExpressionAnimation centre = visual.Compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)");
        visual.StartAnimation("CenterPoint", centre);
    }

    private static void Run(UIElement element, float? fromY, float toY, float? fromOpacity, float toOpacity, int ms, bool entering, Action? done)
    {
        DispatcherQueue? queue = element.DispatcherQueue;
        try
        {
            Visual visual = Prepare(element);
            Compositor compositor = visual.Compositor;
            visual.StopAnimation("Translation.Y");
            visual.StopAnimation("Opacity");
            if (fromY is float startY)
            {
                visual.Properties.InsertVector3("Translation", new Vector3(0f, startY, 0f));
            }

            if (fromOpacity is float startOpacity)
            {
                visual.Opacity = startOpacity;
            }

            CompositionEasingFunction ease = entering
                ? compositor.CreateCubicBezierEasingFunction(DecelerateIn, DecelerateOut)
                : compositor.CreateCubicBezierEasingFunction(AccelerateIn, AccelerateOut);

            ScalarKeyFrameAnimation move = compositor.CreateScalarKeyFrameAnimation();
            move.InsertKeyFrame(1f, toY, ease);
            move.Duration = TimeSpan.FromMilliseconds(ms);

            // Arriving, the fade is done before the move; leaving, it takes the whole time.
            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, toOpacity, compositor.CreateLinearEasingFunction());
            fade.Duration = TimeSpan.FromMilliseconds(entering ? Math.Max(1, ms * 2 / 3) : ms);

            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation("Translation.Y", move);
            visual.StartAnimation("Opacity", fade);
            batch.End();

            if (done is not null)
            {
                // On the UI thread; dropped if the window's thread is already gone.
                batch.Completed += (_, _) => queue?.TryEnqueue(() => Finish(done));
            }
        }
        catch (Exception ex)
        {
            // No animation: straight to the end.
            Log.Warn("Motion: an animation could not start; shown without it.", ex);
            if (entering)
            {
                Reset(element);
            }

            if (done is not null)
            {
                Finish(done);
            }
        }
    }

    private static void Finish(Action done)
    {
        try
        {
            done();
        }
        catch (Exception ex)
        {
            Log.Error("Motion: the end of an animation could not be handled.", ex);
        }
    }
}
