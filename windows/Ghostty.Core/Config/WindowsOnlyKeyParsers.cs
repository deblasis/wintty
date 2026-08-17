using System;
using System.Linq;

namespace Ghostty.Core.Config;

/// <summary>
/// Parsing helpers for the Windows-only config keys that libghostty
/// does not recognize. Used by <see cref="IConfigService"/> typed
/// accessors so every call site gets the same normalization and
/// default-fallback behavior. Kept pure (no I/O, no logging) so it
/// can be unit-tested without the XAML runtime.
/// </summary>
public static class WindowsOnlyKeyParsers
{
    public const int VerticalTabsWidthDefault = 220;
    public const int VerticalTabsWidthMin = 80;
    public const int VerticalTabsWidthMax = 600;

    public static bool ParseBool(string? raw, bool defaultValue)
    {
        // Only canonical true/false; 1/0 would diverge from Ghostty's parser.
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        var trimmed = raw.Trim();
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return defaultValue;
    }

    public static string ParseStringAllowed(
        string? raw,
        string[] allowed,
        string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        var normalized = raw.Trim().ToLowerInvariant();
        return allowed.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : defaultValue;
    }

    /// <summary>
    /// Invariant integer with inclusive clamp. Non-numeric (including
    /// unit suffixes like "220px") falls back. Empty/null falls back.
    /// </summary>
    public static int ParseIntClamped(string? raw, int fallback, int min, int max)
    {
        if (min > max) (min, max) = (max, min);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(
            raw.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var n))
        {
            return fallback;
        }
        return Math.Clamp(n, min, max);
    }
}
