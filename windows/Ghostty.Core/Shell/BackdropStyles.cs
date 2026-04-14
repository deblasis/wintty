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
}
