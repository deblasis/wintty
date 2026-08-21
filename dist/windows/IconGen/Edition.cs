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
/// <param name="BandInk">The letters' colour. Paired with BandFill by hand
/// so the contrast holds; <c>EditionBrandTests</c> pins the ratio.</param>
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
    /// Its own constant, not an alias of <see cref="HazardStripe.BandHeightFraction"/>.
    /// They were shared on the grounds that the two bands could not then
    /// disagree about where "the band" is - but they no longer have the
    /// same job, and a nightly build of an edition now has to fit both into
    /// that space rather than one covering the other.
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
    /// Below this OUTPUT size the band carries no letters, only colour.
    ///
    /// Measured, not guessed: at a band height of 0.15 and an em of 0.92
    /// band heights, a three-glyph monogram is clean at 64 px and up,
    /// marginal at 48 and mush at 40. The previous design's floor was
    /// expressed in band pixels and evaluated on the MASTER, so every rung
    /// that downsampled from a larger master escaped it entirely and 40,
    /// 48 and 60 px all shipped the smudge the floor existed to prevent.
    /// This one is in output space because that is the size a human sees.
    /// </summary>
    public const int MinLetterSizePx = 64;

    public static EditionBrand For(Edition edition) => edition switch
    {
        // Amber on near-black. The largest move off the base indigo,
        // because Pro is the edition most often installed next to the
        // flagship. 9.3:1.
        Edition.Pro => new EditionBrand(
            "PRO", Color.FromArgb(0xFF, 0xF2, 0xA4, 0x13), Color.FromArgb(0xFF, 0x10, 0x13, 0x17)),

        // Teal. Far enough from both amber and the base blue to survive
        // being in the same taskbar as either. 9.0:1.
        Edition.Enterprise => new EditionBrand(
            "ENT", Color.FromArgb(0xFF, 0x3F, 0xC6, 0xCC), Color.FromArgb(0xFF, 0x0B, 0x14, 0x17)),

        // Bronze. Sits beside Pro's amber without competing with it, which
        // is the pairing that actually has to work: these two are the ones
        // installed together. 5.4:1.
        Edition.Legacy => new EditionBrand(
            "LTS", Color.FromArgb(0xFF, 0xA8, 0x76, 0x3C), Color.FromArgb(0xFF, 0x10, 0x13, 0x17)),

        // Slate. Light enough to read as a band rather than sinking into
        // the plate's own dark corner, which near-graphite did. 7.3:1.
        //
        // Unreachable in the shipping build: Directory.Build.props maps
        // only Wintty, Wintty.Pro, Wintty.Pro.Enterprise and
        // Wintty.Pro.Legacy, and the oss tier does not carry that patch.
        // Kept because it costs nothing and the mapping is one line away.
        Edition.Oss => new EditionBrand(
            "OSS", Color.FromArgb(0xFF, 0x98, 0xA2, 0xB0), Color.FromArgb(0xFF, 0x0F, 0x12, 0x16)),

        _ => new EditionBrand(string.Empty, Color.Empty, Color.Empty),
    };
}
