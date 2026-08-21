namespace Ghostty.IconGen;

/// <summary>
/// Which product edition an icon is being generated for. Editions install
/// side by side, so their shortcuts sit next to each other in the Start
/// menu, the taskbar and Alt-Tab, and have to be tellable apart there.
/// </summary>
internal enum Edition
{
    /// <summary>The unmarked flagship mark. What every build produced
    /// before editions existed, and still the default.</summary>
    None,
    Pro,
    Enterprise,
    Legacy,
    Oss,
}

/// <summary>
/// How one edition differs from the flagship mark.
///
/// Two cues, deliberately, because each covers the other's blind spot:
/// hue is the only one that survives at 16 px, where the band is a
/// couple of pixels of mush; the band is the only one that survives
/// greyscale, a Windows high-contrast theme, or a user who cannot
/// separate amber from graphite. Either alone leaves a real user unable
/// to pick their shortcut.
/// </summary>
/// <param name="HueShiftDegrees">Rotation applied to the screen behind
/// the mark. Only saturated pixels move (see <see cref="TierTint"/>), so
/// the silver bezel and the white ghost keep their colour and the family
/// still reads as one product.</param>
/// <param name="SaturationScale">Multiplier on saturation. This is what
/// separates two editions that would otherwise land on neighbouring
/// hues: Legacy is a muted gold rather than a second amber, and Oss is
/// near-graphite.</param>
/// <param name="Monogram">Letters for the band, or empty for no band.
/// The flagship is deliberately unmarked so that carrying a mark means
/// "this is an edition" rather than every icon carrying furniture.</param>
internal sealed record EditionBrand(
    double HueShiftDegrees,
    double SaturationScale,
    string Monogram)
{
    /// <summary>
    /// Fraction of the icon height the monogram band occupies.
    ///
    /// 0.15 is not a taste choice. The ghost's lowest lit row in the
    /// shipping 256 px master is y=213, which is 83.2 percent down, so a
    /// band starting at 85 percent clears the mark with a few pixels to
    /// spare. A taller band looks better in isolation and starts eating
    /// the ghost's body, which is the thing this must not do.
    /// <see cref="MonogramBandTests.BandNeverReachesTheGhost"/> holds
    /// this to the real artwork rather than to this comment, and
    /// <see cref="HazardStripe.BandHeightFraction"/> is the same value so
    /// the nightly stripe and the edition band cannot disagree about
    /// where "the band" is.
    /// </summary>
    public const double BandHeightFraction = 0.15;

    /// <summary>
    /// Below this band height in pixels the letters stop being letters.
    /// A three-glyph monogram needs roughly this much to keep its
    /// counters open; under it the band is drawn as a plain bar, which
    /// carries the edition's colour honestly instead of rendering mush
    /// that reads as damage.
    /// </summary>
    public const int MinLegibleBandPx = 7;

    public static EditionBrand For(Edition edition) => edition switch
    {
        // Amber. The largest move off the base indigo, because Pro is the
        // edition most often installed next to the flagship.
        Edition.Pro => new EditionBrand(160, 1.05, "PRO"),

        // Teal. Far enough from both amber and the base blue to survive
        // being in the same taskbar as either.
        Edition.Enterprise => new EditionBrand(-62, 1.0, "ENT"),

        // Muted gold. The hue alone sits close to Pro's amber, so the
        // saturation drop is doing as much work here as the rotation.
        Edition.Legacy => new EditionBrand(172, 0.55, "LTS"),

        // Near-graphite. The open-source build reads as the plain one.
        Edition.Oss => new EditionBrand(0, 0.15, "OSS"),

        _ => new EditionBrand(0, 1.0, string.Empty),
    };
}
