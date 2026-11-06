using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace MyNotch.Services;

/// <summary>
/// The screen-attached notch body, drawn by the compositor (Visual Layer, spring physics).
/// One rounded-rectangle geometry is the single source of truth for the shape: it clips the
/// black content, casts the drop shadow and carries the rim light. Morphing the island is
/// just springing that geometry's size and corner radius, so the closed notch opens into a panel
/// while the content inside stays crisp instead of being scaled.
/// </summary>
/// <remarks>
/// The three host elements stretch to the window and are never wider than it: an element that overflows
/// its layout slot gets XAML's own layout clip, which silently replaces ours. The geometry's offset is an
/// expression, (stageWidth - size) / 2, so the shape stays centred on every frame of the spring, and
/// <see cref="SetStageWidth"/> keeps it right when the window itself is resized.
/// </remarks>
internal sealed class IslandVisuals
{
    private readonly Compositor _compositor;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionPropertySet _stageProps;

    /// <param name="body">Element holding the island's content; its visual gets the geometric clip.</param>
    /// <param name="shadowHost">Empty element behind the body, same bounds; hosts the shadow layer.</param>
    /// <param name="rimHost">Empty element above the body, same bounds; hosts the rim light.</param>
    /// <param name="visualSize">Size of the shadow/rim visuals: anything at least as large as the biggest window.</param>
    /// <param name="stageWidth">Current width of the host elements (the window's width, in DIPs).</param>
    internal IslandVisuals(UIElement body, UIElement shadowHost, UIElement rimHost, UIElement leftShoulder, UIElement rightShoulder, Vector2 visualSize, float stageWidth)
    {
        var bodyVisual = ElementCompositionPreview.GetElementVisual(body);
        _compositor = bodyVisual.Compositor;

        _stageProps = _compositor.CreatePropertySet();
        _stageProps.InsertScalar("StageW", stageWidth);

        _geometry = _compositor.CreateRoundedRectangleGeometry();
        // Extend the rounded rectangle above the screen by its radius: only the bottom
        // corners remain visible, and the top edge is continuously attached to the bezel.
        var centre = _compositor.CreateExpressionAnimation("Vector2((p.StageW - g.Size.X) * 0.5, -g.CornerRadius.X)");
        centre.SetReferenceParameter("p", _stageProps);
        centre.SetReferenceParameter("g", _geometry);
        _geometry.StartAnimation("Offset", centre);

        AttachShoulder(leftShoulder, false);
        AttachShoulder(rightShoulder, true);

        bodyVisual.Clip = _compositor.CreateGeometricClip(_geometry);

        // Body: the island's black rounded-rectangle *shape*, following the geometry directly.
        var bodyShape = _compositor.CreateSpriteShape(_geometry);
        bodyShape.FillBrush = _compositor.CreateColorBrush(Colors.Black);
        var bodyShapeVisual = _compositor.CreateShapeVisual();
        bodyShapeVisual.Size = visualSize;
        bodyShapeVisual.Shapes.Add(bodyShape);

        // Shadow: a sprite whose drop shadow takes its silhouette from a live surface of the same shape. The
        // shadow therefore morphs with the island on the compositor. (A DropShadow set on a LayerVisual renders
        // as a solid rectangle here, so this is the route that works.)
        var maskShape = _compositor.CreateSpriteShape(_geometry);
        maskShape.FillBrush = _compositor.CreateColorBrush(Colors.White);
        var maskSource = _compositor.CreateShapeVisual();
        maskSource.Size = visualSize;
        maskSource.Shapes.Add(maskShape);
        var maskSurface = _compositor.CreateVisualSurface();
        maskSurface.SourceVisual = maskSource;
        maskSurface.SourceSize = visualSize;

        var shadow = _compositor.CreateDropShadow();
        shadow.BlurRadius = 16f;
        shadow.Opacity = 0.35f;
        shadow.Offset = new Vector3(0f, 4f, 0f);
        shadow.Color = Colors.Black;
        shadow.Mask = _compositor.CreateSurfaceBrush(maskSurface);

        var shadowSprite = _compositor.CreateSpriteVisual();
        shadowSprite.Size = visualSize;
        shadowSprite.Shadow = shadow;

        var stack = _compositor.CreateContainerVisual();
        stack.Size = visualSize;
        stack.Children.InsertAtTop(shadowSprite);
        stack.Children.InsertAtTop(bodyShapeVisual);
        ElementCompositionPreview.SetElementChildVisual(shadowHost, stack);
        // Rim light: a 1px stroke along the same outline, bright at the top edge and fading out below.
        var rim = _compositor.CreateLinearGradientBrush();
        rim.MappingMode = CompositionMappingMode.Relative;
        rim.StartPoint = new Vector2(0.5f, 0f);
        rim.EndPoint = new Vector2(0.5f, 1f);
        rim.ColorStops.Add(_compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(0x4A, 255, 255, 255)));
        rim.ColorStops.Add(_compositor.CreateColorGradientStop(0.35f, Windows.UI.Color.FromArgb(0x16, 255, 255, 255)));
        rim.ColorStops.Add(_compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0x08, 255, 255, 255)));

        var rimShape = _compositor.CreateSpriteShape(_geometry);
        rimShape.StrokeBrush = rim;
        rimShape.StrokeThickness = 0f;
        var rimVisual = _compositor.CreateShapeVisual();
        rimVisual.Size = visualSize;
        rimVisual.Shapes.Add(rimShape);
        ElementCompositionPreview.SetElementChildVisual(rimHost, rimVisual);
    }

    /// <summary>Call when the host elements' width changes (window resized): re-centres the shape immediately.</summary>
    internal void SetStageWidth(float width) => _stageProps.InsertScalar("StageW", width);

    private void AttachShoulder(UIElement element, bool right)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var position = _compositor.CreateExpressionAnimation(right
            ? "Vector3((p.StageW + g.Size.X) * 0.5 - 0.5, 0, 0)"
            : "Vector3((p.StageW - g.Size.X) * 0.5 - 10 * g.CornerRadius.X / 14 + 0.5, 0, 0)");
        position.SetReferenceParameter("p", _stageProps);
        position.SetReferenceParameter("g", _geometry);
        visual.StartAnimation("Translation", position);
        var scale = _compositor.CreateExpressionAnimation("Vector3(g.CornerRadius.X / 14, g.CornerRadius.X / 14, 1)");
        scale.SetReferenceParameter("g", _geometry);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>Sets the shape immediately (no animation).</summary>
    internal void Snap(Vector2 size, float radius)
    {
        _geometry.StopAnimation("Size");
        _geometry.StopAnimation("CornerRadius");
        _geometry.Size = new Vector2(size.X, size.Y + radius);
        _geometry.CornerRadius = new Vector2(radius);
    }

    /// <summary>Springs to a new size and corner radius (the offset follows by expression, staying centred).</summary>
    internal void MorphTo(Vector2 size, float radius, float damping = 0.78f, int periodMs = 55)
    {
        Spring("Size", new Vector2(size.X, size.Y + radius), damping, periodMs);
        Spring("CornerRadius", new Vector2(radius), damping, periodMs);
    }

    private void Spring(string property, Vector2 target, float damping, int periodMs)
    {
        var spring = _compositor.CreateSpringVector2Animation();
        spring.FinalValue = target;          // starts from the property's current value
        spring.DampingRatio = damping;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);
        _geometry.StartAnimation(property, spring);
    }
}

