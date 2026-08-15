using System;

namespace Ghostty.Core.Shell;

/// <summary>
/// Decides which part of the splash texture sheet to show, how far to turn
/// it, and what colour to draw it in. Pure, so all three are testable
/// without a window or a device context.
/// </summary>
/// <remarks>
/// <para>Nothing is composed at launch. The sheet is drawn once, offline,
/// and shipped as a single image; all this does is choose a window into it
/// and an angle. That is the whole of the run-time cost: one transformed
/// blit, on the path whose job is to be on screen before the app is.</para>
///
/// <para>The variety comes from the choosing. A different crop, a different
/// angle and a different zoom each launch is enough for the same sheet to
/// look like a different piece of paper every time, which is far cheaper
/// than composing something new and, at this contrast, indistinguishable
/// from it.</para>
///
/// <para>It is texture, not illustration: drawn large, set at an angle, and
/// close enough to the background colour that it reads as something the
/// surface is made of rather than as a picture placed on it. The icon is
/// opaque and sits on top, the way a mark sits on printed paper.</para>
/// </remarks>
public static class LaunchTexture
{
    /// <summary>
    /// How far the ink sits from the background, per channel. This is the
    /// dial for how visible the texture is.
    /// </summary>
    /// <remarks>
    /// <para>A fixed step rather than a fraction of the remaining headroom.
    /// A fraction lands very differently on a near-black background than on
    /// a mid-grey one, and the point is that it reads the same on both.</para>
    ///
    /// <para>The sheet does not spend all of this. It is a mask that tops
    /// out below full, so the strongest mark on screen lands at roughly
    /// four fifths of this number. Raise this to make the texture more
    /// visible; the sheet needs no regenerating for it, since the tint is
    /// applied at draw time.</para>
    /// </remarks>
    public const int Contrast = 10;

    /// <summary>
    /// The narrowest crop, as a fraction of the sheet's shorter edge.
    /// </summary>
    /// <remarks>
    /// The sheet leaves about half its grid cells deliberately empty, so a
    /// crop has to be wide enough to span several of them or it can land in
    /// the gaps and come up blank. This is a floor on the zoom rather than a
    /// matter of taste, and it moves whenever the sheet's grid or how much
    /// of it is left empty moves. It is checked against the generator's own
    /// emptiness report, which probes at this size.
    /// </remarks>
    public const double MinimumCrop = 0.48;

    /// <summary>The widest crop. Above this there is little left to vary.</summary>
    public const double MaximumCrop = 0.72;

    /// <summary>
    /// The range the sheet may be turned within, in degrees.
    /// </summary>
    /// <remarks>
    /// <para>Wide, so that launches look genuinely differently turned rather
    /// than merely nudged, but strictly inside a half turn either way so the
    /// content is never inverted. Upside-down writing reads as a rendering
    /// fault even when it is far too faint to actually read, which is the
    /// one way this can look broken rather than incidental.</para>
    ///
    /// <para>Both ends stop well short of vertical. Near-vertical writing is
    /// as awkward as inverted writing for a different reason: it stops
    /// reading as writing at all and becomes a row of marks, which loses the
    /// only thing the motifs are there to suggest. Keeping the extremes off
    /// the vertical leaves every crop legible as what it is without any of
    /// it being readable.</para>
    /// </remarks>
    public const float MinimumAngle = -55f;

    public const float MaximumAngle = 55f;

    /// <summary>
    /// Where to take the texture from, and how far to turn it.
    /// </summary>
    /// <param name="SourceX">Left edge of the crop, in sheet pixels.</param>
    /// <param name="SourceY">Top edge of the crop, in sheet pixels.</param>
    /// <param name="SourceWidth">Crop width, in sheet pixels.</param>
    /// <param name="SourceHeight">Crop height, in sheet pixels.</param>
    /// <param name="DestinationWidth">
    /// Width to draw it at, centred on the window and turned about that
    /// centre. Larger than the window: see <see cref="Resolve"/>.
    /// </param>
    /// <param name="DestinationHeight">Height to draw it at.</param>
    /// <param name="AngleDegrees">Rotation about the window's centre.</param>
    public readonly record struct Placement(
        float SourceX,
        float SourceY,
        float SourceWidth,
        float SourceHeight,
        float DestinationWidth,
        float DestinationHeight,
        float AngleDegrees);

    /// <summary>
    /// Scramble the seed before anything is drawn from it.
    /// </summary>
    /// <remarks>
    /// <para>Without this the splash barely changed between launches, and
    /// the reason is not obvious. A seeded <see cref="Random"/> turns a seed
    /// that moves steadily into a <em>first draw</em> that moves steadily:
    /// its first value is very nearly a linear function of its seed. The
    /// seed here is a clock, which by definition moves steadily, so the
    /// angle came out as a slow sawtooth rather than as a choice. Two
    /// launches a couple of seconds apart differed by about four degrees,
    /// which reads as no rotation at all.</para>
    ///
    /// <para>An avalanche step fixes it: flipping any one bit of the input
    /// changes about half the bits of the output, so clock ticks a
    /// millisecond apart land nowhere near each other. Same seed still gives
    /// the same placement, which is what keeps this testable.</para>
    /// </remarks>
    private static int Mix(int seed)
    {
        var hash = (uint)seed;
        hash ^= hash >> 16;
        hash *= 0x7FEB352D;
        hash ^= hash >> 15;
        hash *= 0x846CA68B;
        hash ^= hash >> 16;
        return (int)hash;
    }

