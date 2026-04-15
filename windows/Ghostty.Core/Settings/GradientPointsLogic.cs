using System;
using System.Collections.Generic;

namespace Ghostty.Core.Settings;

/// <summary>
/// Pure positional helpers for <c>GradientPointsEditor</c>. Lives in
/// Ghostty.Core so it can be unit-tested without a XAML test host.
/// All coordinates are in normalized [0,1] canvas space.
/// </summary>
public static class GradientPointsLogic
{
    public static (float X, float Y) Clamp(float x, float y) =>
        (Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f));

    /// <summary>
    /// Returns the index of the topmost point whose center lies within
    /// <paramref name="handleRadius"/> of (<paramref name="px"/>,
    /// <paramref name="py"/>), or null if none match. Later points draw
    /// above earlier, so the scan runs from end to start.
    /// All values are in normalized [0,1] canvas space.
    /// </summary>
    public static int? HitTest(
        IReadOnlyList<(float X, float Y)> points,
        float px,
        float py,
        float handleRadius)
    {
        var r2 = handleRadius * handleRadius;
        for (int i = points.Count - 1; i >= 0; i--)
        {
            var dx = points[i].X - px;
            var dy = points[i].Y - py;
            if (dx * dx + dy * dy <= r2) return i;
        }
        return null;
    }
}
