using System.Drawing;
using Xunit;

namespace Ghostty.IconGen.Tests;

public class HazardStripeTests
{
    // Color values must match HazardStripe.StripeYellow.
    private static readonly Color StripeYellow = Color.FromArgb(0xFF, 0xF5, 0xC5, 0x18);

    [Fact]
    public void ApplyDrawsYellowPixelsInBottomBand()
    {
        using var bitmap = new Bitmap(256, 256);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(Color.Blue);

        HazardStripe.Apply(bitmap);

        int yellowCount = CountColor(bitmap, StripeYellow, tolerance: 8);
        Assert.True(yellowCount > 500,
            $"Expected hazard stripes to contain yellow pixels; got {yellowCount}.");
    }

    [Fact]
    public void ApplyLeavesTopEightyPercentUntouched()
    {
        using var bitmap = new Bitmap(256, 256);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(Color.Blue);

        HazardStripe.Apply(bitmap);

        // Top 80% (rows 0..204) should still be pure blue.
        for (int y = 0; y < 204; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                var c = bitmap.GetPixel(x, y);
                Assert.Equal(Color.Blue.ToArgb(), c.ToArgb());
            }
        }
    }

    private static int CountColor(Bitmap bitmap, Color target, int tolerance)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (Math.Abs(c.R - target.R) <= tolerance &&
                    Math.Abs(c.G - target.G) <= tolerance &&
                    Math.Abs(c.B - target.B) <= tolerance)
                    count++;
            }
        return count;
    }
}
