using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Ghostty.Services;

namespace Ghostty.Shell;

/// <summary>
/// Full-window composition visual that renders a multi-point radial
/// gradient tint. In underlay mode, sits between the SystemBackdrop
/// and RootGrid (visible through transparent terminal areas). In
/// overlay mode, sits on top of all content as a semi-transparent
/// tint layer (always visible regardless of terminal opacity).
/// </summary>
internal sealed class GradientTintVisual : IDisposable
{
    private readonly Compositor _compositor;
    private readonly SpriteVisual _rootVisual;
    private readonly ContainerVisual _hostVisual;
    private readonly List<CompositionAnimation> _animations = [];
    private readonly bool _isOverlay;
    // Overlay host element added to the XAML tree on top of content.
    private readonly Microsoft.UI.Xaml.Controls.Canvas? _overlayCanvas;

    /// <param name="host">The root Grid element to attach to.</param>
    /// <param name="points">Gradient color points.</param>
    /// <param name="overlay">True for overlay (on top), false for underlay (behind).</param>
    /// <param name="overlayOpacity">Opacity for overlay mode (0.0-1.0).</param>
    internal GradientTintVisual(
        Microsoft.UI.Xaml.Controls.Grid host,
        IReadOnlyList<GradientPoint> points,
        bool overlay = false,
        float overlayOpacity = 0.3f)
    {
        _isOverlay = overlay;

        var hostVisual = ElementCompositionPreview.GetElementVisual(host);
        _compositor = hostVisual.Compositor;
        _hostVisual = hostVisual as ContainerVisual
            ?? _compositor.CreateContainerVisual();

        _rootVisual = _compositor.CreateSpriteVisual();
        _rootVisual.RelativeSizeAdjustment = Vector2.One;

        RebuildBrush(points);

        if (overlay)
        {
            // Create a transparent Canvas on top of all XAML content,
            // then attach our visual to it so the gradient overlays
            // everything including the terminal.
            _overlayCanvas = new Microsoft.UI.Xaml.Controls.Canvas
            {
                IsHitTestVisible = false,
                Opacity = overlayOpacity,
            };
            // Span the entire grid -- use a large value to cover any row/column count.
            Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_overlayCanvas, int.MaxValue);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(_overlayCanvas, int.MaxValue);
            host.Children.Add(_overlayCanvas);
            ElementCompositionPreview.SetElementChildVisual(
                _overlayCanvas, _rootVisual);
        }
        else
        {
            // Insert behind all XAML content so the gradient sits
            // between the SystemBackdrop and the RootGrid.
            ElementCompositionPreview.SetElementChildVisual(host, _rootVisual);
        }
    }

    /// <summary>
    /// Rebuild the gradient brush from a new set of points.
    /// </summary>
    internal void RebuildBrush(IReadOnlyList<GradientPoint> points)
    {
        if (points.Count == 0)
        {
            _rootVisual.Brush = null;
            return;
        }

        if (points.Count == 1)
        {
            _rootVisual.Brush = CreateSinglePointBrush(points[0]);
            return;
        }

        // For multiple points, layer radial gradient brushes using
        // additive blending via CompositionColorBrush per-point
        // sprites in a container visual.
        _rootVisual.Brush = CreateMultiPointBrush(points);
    }

    private CompositionBrush CreateSinglePointBrush(GradientPoint pt)
    {
        var brush = _compositor.CreateRadialGradientBrush();
        brush.EllipseCenter = new Vector2(pt.X, pt.Y);
        brush.EllipseRadius = new Vector2(pt.Radius);
        brush.MappingMode = CompositionMappingMode.Relative;

        var stop0 = _compositor.CreateColorGradientStop(0f, pt.Color);
        var stop1 = _compositor.CreateColorGradientStop(1f,
            Windows.UI.Color.FromArgb(0, pt.Color.R, pt.Color.G, pt.Color.B));
        brush.ColorStops.Add(stop0);
        brush.ColorStops.Add(stop1);

        return brush;
    }

    private CompositionBrush? CreateMultiPointBrush(IReadOnlyList<GradientPoint> points)
    {
        // Use a container visual with one sprite per point, each
        // with its own radial gradient brush. The sprites use
        // additive blending to naturally merge the color blobs.
        //
        // Clear any previous children.
        _rootVisual.Children.RemoveAll();

        foreach (var pt in points)
        {
            var sprite = _compositor.CreateSpriteVisual();
            sprite.RelativeSizeAdjustment = Vector2.One;
            sprite.Brush = CreateSinglePointBrush(pt);
            _rootVisual.Children.InsertAtTop(sprite);
        }

        // Children handle rendering; the root visual doesn't need its own brush.
        return null;
    }

    /// <summary>
    /// Update the visual opacity to track background-opacity.
    /// For overlay mode, this scales the overlay canvas opacity.
    /// </summary>
    internal void SetOpacity(float opacity)
    {
        if (_isOverlay && _overlayCanvas is not null)
            _overlayCanvas.Opacity = opacity;
        else
            _rootVisual.Opacity = opacity;
    }

    /// <summary>
    /// Set the overlay strength (only applies in overlay mode).
    /// </summary>
    internal void SetOverlayOpacity(float opacity)
    {
        if (_overlayCanvas is not null)
            _overlayCanvas.Opacity = opacity;
    }

    /// <summary>
    /// Apply composable animation effects. The mode string is a
    /// comma-separated list of effects:
    ///   Position (pick one): drift, orbit, wander, bounce
    ///   Modifiers (stackable): breathe, color-cycle
    /// Example: "orbit,breathe,color-cycle"
    /// Legacy single values like "drift-color-cycle" still work.
    /// </summary>
    internal void ApplyAnimation(string mode, float speed)
    {
        StopAnimations();

        if (mode is "static" or "") return;
        if (_rootVisual.Children.Count == 0) return;

        var duration = TimeSpan.FromSeconds(8.0 / Math.Max(speed, 0.01f));

        // Parse comma-separated effects, also support legacy combined values.
        var effects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in mode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            effects.Add(part);

        // Expand legacy combined values.
        if (effects.Remove("drift-color-cycle"))
        {
            effects.Add("drift");
            effects.Add("color-cycle");
        }

        // Position effects (mutually exclusive, last wins).
        if (effects.Contains("bounce"))
            ApplyBounce(duration);
        else if (effects.Contains("wander"))
            ApplyWander(duration);
        else if (effects.Contains("orbit"))
            ApplyOrbit(duration);
        else if (effects.Contains("drift"))
            ApplyDrift(duration);

        // Modifier effects (stackable).
        if (effects.Contains("breathe"))
            ApplyBreathe(duration);
        if (effects.Contains("color-cycle"))
            ApplyColorCycle(duration);
    }

    private void ApplyDrift(TimeSpan duration)
    {
        var rng = new Random(42); // fixed seed: deterministic animation offsets across rebuilds
        var index = 0;
        foreach (var child in _rootVisual.Children)
        {
            if (child is not SpriteVisual sprite) continue;
            if (sprite.Brush is not CompositionRadialGradientBrush brush) continue;

            var center = brush.EllipseCenter;
            var dx = 0.05f * (float)(rng.NextDouble() * 2 - 1);
            var dy = 0.05f * (float)(rng.NextDouble() * 2 - 1);
            var phase = (float)index / _rootVisual.Children.Count;

            var anim = _compositor.CreateVector2KeyFrameAnimation();
            anim.InsertKeyFrame(0f, center);
            anim.InsertKeyFrame(0.25f + phase * 0.1f,
                new Vector2(center.X + dx, center.Y + dy));
            anim.InsertKeyFrame(0.5f, center);
            anim.InsertKeyFrame(0.75f - phase * 0.1f,
                new Vector2(center.X - dx, center.Y - dy));
            anim.InsertKeyFrame(1f, center);
            anim.Duration = duration;
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            brush.StartAnimation("EllipseCenter", anim);
            _animations.Add(anim);
            index++;
        }
    }

    private void ApplyOrbit(TimeSpan duration)
    {
        var index = 0;
        var count = _rootVisual.Children.Count;
        foreach (var child in _rootVisual.Children)
        {
            if (child is not SpriteVisual sprite) continue;
            if (sprite.Brush is not CompositionRadialGradientBrush brush) continue;

            var center = brush.EllipseCenter;
            // Orbit radius: 15% of window, each point at a different
            // phase around the circle for organic motion.
            var orbitR = 0.15f;
            var phaseOffset = (float)index / count * MathF.PI * 2f;

            var anim = _compositor.CreateVector2KeyFrameAnimation();
            // 12 keyframes around a circle for smooth motion.
            for (int i = 0; i <= 12; i++)
            {
                var t = (float)i / 12f;
                var angle = t * MathF.PI * 2f + phaseOffset;
                var pos = new Vector2(
                    center.X + MathF.Cos(angle) * orbitR,
                    center.Y + MathF.Sin(angle) * orbitR);
                anim.InsertKeyFrame(t, pos);
            }
            anim.Duration = duration;
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            brush.StartAnimation("EllipseCenter", anim);
            _animations.Add(anim);
            index++;
        }
    }

    private void ApplyWander(TimeSpan duration)
    {
        var rng = new Random(42); // fixed seed: deterministic animation offsets across rebuilds
        foreach (var child in _rootVisual.Children)
        {
            if (child is not SpriteVisual sprite) continue;
            if (sprite.Brush is not CompositionRadialGradientBrush brush) continue;

            var center = brush.EllipseCenter;

            // Generate 8 random waypoints clamped to [0,1], then
            // return to start for seamless looping.
            var anim = _compositor.CreateVector2KeyFrameAnimation();
            anim.InsertKeyFrame(0f, center);
            for (int i = 1; i <= 8; i++)
            {
                var wx = Math.Clamp(center.X + (float)(rng.NextDouble() - 0.5) * 0.6f, 0f, 1f);
                var wy = Math.Clamp(center.Y + (float)(rng.NextDouble() - 0.5) * 0.6f, 0f, 1f);
                anim.InsertKeyFrame(i / 9f, new Vector2(wx, wy));
            }
            anim.InsertKeyFrame(1f, center);
            anim.Duration = duration * 2; // slower for large movements
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            brush.StartAnimation("EllipseCenter", anim);
            _animations.Add(anim);
        }
    }

    private void ApplyBounce(TimeSpan duration)
    {
        var rng = new Random(42); // fixed seed: deterministic animation offsets across rebuilds
        foreach (var child in _rootVisual.Children)
        {
            if (child is not SpriteVisual sprite) continue;
            if (sprite.Brush is not CompositionRadialGradientBrush brush) continue;

            var center = brush.EllipseCenter;

            // Simulate bouncing: pick a velocity direction, generate
            // keyframes that reflect off [0,1] boundaries.
            var vx = (float)(rng.NextDouble() - 0.5) * 0.4f;
            var vy = (float)(rng.NextDouble() - 0.5) * 0.4f;

            var anim = _compositor.CreateVector2KeyFrameAnimation();
            var px = center.X;
            var py = center.Y;
            var steps = 16;

            for (int i = 0; i <= steps; i++)
            {
                anim.InsertKeyFrame((float)i / steps, new Vector2(px, py));
                px += vx;
                py += vy;
                // Reflect off edges.
                if (px < 0f) { px = -px; vx = -vx; }
                if (px > 1f) { px = 2f - px; vx = -vx; }
                if (py < 0f) { py = -py; vy = -vy; }
                if (py > 1f) { py = 2f - py; vy = -vy; }
            }
            anim.Duration = duration * 2;
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            brush.StartAnimation("EllipseCenter", anim);
            _animations.Add(anim);
        }
    }

    private void ApplyBreathe(TimeSpan duration)
    {
        var index = 0;
        var count = _rootVisual.Children.Count;
        foreach (var child in _rootVisual.Children)
        {
            if (child is not SpriteVisual sprite) continue;
            if (sprite.Brush is not CompositionRadialGradientBrush brush) continue;

            var baseRadius = brush.EllipseRadius;
            // Pulse between 80% and 120% of the base radius.
            var small = baseRadius * 0.8f;
            var large = baseRadius * 1.2f;
            // Phase offset so points don't all breathe in sync.
            var phase = (float)index / count;

            var anim = _compositor.CreateVector2KeyFrameAnimation();
            // Offset the start point based on phase.
            anim.InsertKeyFrame(0f, Vector2.Lerp(small, large, phase));
            anim.InsertKeyFrame(0.25f, large);
            anim.InsertKeyFrame(0.5f, Vector2.Lerp(large, small, phase));
            anim.InsertKeyFrame(0.75f, small);
            anim.InsertKeyFrame(1f, Vector2.Lerp(small, large, phase));
            anim.Duration = duration;
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            brush.StartAnimation("EllipseRadius", anim);
            _animations.Add(anim);
            index++;
        }
    }

    private void ApplyColorCycle(TimeSpan duration)
    {
        foreach (var child in _rootVisual.Children)
        {
            if (child is not SpriteVisual sprite) continue;
            if (sprite.Brush is not CompositionRadialGradientBrush brush) continue;
            if (brush.ColorStops.Count < 1) continue;

            var stop = brush.ColorStops[0];
            var baseColor = stop.Color;

            var anim = _compositor.CreateColorKeyFrameAnimation();
            for (int i = 0; i <= 6; i++)
            {
                var hueShift = i * 60f;
                var shifted = ShiftHue(baseColor, hueShift);
                anim.InsertKeyFrame(i / 6f, shifted);
            }
            anim.Duration = duration;
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            stop.StartAnimation("Color", anim);
            _animations.Add(anim);
        }
    }

    private void StopAnimations()
    {
        foreach (var child in _rootVisual.Children)
        {
            if (child is not SpriteVisual sprite) continue;
            if (sprite.Brush is not CompositionRadialGradientBrush brush) continue;
            brush.StopAnimation("EllipseCenter");
            brush.StopAnimation("EllipseRadius");
            foreach (var stop in brush.ColorStops)
                stop.StopAnimation("Color");
        }
        _animations.Clear();
    }

    /// <summary>
    /// Shift the hue of an RGB color by the given degrees.
    /// </summary>
    private static Windows.UI.Color ShiftHue(Windows.UI.Color color, float degrees)
    {
        float r = color.R / 255f;
        float g = color.G / 255f;
        float b = color.B / 255f;

        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float l = (max + min) / 2f;

        if (max == min)
            return color;

        float d = max - min;
        float s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        float h;

        if (max == r)
            h = ((g - b) / d + (g < b ? 6f : 0f)) * 60f;
        else if (max == g)
            h = ((b - r) / d + 2f) * 60f;
        else
            h = ((r - g) / d + 4f) * 60f;

        h = (h + degrees) % 360f;
        if (h < 0) h += 360f;

        return HslToColor(color.A, h, s, l);
    }

    private static Windows.UI.Color HslToColor(byte a, float h, float s, float l)
    {
        float c = (1f - Math.Abs(2f * l - 1f)) * s;
        float x = c * (1f - Math.Abs(h / 60f % 2f - 1f));
        float m = l - c / 2f;

        float r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Windows.UI.Color.FromArgb(a,
            (byte)Math.Clamp((r + m) * 255f, 0f, 255f),
            (byte)Math.Clamp((g + m) * 255f, 0f, 255f),
            (byte)Math.Clamp((b + m) * 255f, 0f, 255f));
    }

    public void Dispose()
    {
        StopAnimations();
        _rootVisual.Children.RemoveAll();
        _rootVisual.Brush?.Dispose();
        _rootVisual.Dispose();

        // Remove the overlay canvas from the XAML tree.
        if (_overlayCanvas?.Parent is Microsoft.UI.Xaml.Controls.Grid parent)
            parent.Children.Remove(_overlayCanvas);
    }
}
