using System;
using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="LaunchTexture"/>. Two contracts matter and
/// neither is visible by looking at the splash: that the turned crop always
/// covers the window, since a shortfall shows as a bare corner only at some
/// angles and window shapes, and that the crop never leaves the sheet, since
/// sampling past the edge shows as a hard line through the texture.
/// </summary>
public sealed class LaunchTextureTests
{
    private const int Sheet = 2048;

    [Theory]
    [InlineData(1200, 800)]
    [InlineData(800, 1200)]
    [InlineData(3840, 2160)]
    [InlineData(600, 600)]
    public void Crop_stays_inside_the_sheet(int width, int height)
    {
        // Sampling outside the source rect is not a crash, it is a hard edge
        // drawn across the middle of the splash, so this is checked over many
        // seeds rather than one.
        for (var seed = 0; seed < 500; seed++)
        {
            var placement = LaunchTexture.Resolve(seed, width, height, Sheet, Sheet);
            Assert.NotNull(placement);

            var place = placement!.Value;
            Assert.True(place.SourceX >= 0, $"seed {seed}: x {place.SourceX}");
            Assert.True(place.SourceY >= 0, $"seed {seed}: y {place.SourceY}");
            Assert.True(
                place.SourceX + place.SourceWidth <= Sheet + 0.001,
                $"seed {seed}: right edge {place.SourceX + place.SourceWidth}");
            Assert.True(
                place.SourceY + place.SourceHeight <= Sheet + 0.001,
                $"seed {seed}: bottom edge {place.SourceY + place.SourceHeight}");
        }
    }