    /// <summary>
    /// Pick the colour to draw the texture in: a fixed step away from the
    /// background, in whichever direction the background leaves room for.
    /// </summary>
    public static uint ResolveInkRgb(uint backgroundRgb)
    {
        var r = (int)((backgroundRgb >> 16) & 0xFF);
        var g = (int)((backgroundRgb >> 8) & 0xFF);
        var b = (int)(backgroundRgb & 0xFF);

        // Rec. 601 luma. Which side of mid the background falls on is the
        // only question being asked, so the cheap weighting is enough: a
        // gamma-correct one would only move the answer on colours where
        // either direction reads about the same anyway.
        var luma = ((299 * r) + (587 * g) + (114 * b)) / 1000;
        var step = luma < 128 ? Contrast : -Contrast;

        return (uint)((Channel(r + step) << 16) | (Channel(g + step) << 8) | Channel(b + step));

        static int Channel(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
    }

    /// <summary>
    /// Choose a crop and an angle for one launch, or null when there is no
    /// sheet worth drawing or the window has no area.
    /// </summary>
    /// <param name="seed">
    /// Varies the result. The same seed always gives the same placement,
    /// which is what makes this testable.
    /// </param>
    /// <param name="windowWidth">Splash width in physical pixels.</param>
    /// <param name="windowHeight">Splash height in physical pixels.</param>
    /// <param name="sheetWidth">Texture sheet width in pixels.</param>
    /// <param name="sheetHeight">Texture sheet height in pixels.</param>
    public static Placement? Resolve(
        int seed, int windowWidth, int windowHeight, int sheetWidth, int sheetHeight)
    {
        if (windowWidth <= 0 || windowHeight <= 0) return null;
        if (sheetWidth <= 0 || sheetHeight <= 0) return null;

        var random = new Random(Mix(seed));

        var angle = MinimumAngle + ((float)random.NextDouble() * (MaximumAngle - MinimumAngle));

        // A level box turned by an angle covers a larger upright box, so to
        // leave no bare corner the image has to be drawn bigger than the
        // window by exactly that much. Turning a window-sized image instead
        // would show the background through two opposite corners.
        var radians = angle * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        var destinationWidth = (windowWidth * cos) + (windowHeight * sin);
        var destinationHeight = (windowWidth * sin) + (windowHeight * cos);

        // The crop is taken at the same shape as it will be drawn at, so the
        // texture is not stretched. Anything that stretches shows up as
        // stave lines of two different weights in the same picture.
        //
        // Except that three things are wanted at once and a long window
        // cannot have all of them: the crop must keep the destination's
        // shape, its shorter side must clear the floor, and its longer side
        // must fit the sheet. Those are jointly impossible once the shape is
        // more lopsided than the floor allows -- at a 0.48 floor, past about
        // 2.08 to 1 -- which a tall narrow terminal reaches whenever the
        // angle is small enough not to square it up again.
        //
        // The floor wins and the shape gives. A crop under the floor can
        // land between motifs and come up blank, which is the failure the
        // floor exists to stop; the stretch that buys it is a few percent of
        // a texture drawn at a few percent contrast, well under anything
        // visible.
        var limit = 1.0 / MinimumCrop;
        var aspect = Math.Clamp(destinationWidth / destinationHeight, 1.0 / limit, limit);

        var zoom = MinimumCrop + (random.NextDouble() * (MaximumCrop - MinimumCrop));

        // The zoom sets the SHORTER side of the crop, so which side that is
        // depends on the shape. Applying it to a fixed axis lets the other
        // one come out under the floor whenever the window leans the other
        // way, and an under-floor crop is one that can land in the gaps
        // between motifs and come up blank.
        double cropWidth, cropHeight;
        if (aspect >= 1)
        {
            cropHeight = sheetHeight * zoom;
            cropWidth = cropHeight * aspect;
        }
        else
        {
            cropWidth = sheetWidth * zoom;
            cropHeight = cropWidth / aspect;
        }

        // Scale both together if that ran off the sheet, since correcting one
        // alone would stretch the result.
        var overflow = Math.Max(cropWidth / sheetWidth, cropHeight / sheetHeight);
        if (overflow > 1)
        {
            cropWidth /= overflow;
            cropHeight /= overflow;
        }

        // Dividing by the overflow leaves the crop the size of the sheet to
        // within rounding, and rounding can leave it a hair over. Trimming
        // it and flooring the origin costs nothing and keeps the crop inside
        // the sheet by construction rather than by luck -- sampling past the
        // edge draws a hard line across the splash.
        cropWidth = Math.Min(cropWidth, sheetWidth);
        cropHeight = Math.Min(cropHeight, sheetHeight);

        var x = Math.Max(0, sheetWidth - cropWidth) * random.NextDouble();
        var y = Math.Max(0, sheetHeight - cropHeight) * random.NextDouble();

        return new Placement(
            (float)x,
            (float)y,
            (float)cropWidth,
            (float)cropHeight,
            (float)destinationWidth,
            (float)destinationHeight,
            angle);
    }
}
