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
}
