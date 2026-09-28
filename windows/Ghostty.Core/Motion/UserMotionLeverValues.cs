namespace Ghostty.Core.Motion;

/// <summary>
/// The <c>animations</c> key's vocabulary: the three written values, the
/// enum rung each maps to, and the fallback. Kept beside
/// <see cref="UserMotionLever"/> so the config spellings, the settings
/// combo's tags and the parse default live in one place.
///
/// An absent, blank or unrecognized value falls back to
/// <see cref="UserMotionLever.FollowSystem"/>, the card's default: the
/// same fail-open direction the OS animation reads take, and the reason
/// "system" is not spelled as its enum member here.
/// </summary>
public static class UserMotionLeverValues
{
    public const string FollowSystemValue = "system";
    public const string ReducedValue = "reduced";
    public const string OffValue = "off";

    /// <summary>
    /// The rung a written config value names. Case is folded and
    /// surrounding whitespace trimmed, matching how the other file-backed
    /// settings read their values.
    /// </summary>
    public static UserMotionLever Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            ReducedValue => UserMotionLever.Reduced,
            OffValue => UserMotionLever.Off,
            _ => UserMotionLever.FollowSystem,
        };

    /// <summary>The written form a settings page stores for a rung.</summary>
    public static string ConfigValue(UserMotionLever lever) => lever switch
    {
        UserMotionLever.Reduced => ReducedValue,
        UserMotionLever.Off => OffValue,
        _ => FollowSystemValue,
    };
}
