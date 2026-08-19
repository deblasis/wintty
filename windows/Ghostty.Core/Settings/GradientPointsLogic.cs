using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ghostty.Core.Settings;

/// <summary>
/// Pure positional helpers for <c>GradientPointsEditor</c>. Lives in
/// Ghostty.Core so it can be unit-tested without a XAML test host.
/// All coordinates are in normalized [0,1] canvas space.
/// </summary>
public static partial class GradientPointsLogic
{
    public static (float X, float Y) Clamp(float x, float y) =>
        (Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f));

    /// <summary>
    /// A whole written position, anchored end to end: either the spoken
    /// form ("35% across, 60% down") or the bare pair ("35, 60").
    ///
    /// Anchored, and not "the first two numbers anywhere", because this is
    /// the only check between a client and a config write. Scanning loosely
    /// makes the round-trip a client is most likely to attempt -
    /// SetValue(element.Name) - parse "Gradient point 1 of 4" as 1% and 4%
    /// and silently move the point. It also makes a decimal-comma locale's
    /// "12,5, 87,5" land on 12% and 5% instead of being refused, which is
    /// the worse of the two failures.
    /// </summary>
    [GeneratedRegex(
        @"^\s*(-?\d+(?:\.\d+)?)\s*%?\s*(?:across)?\s*,\s*(-?\d+(?:\.\d+)?)\s*%?\s*(?:down)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PositionFormat();

    /// <summary>Longest string that could still be a written position.</summary>
    private const int MaxPositionLength = 64;

    /// <summary>
    /// What a client hears when focus lands on a handle. The ordinal
    /// carries it: the points are otherwise indistinguishable by name.
    /// </summary>
    public static string DescribeHandle(int index, int total) =>
        string.Format(CultureInfo.InvariantCulture, "Gradient point {0} of {1}", index + 1, total);

    /// <summary>
    /// A position spoken rather than plotted. "Across" and "down" beat
    /// "X" and "Y" out loud, and whole percents beat four decimals.
    ///
    /// Invariant on both sides: this string is a round-trip channel for a
    /// client, not prose, so it has to parse back under any culture.
    /// AwayFromZero so 34.5% and 35.5% round the same direction; banker's
    /// rounding would send one up and the other down.
    /// </summary>
    public static string DescribePosition(float x, float y) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}% across, {1}% down",
            Percent(x),
            Percent(y));

    private static int Percent(float v) =>
        (int)Math.Round(Math.Clamp(v, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Read a position a client wrote back. Out-of-range numbers clamp
    /// rather than fail - a client asking for 150% wants the edge - but a
    /// string that is not a position at all is refused outright.
    /// </summary>
    public static bool TryParsePosition(string? text, out float x, out float y)
    {
        x = 0f;
        y = 0f;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // The pattern has several optional runs of whitespace in a row, so a
        // long failing input costs quadratic backtracking. SetValue is an
        // arbitrary-string entry point any process on the desktop can reach,
        // and it runs on the settings window's UI thread; a real position is
        // a couple of dozen characters.
        if (text.Length > MaxPositionLength) return false;

        var match = PositionFormat().Match(text);
        if (!match.Success) return false;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
        {
            return false;
        }

        (x, y) = Clamp((float)(px / 100.0), (float)(py / 100.0));
        return true;
    }

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
