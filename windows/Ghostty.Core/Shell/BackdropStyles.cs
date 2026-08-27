namespace Ghostty.Core.Shell;

/// <summary>
/// Canonical string identifiers for the backdrop styles the main
/// window understands. Kept as constants rather than an enum so they
/// can be compared directly against values parsed from
/// <c>background-style</c> in the config without a separate
/// parse step, and used in pattern-matching switches.
/// Values are lowercase to match the config convention.
/// </summary>
public static class BackdropStyles
{
    public const string Frosted = "frosted";
    public const string Crystal = "crystal";
    public const string Solid = "solid";

    /// <summary>What an unset or unusable style falls back to.</summary>
    public const string Default = Frosted;

    /// <summary>
    /// Fold a raw config value to a known style.
    ///
    /// Config values arrive with their case and spacing intact and every
    /// comparison downstream is ordinal, so a config saying "Frosted" ran
    /// solid with nothing said about it. Reports false for anything
    /// unrecognised so one caller can say so once, instead of each
    /// comparison quietly disagreeing.
    /// </summary>
    public static bool TryNormalize(string? raw, out string style)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case Frosted: style = Frosted; return true;
            case Crystal: style = Crystal; return true;
            case Solid: style = Solid; return true;
            default: style = Default; return false;
        }
    }
}