/// <summary>Content transitions that accompany a morph. All run on the compositor thread.</summary>
internal static class NotchMotion
{
    /// <summary>Quick fade (and optional rise) for content that just changed: glance swap, tab page.</summary>
    internal static void FadeIn(UIElement element, float riseDip = 4f, int durationMs = 180)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = EaseOut(compositor);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", fade);

        if (riseDip > 0f)
        {
            // The Translation property only exists once enabled; animating it first throws E_INVALIDARG.
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var rise = compositor.CreateVector3KeyFrameAnimation();
            rise.InsertKeyFrame(0f, new Vector3(0f, riseDip, 0f));
            rise.InsertKeyFrame(1f, Vector3.Zero, ease);
            rise.Duration = TimeSpan.FromMilliseconds(durationMs + 60);
            visual.StartAnimation("Translation", rise);
        }
    }

    /// <summary>Fades out, then runs <paramref name="completed"/> on the UI thread (e.g. to collapse the element).</summary>
    internal static void FadeOut(UIElement element, int durationMs, Action? completed = null)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        if (completed is not null) batch.Completed += (_, _) => completed();
    }

    /// <summary>
    /// Content arriving with the morph: scales up from <paramref name="fromScale"/> about its top
    /// centre while fading in. The delay lets the shape open up first; opacity holds at 0 until then.
    /// </summary>
    internal static void PopIn(UIElement element, float pivotX, float fromScale = 0.94f, int delayMs = 60, int durationMs = 260)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = EaseOut(compositor);
        visual.CenterPoint = new Vector3(pivotX, 0f, 0f);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", fade);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(fromScale, fromScale, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, ease);
        scale.Duration = TimeSpan.FromMilliseconds(durationMs);
        scale.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Scale", scale);
    }

    private static CubicBezierEasingFunction EaseOut(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
}

