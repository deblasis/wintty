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
    /// The frame's material as it can actually be seen, given what the
    /// terminal's material left behind it.
    ///
    /// A translucent frame is a hole, and a hole shows something only while
    /// there is something on the far side of it. There is one SystemBackdrop
    /// per window, so a solid background leaves a frosted frame revealing
    /// nothing but the window's own opaque root -- which under
    /// window-theme=wintty is the palette the terminal is painted from, so
    /// the window comes up one flat colour with no chrome visible in it at
    /// all. Frosted and crystal therefore degrade to solid whenever what is
    /// behind them is not itself translucent, and take their normal opaque
    /// shade instead.
    ///
    /// Takes the effective backdrop rather than the configured one: low
    /// power and background-opacity both flatten the backdrop to solid
    /// without touching the config, and a frame over a flattened backdrop
    /// has just as little to reveal.
    /// </summary>
    public static string FrameOver(string frameStyle, string backdropStyle) =>
        backdropStyle is Frosted or Crystal ? frameStyle : Solid;

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