    [Theory]
    [InlineData(1200, 800)]
    [InlineData(800, 1200)]
    [InlineData(3840, 2160)]
    [InlineData(600, 600)]
    public void Turned_destination_covers_every_corner_of_the_window(int width, int height)
    {
        for (var seed = 0; seed < 500; seed++)
        {
            var place = LaunchTexture.Resolve(seed, width, height, Sheet, Sheet)!.Value;

            // Turn each corner of the window back into the frame the image is
            // drawn in. If they all land inside the destination rectangle,
            // the image covers the window at that angle.
            var radians = -place.AngleDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);

            foreach (var (cx, cy) in new[]
                     {
                         (-width / 2.0, -height / 2.0), (width / 2.0, -height / 2.0),
                         (-width / 2.0, height / 2.0), (width / 2.0, height / 2.0),
                     })
            {
                var x = Math.Abs((cx * cos) - (cy * sin));
                var y = Math.Abs((cx * sin) + (cy * cos));

                Assert.True(
                    x <= (place.DestinationWidth / 2) + 0.001,
                    $"seed {seed}: corner at {x} outside {place.DestinationWidth / 2}");
                Assert.True(
                    y <= (place.DestinationHeight / 2) + 0.001,
                    $"seed {seed}: corner at {y} outside {place.DestinationHeight / 2}");
            }
        }
    }

    [Theory]
    [InlineData(1200, 800)]
    [InlineData(800, 1200)]
    [InlineData(3840, 2160)]
    [InlineData(600, 600)]
    [InlineData(600, 1400)]     // tall and narrow: the shape the clamp bites on
    [InlineData(3440, 1080)]    // and the same lopsidedness the other way
    public void Crop_is_never_smaller_than_the_sheet_grid_allows(int width, int height)
    {
        // Below this a crop can land wholly in the space between motifs and
        // come up empty. It is a floor on the zoom, not a preference.
        //
        // Checked over several window shapes because the shape is what
        // threatens it: a long window wants a crop too lopsided to both
        // clear the floor and fit the sheet, and this only passed before
        // because every case used was close to square.
        for (var seed = 0; seed < 500; seed++)
        {
            var place = LaunchTexture.Resolve(seed, width, height, Sheet, Sheet)!.Value;
            var shortest = Math.Min(place.SourceWidth, place.SourceHeight);

            Assert.True(
                shortest >= Sheet * LaunchTexture.MinimumCrop * 0.999,
                $"{width}x{height} seed {seed}: crop {shortest} below floor "
              + $"{Sheet * LaunchTexture.MinimumCrop}");
        }
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(1200, 0)]
    [InlineData(-1, 800)]
    [InlineData(1200, -1)]
    public void A_window_with_no_area_resolves_to_nothing(int width, int height)
    {
        // The drawing code leans on this: without the guard an aspect of
        // zero or infinity makes the crop NaN, which GDI+ then draws as
        // nothing in particular. Documented contract, so it gets a test.
        Assert.Null(LaunchTexture.Resolve(1, width, height, Sheet, Sheet));
    }

    [Theory]
    [InlineData(2048, 1024)]
    [InlineData(1024, 2048)]
    [InlineData(1600, 900)]
    public void A_sheet_that_is_not_square_still_crops_inside_itself(int sheetW, int sheetH)
    {
        // The overflow clamp is the non-square path, and every other test
        // here uses a square sheet, so nothing exercised it. A user
        // supplying their own texture is exactly how a non-square one
        // arrives.
        for (var seed = 0; seed < 300; seed++)
        {
            var place = LaunchTexture.Resolve(seed, 1200, 800, sheetW, sheetH)!.Value;

            Assert.True(place.SourceX >= 0 && place.SourceY >= 0, $"seed {seed}");
            Assert.True(
                place.SourceX + place.SourceWidth <= sheetW + 0.001,
                $"seed {seed}: right edge {place.SourceX + place.SourceWidth} of {sheetW}");
            Assert.True(
                place.SourceY + place.SourceHeight <= sheetH + 0.001,
                $"seed {seed}: bottom edge {place.SourceY + place.SourceHeight} of {sheetH}");
        }
    }

    [Fact]
    public void Crop_is_the_shape_it_is_drawn_at()
    {
        // Any mismatch is a stretch, and a stretched sheet shows as stave
        // lines of two different weights in the same picture.
        for (var seed = 0; seed < 200; seed++)
        {
            var place = LaunchTexture.Resolve(seed, 1600, 900, Sheet, Sheet)!.Value;

            Assert.Equal(
                place.DestinationWidth / place.DestinationHeight,
                place.SourceWidth / place.SourceHeight,
                3);
        }
    }

    [Fact]
    public void Same_seed_gives_the_same_placement()
    {
        var first = LaunchTexture.Resolve(4242, 1200, 800, Sheet, Sheet);
        var second = LaunchTexture.Resolve(4242, 1200, 800, Sheet, Sheet);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_seeds_move_the_crop()
    {
        // The whole point of the sheet is that launches differ, so identical
        // placements across seeds would be a silent failure of the feature
        // rather than of the code.
        var seen = new System.Collections.Generic.HashSet<LaunchTexture.Placement>();
        for (var seed = 0; seed < 50; seed++)
        {
            seen.Add(LaunchTexture.Resolve(seed, 1200, 800, Sheet, Sheet)!.Value);
        }

        Assert.True(seen.Count > 45, $"only {seen.Count} distinct placements in 50 seeds");
    }

    [Fact]
    public void Consecutive_launches_get_unrelated_angles()
    {
        // The regression this exists for: the seed is a clock, and a seeded
        // Random turns a steadily moving seed into a steadily moving first
        // draw. The angle came out as a slow sawtooth -- about four degrees
        // between launches a couple of seconds apart -- which on screen
        // reads as no rotation at all. Nothing above catches it, because
        // every individual angle was in range and every seed did give a
        // different one.
        const int TicksApart = 2000;                   // roughly two seconds
        var angles = new float[240];
        for (var i = 0; i < angles.Length; i++)
        {
            angles[i] = LaunchTexture
                .Resolve(123_456_789 + (i * TicksApart), 1200, 800, Sheet, Sheet)!.Value
                .AngleDegrees;
        }

        var range = LaunchTexture.MaximumAngle - LaunchTexture.MinimumAngle;

        var steps = new List<float>();
        for (var i = 1; i < angles.Length; i++) steps.Add(Math.Abs(angles[i] - angles[i - 1]));
        steps.Sort();
        var median = steps[steps.Count / 2];

        // The median, not the mean. A ramp takes the same small step almost
        // every time and one big one when it wraps, and those rare jumps drag
        // the mean up to nearly what a fair draw gives -- measured at 7.1 of
        // a 28 degree range, against 9.3 for uniform, which is too close to
        // separate. The median ignores the wrap: 4.2 broken against 9.7
        // fixed, where uniform gives 8.2.
        Assert.True(
            median > range / 5,
            $"consecutive launches differ by a median {median:F1} deg out of {range:F0}; "
          + "the angle is tracking the clock rather than being chosen");
    }

    [Fact]
    public void Content_is_never_upside_down()
    {
        // The invariant that matters most here, and the one nothing else
        // would catch: strictly inside a half turn either way. Inverted
        // writing reads as a rendering fault even when it is far too faint
        // to read, so a range that crosses +/-90 is broken however good it
        // looks in any single screenshot.
        for (var seed = 0; seed < 5000; seed++)
        {
            var angle = LaunchTexture.Resolve(seed, 1200, 800, Sheet, Sheet)!.Value.AngleDegrees;

            Assert.True(
                Math.Abs(angle) < 90f,
                $"seed {seed}: {angle} deg turns the content past vertical");
        }
    }

    [Fact]
    public void Angle_stays_in_range_and_off_vertical()
    {
        for (var seed = 0; seed < 2000; seed++)
        {
            var angle = LaunchTexture.Resolve(seed, 1200, 800, Sheet, Sheet)!.Value.AngleDegrees;

            Assert.InRange(angle, LaunchTexture.MinimumAngle, LaunchTexture.MaximumAngle);
        }

        // Both ends stop short of vertical, for the same reason neither
        // reaches level: a right angle is a special case the eye finds.
        Assert.True(Math.Abs(LaunchTexture.MinimumAngle) < 90f);
        Assert.True(Math.Abs(LaunchTexture.MaximumAngle) < 90f);
    }

    [Fact]
    public void The_whole_turn_range_gets_used()
    {
        // A range only counts if it is actually swept. Splitting it into
        // fifths and requiring every fifth catches a distribution that has
        // quietly collapsed toward one end.
        var fifths = new HashSet<int>();
        var range = LaunchTexture.MaximumAngle - LaunchTexture.MinimumAngle;

        for (var seed = 0; seed < 500; seed++)
        {
            var angle = LaunchTexture.Resolve(seed, 1200, 800, Sheet, Sheet)!.Value.AngleDegrees;
            fifths.Add((int)((angle - LaunchTexture.MinimumAngle) / range * 5) % 5);
        }

        Assert.Equal(5, fifths.Count);
    }

    [Theory]
    [InlineData(0x000000u, true)]      // black has only one direction to go
    [InlineData(0xFFFFFFu, false)]     // and so does white
    [InlineData(0x1E1E2Eu, true)]      // the default dark background
    [InlineData(0x808080u, false)]     // mid grey, just past the luma split
    public void Ink_steps_away_from_the_background(uint background, bool expectLighter)
    {
        // Derived from the constant rather than written out, because the
        // constant is a dial: it gets turned whenever the texture reads as
        // too faint or too loud, and a test that hard-codes the answer turns
        // every such adjustment into a failing test with nothing wrong.
        var step = expectLighter ? LaunchTexture.Contrast : -LaunchTexture.Contrast;
        var ink = LaunchTexture.ResolveInkRgb(background);

        foreach (var shift in new[] { 16, 8, 0 })
        {
            var before = (int)((background >> shift) & 0xFF);
            var after = (int)((ink >> shift) & 0xFF);
            Assert.Equal(Math.Clamp(before + step, 0, 255), after);
        }
    }

    [Fact]
    public void Ink_stays_within_a_channel()
    {
        // Every possible grey, so a background near either end cannot push a
        // channel past the range and wrap.
        for (var value = 0; value <= 255; value++)
        {
            var background = (uint)((value << 16) | (value << 8) | value);
            var ink = LaunchTexture.ResolveInkRgb(background);

            foreach (var shift in new[] { 16, 8, 0 })
            {
                Assert.InRange((ink >> shift) & 0xFF, 0u, 255u);
            }
        }
    }
}