/// <summary>
/// The "beat" bars on the closed island while music plays. Compositor-side looping scale
/// animations: no UI-thread work per frame, and stopped entirely when not visible.
/// </summary>
internal sealed class BeatAnimator
{
    private readonly UIElement[] _bars;
    private readonly int[] _periodsMs = { 720, 540, 640 };
    private bool _running;

    internal BeatAnimator(params UIElement[] bars) => _bars = bars;

    internal void Start()
    {
        if (_running) return;
        _running = true;
        for (int i = 0; i < _bars.Length; i++)
        {
            var visual = ElementCompositionPreview.GetElementVisual(_bars[i]);
            var compositor = visual.Compositor;
            // Anchor at the bottom edge so the bars grow upward.
            visual.CenterPoint = new Vector3(1.5f, 12f, 0f);
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0f, 0.3f);
            anim.InsertKeyFrame(0.5f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f)));
            anim.InsertKeyFrame(1f, 0.3f);
            anim.Duration = TimeSpan.FromMilliseconds(_periodsMs[i % _periodsMs.Length]);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Scale.Y", anim);
        }
    }

    internal void Stop()
    {
        if (!_running) return;
        _running = false;
        foreach (var bar in _bars)
        {
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.StopAnimation("Scale.Y");
            visual.Scale = new Vector3(1f, 0.45f, 1f);
        }
    }
}

/// <summary>A slow "breathing" opacity loop on one element (the awaiting-approval dot). Compositor-side.</summary>
internal sealed class PulseAnimator
{
    private readonly UIElement _element;
    private bool _running;

    internal PulseAnimator(UIElement element) => _element = element;

    internal void Start()
    {
        if (_running) return;
        _running = true;
        var visual = ElementCompositionPreview.GetElementVisual(_element);
        var compositor = visual.Compositor;
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 1f);
        anim.InsertKeyFrame(0.5f, 0.3f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        anim.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        anim.Duration = TimeSpan.FromMilliseconds(1300);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", anim);
    }

    internal void Stop()
    {
        if (!_running) return;
        _running = false;
        var visual = ElementCompositionPreview.GetElementVisual(_element);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1f;
    }
}

/// <summary>
/// Oyenza-style idle eyes: a brief blink every 5.5 seconds. The eyes are the default (idle) glance, so this is
/// what the notch shows most of the time. A looping 5.5 s keyframe animation would keep the compositor rendering
/// every frame for a blink that lasts 140 ms; instead a timer fires a one-shot blink, and between blinks nothing
/// on the notch animates.
/// </summary>
internal sealed class IdleEyeAnimator
{
    private const float BlinkMs = 140f;
    private readonly UIElement _eyes;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private ScalarKeyFrameAnimation? _blink;
    private bool _running;

    internal IdleEyeAnimator(UIElement eyes)
    {
        _eyes = eyes;
        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(5500);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Blink();
    }

    internal void Start()
    {
        if (_running) return;
        _running = true;
        _timer.Start();
    }

    internal void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer.Stop();
        var visual = ElementCompositionPreview.GetElementVisual(_eyes);
        visual.StopAnimation("Scale.Y");
        visual.Scale = Vector3.One;
    }

    private void Blink()
    {
        if (!_running) return;
        var visual = ElementCompositionPreview.GetElementVisual(_eyes);
        if (_blink is null)
        {
            visual.CenterPoint = new Vector3(14.25f, 5.5f, 0f);
            var compositor = visual.Compositor;
            var linear = compositor.CreateLinearEasingFunction();
            _blink = compositor.CreateScalarKeyFrameAnimation();
            _blink.InsertKeyFrame(0f, 1f);
            _blink.InsertKeyFrame(60f / BlinkMs, 2.5f / 11f, linear);
            _blink.InsertKeyFrame(90f / BlinkMs, 2.5f / 11f, linear);
            _blink.InsertKeyFrame(1f, 1f, linear);
            _blink.Duration = TimeSpan.FromMilliseconds(BlinkMs);
        }
        visual.StartAnimation("Scale.Y", _blink);
    }
}


