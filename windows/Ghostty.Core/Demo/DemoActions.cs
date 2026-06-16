#if DEMO
using System;
using Ghostty.Core.Input;

namespace Ghostty.Core.Demo;

/// <summary>
/// Resolves a demo "action" beat's <c>key</c> string to a <see cref="PaneAction"/>.
/// Tolerates snake_case and any casing ("split_vertical", "SplitVertical") by
/// stripping underscores and parsing case-insensitively against the enum.
/// </summary>
internal static class DemoActions
{
    public static bool TryParse(string? key, out PaneAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var normalized = key.Replace("_", string.Empty);
        if (normalized.Length == 0)
            return false; // e.g. "_" / "__" -- nothing left to parse.

        // Reject numeric input: Enum.TryParse("8") parses the underlying value
        // and IsDefined accepts it, silently invoking whatever action has that
        // value instead of warn+skip. Demo action keys are always names.
        if (char.IsDigit(normalized[0]) || normalized[0] == '-')
            return false;

        return Enum.TryParse(normalized, ignoreCase: true, out action)
            && Enum.IsDefined(action);
    }
}
#endif
