using Ghostty.Core.Hosting;
using Xunit;

namespace Ghostty.Tests.Hosting;

// Pure-logic placement coverage for the quake window resolver.
// Reference monitor is 1920x1080 at the origin; tests that need a
// portrait monitor or a negative origin construct their own.
public class QuickTerminalGeometryTests
{
    // Default monitor: 1920x1080 work-area at (0, 0).
    private static readonly MonitorBounds Hd = new(0, 0, 1920, 1080);

    // No size specified -> primary=50%, secondary=100%.
    private static readonly QuickTerminalSize Default = new(Primary: null, Secondary: null);

    // ---- Defaults across all five positions --------------------------------

    [Fact]
    public void Top_Default_Spans_Full_Width_And_Half_Height_At_Top()
    {
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Top, Default, Hd);
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 540), rect);
    }

    [Fact]
    public void Bottom_Default_Spans_Full_Width_And_Half_Height_At_Bottom()
    {
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Bottom, Default, Hd);
        Assert.Equal(new QuickTerminalRect(0, 540, 1920, 540), rect);
    }

    [Fact]
    public void Left_Default_Spans_Half_Width_And_Full_Height_At_Left()
    {
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Left, Default, Hd);
        Assert.Equal(new QuickTerminalRect(0, 0, 960, 1080), rect);
    }

    [Fact]
    public void Right_Default_Spans_Half_Width_And_Full_Height_At_Right()
    {
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Right, Default, Hd);
        Assert.Equal(new QuickTerminalRect(960, 0, 960, 1080), rect);
    }

    [Fact]
    public void Center_Default_On_Landscape_Is_Half_Height_Full_Width_Centered()
    {
        // Landscape monitor: primary axis is height, so default
        // primary=50% of 1080 = 540, secondary=100% of 1920 = 1920.
        // Both axes are centered in the monitor's work area.
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Center, Default, Hd);
        Assert.Equal(new QuickTerminalRect(0, 270, 1920, 540), rect);
    }

    // ---- Primary-only Percentage across positions --------------------------

    [Theory]
    [InlineData(25.0, 270)]
    [InlineData(50.0, 540)]
    [InlineData(75.0, 810)]
    public void Top_Primary_Percentage_Sets_Height(double pct, int expectedHeight)
    {
        var size = new QuickTerminalSize(Primary: Dimension.Percentage(pct), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Top, size, Hd);
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, expectedHeight), rect);
    }

    [Theory]
    [InlineData(25.0, 480)]
    [InlineData(50.0, 960)]
    public void Left_Primary_Percentage_Sets_Width(double pct, int expectedWidth)
    {
        var size = new QuickTerminalSize(Primary: Dimension.Percentage(pct), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Left, size, Hd);
        Assert.Equal(new QuickTerminalRect(0, 0, expectedWidth, 1080), rect);
    }

    // ---- Primary-only Pixels ----------------------------------------------

    [Fact]
    public void Top_Primary_Pixels_Sets_Height_To_Exact_Value()
    {
        var size = new QuickTerminalSize(Primary: Dimension.Pixels(300), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Top, size, Hd);
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 300), rect);
    }

    [Fact]
    public void Bottom_Primary_Pixels_Anchors_Window_To_Bottom_Edge()
    {
        var size = new QuickTerminalSize(Primary: Dimension.Pixels(200), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Bottom, size, Hd);
        Assert.Equal(new QuickTerminalRect(0, 880, 1920, 200), rect);
    }

    [Fact]
    public void Right_Primary_Pixels_Anchors_Window_To_Right_Edge()
    {
        var size = new QuickTerminalSize(Primary: Dimension.Pixels(500), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Right, size, Hd);
        Assert.Equal(new QuickTerminalRect(1420, 0, 500, 1080), rect);
    }

    // ---- Both axes specified ----------------------------------------------

    [Fact]
    public void Top_Primary_And_Secondary_Both_Honored()
    {
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(400),
            Secondary: Dimension.Pixels(1200));
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Top, size, Hd);
        Assert.Equal(new QuickTerminalRect(0, 0, 1200, 400), rect);
    }

    [Fact]
    public void Left_Both_Axes_Honored_As_Percentages()
    {
        var size = new QuickTerminalSize(
            Primary: Dimension.Percentage(30),
            Secondary: Dimension.Percentage(80));
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Left, size, Hd);
        // Primary axis = width = 30% of 1920 = 576.
        // Secondary axis = height = 80% of 1080 = 864.
        Assert.Equal(new QuickTerminalRect(0, 0, 576, 864), rect);
    }

    // ---- Center: landscape vs portrait -------------------------------------

    [Fact]
    public void Center_On_Portrait_Monitor_Primary_Axis_Is_Width()
    {
        var portrait = new MonitorBounds(0, 0, 1080, 1920);
        // Primary defaults to 50% of width (540), secondary to full height (1920).
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Center, Default, portrait);
        Assert.Equal(new QuickTerminalRect(270, 0, 540, 1920), rect);
    }

    [Fact]
    public void Center_On_Portrait_Both_Axes_Specified()
    {
        var portrait = new MonitorBounds(0, 0, 1080, 1920);
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(800),    // primary = width
            Secondary: Dimension.Pixels(1200)); // secondary = height
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Center, size, portrait);
        // Centered: x = (1080 - 800)/2 = 140, y = (1920 - 1200)/2 = 360.
        Assert.Equal(new QuickTerminalRect(140, 360, 800, 1200), rect);
    }

    [Fact]
    public void Center_On_Square_Monitor_Behaves_As_Landscape()
    {
        // Width == Height ties the IsLandscape branch toward landscape.
        var square = new MonitorBounds(0, 0, 1000, 1000);
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(400),
            Secondary: Dimension.Pixels(800));
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Center, size, square);
        // Landscape: primary = height = 400, secondary = width = 800.
        // x = (1000 - 800)/2 = 100, y = (1000 - 400)/2 = 300.
        Assert.Equal(new QuickTerminalRect(100, 300, 800, 400), rect);
    }

    // ---- Clamping ---------------------------------------------------------

    [Fact]
    public void Oversize_Percentage_Saturates_To_Full_Parent_Dimension()
    {
        var size = new QuickTerminalSize(
            Primary: Dimension.Percentage(150), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Top, size, Hd);
        // 150% of 1080 -> clamped to 1080.
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 1080), rect);
    }

    [Fact]
    public void Oversize_Pixels_Saturates_To_Full_Parent_Dimension()
    {
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(9999), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Top, size, Hd);
        // 9999 clamped to 1080.
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 1080), rect);
    }

    [Fact]
    public void Oversize_Secondary_Pixels_Saturates_To_Full_Parent_Dimension()
    {
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(300),
            Secondary: Dimension.Pixels(9999));
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Top, size, Hd);
        // Secondary 9999 (width) clamped to 1920.
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 300), rect);
    }

    [Fact]
    public void Zero_Percentage_Clamps_Up_To_Minimum_Of_One_Pixel()
    {
        var size = new QuickTerminalSize(
            Primary: Dimension.Percentage(0), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Top, size, Hd);
        // 0% clamped to floor of 1 px so the window stays addressable.
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 1), rect);
    }

    // ---- Negative monitor origin (multi-monitor) --------------------------

    [Fact]
    public void Negative_Origin_Bottom_Anchors_To_Negative_Monitor_Bottom()
    {
        // Secondary monitor to the left of primary: top-left at (-1920, 0).
        var left = new MonitorBounds(-1920, 0, 1920, 1080);
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(200), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Bottom, size, left);
        // X stays at monitor.X = -1920; Y = bottom (1080) - 200 = 880.
        Assert.Equal(new QuickTerminalRect(-1920, 880, 1920, 200), rect);
    }

    [Fact]
    public void Negative_Origin_Right_Anchors_To_Negative_Monitor_Right_Edge()
    {
        var left = new MonitorBounds(-1920, 0, 1920, 1080);
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(500), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Right, size, left);
        // Right edge of this monitor is 0; window starts at 0 - 500 = -500.
        Assert.Equal(new QuickTerminalRect(-500, 0, 500, 1080), rect);
    }

    [Fact]
    public void Negative_Origin_Center_Stays_Inside_Monitor_Bounds()
    {
        var left = new MonitorBounds(-1920, 0, 1920, 1080);
        var rect = QuickTerminalGeometry.Resolve(
            QuickTerminalPosition.Center, Default, left);
        // Default landscape center: secondary=full width (1920), primary=540
        // vertically centered. X = -1920 + (1920-1920)/2 = -1920.
        Assert.Equal(new QuickTerminalRect(-1920, 270, 1920, 540), rect);
    }

    // ---- Position-anchoring sanity ----------------------------------------

    [Fact]
    public void Bottom_X_Equals_Monitor_X_Regardless_Of_Size()
    {
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(300),
            Secondary: Dimension.Pixels(500));
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Bottom, size, Hd);
        // X stays at monitor.X because secondary axis is independent of position.
        Assert.Equal(Hd.X, rect.X);
    }

    [Fact]
    public void Right_Width_Equals_Primary_Pixels()
    {
        var size = new QuickTerminalSize(
            Primary: Dimension.Pixels(600), Secondary: null);
        var rect = QuickTerminalGeometry.Resolve(QuickTerminalPosition.Right, size, Hd);
        Assert.Equal(600, rect.Width);
    }
}
