using System.Drawing;

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
/// How one edition differs from the flagship mark: a coloured band across
/// the bottom carrying the edition's letters. The artwork above it is the
/// flagship's, untouched.
///
/// This replaced a design that rotated the hue of the whole plate and drew
/// a band as a second cue. The argument for two cues was sound - hue is the
/// only one that survives at 16 px, the band is the only one that survives
/// greyscale and high contrast - but the band never delivered its half. It
/// sampled its fill from the artwork at (50%, 62%), which lands inside the
/// paperclip's dark metal, the one region the tint deliberately never
/// touched. Measured across every shipped icon the band came out 96,96,98
/// for Pro and 96,96,97 for Legacy: the same grey. Under greyscale it
/// separated "an edition" from "the flagship" and told you nothing about
/// which edition.
///
/// So the cost of dropping the hue rotation is smaller than it looks, and
/// what it buys is an icon that still reads as this product. The honest
/// price is stated where it is paid: see MonogramBand, at and below 20 px
/// the band is too few pixels to carry letters, and editions are told apart
/// by band colour alone.
/// </summary>
/// <param name="Monogram">Letters for the band, or empty for no band. The
/// flagship is deliberately unmarked so that carrying a mark means "this is
/// an edition" rather than every icon carrying furniture.</param>
/// <param name="BandFill">The band's colour. Named here rather than derived
/// from the artwork, which is what the previous design tried and got wrong.</param>
/// <param name="BandInk">The letters' colour, dark or light depending on the
/// band. The ratios in For() were computed, not eyeballed, and
/// BandContrastIsLegible pins them: two of the four palettes proposed for this
/// change were under 4.0:1 and looked fine at 400 px.</param>
/// <remarks>
/// The three shipping editions are also a LUMINANCE ladder, worst separation
/// 2.51:1, because ink contrast alone does not make two bands tellable apart.
/// The first palette written for this change had amber at 0.455 and teal at
/// 0.458 - 1.01:1, the same grey - so under a high-contrast theme, or to
/// anyone who cannot separate the two hues, Pro and Enterprise were identical.
/// That is the exact failure the band was reinstated to fix, reintroduced by
/// picking colours that only had to look different in colour.
/// EditionsSeparateInGreyscale pins it now.
/// </remarks>
internal sealed record EditionBrand(
    string Monogram,
    Color BandFill,
    Color BandInk)
{
    /// <summary>
    /// Fraction of the icon height the band occupies.
    ///
    /// 0.15 is not a taste choice. The ghost's lowest lit row in the
    /// shipping 256 px master is y=213, which is 83.2 percent down, so a
    /// band starting at 85 percent clears the mark with a few pixels to
    /// spare, and 0.168 is the hard ceiling.
    /// <see cref="MonogramBandTests.BandNeverReachesTheGhost"/> holds this
    /// to the real artwork rather than to this comment.
    ///
    /// The hazard stripe used to share this constant, on the grounds that the
    /// two bands could not then disagree about where "the band" is. They no
    /// longer have the same job - a nightly build of an edition fits both into
    /// this space rather than one covering the other - so the stripe takes the
    /// rectangle it is handed and owns no fraction of its own.
    /// </summary>
    public const double BandHeightFraction = 0.15;

    /// <summary>
    /// Floor on the band's height in pixels.
    ///
    /// At 16 and 20 px the proportional band is 2 and 3 px, which the
    /// plate's corner arcs eat most of. Four pixels is what a colour needs
    /// to survive as a colour. It costs the ghost's last row of
    /// anti-aliasing at those two sizes only, which is why
    /// BandNeverReachesTheGhost is scoped above them.
    /// </summary>
    public const int MinBandPx = 4;

    /// <summary>
    /// Minimum height, in pixels, of the band's interior below its rule for
    /// letters to be drawn into it at all.
    ///
    /// Measured by looking: at 9 px of interior a three-glyph monogram is
    /// not small, it is broken - "PRO" reads "P?O", "LTS" reads "l F 4" -
    /// and by 10 px all three are clean. This is on the INTERIOR rather
    /// than on the icon's size because a nightly build of an edition halves
    /// the band, and an icon-size test let those through at exactly the
    /// dimensions this exists to refuse.
    ///
    /// 10 rather than 11 because the rim inset spends a row: at 80 px output
    /// the band is 12, the rim takes 1 and the rule takes 1, leaving 10. A
    /// floor of 11 there would silently move the first lettered rung from 80
    /// to 96 and leave AppIcon.scale-200 bare.
    /// </summary>
    public const int MinLetterBandPx = 10;

    /// <summary>
    /// Below this band height the top rule is suppressed. At 4 px it eats a
    /// quarter of the band to draw a line nobody can see.
    /// </summary>
    public const int MinRuleBandPx = 6;

    /// <summary>
    /// Below this band height a nightly edition stops splitting the band and
    /// draws the hazard alone. Two 2 px sub-bands are two illegible marks;
    /// the stated priority is that nightly reads first, so it takes the
    /// whole band and the edition cue is dropped at those sizes.
    /// </summary>
    public const int MinSplitBandPx = 12;

    public static EditionBrand For(Edition edition) => edition switch
    {
        // Light gold, dark ink. 12.9:1.
        Edition.Pro => new EditionBrand(
            "PRO", Color.FromArgb(0xFF, 0xFF, 0xD1, 0x66), Color.FromArgb(0xFF, 0x10, 0x13, 0x17)),

        // Deep teal, light ink. 9.3:1.
        Edition.Enterprise => new EditionBrand(
            "ENT", Color.FromArgb(0xFF, 0x0E, 0x4A, 0x52), Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA)),

        // Bronze, dark ink. 4.7:1.
        Edition.Legacy => new EditionBrand(
            "LTS", Color.FromArgb(0xFF, 0xA8, 0x76, 0x3C), Color.FromArgb(0xFF, 0x10, 0x13, 0x17)),

        // Slate. Unreachable in the shipping build - Directory.Build.props
        // maps only Wintty, Wintty.Pro, Wintty.Pro.Enterprise and
        // Wintty.Pro.Legacy - so it is excluded from the ladder above and
        // only has to clear the ink floor. Kept because the mapping is one
        // line away.
        Edition.Oss => new EditionBrand(
            "OSS", Color.FromArgb(0xFF, 0x5D, 0x66, 0x73), Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA)),

        _ => new EditionBrand(string.Empty, Color.Empty, Color.Empty),
    };
}
