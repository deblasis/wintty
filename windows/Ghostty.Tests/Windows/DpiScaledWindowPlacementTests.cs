using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// The DPI-scaled, work-area-clamped placement the settings and about
/// windows are opened with.
///
/// Two defects live in this arithmetic, and they are separate. The first is
/// the scale: AppWindow sizes are physical pixels, so an unscaled design
/// size opens a 1100x750 window as 733x500 at 150%. The second is the clamp,
/// which the old inline version did not have at all: a 750-tall window
/// centered on a 768-tall work area computes y = -11 and starts above the
/// top of the screen.
///
/// The windows themselves are WinUI and cannot be loaded into a test host,
/// so <c>SettingsWindowSizingWiringTests</c> is what proves they still ask
/// for this; these cases are the numbers behind the ask.
/// </summary>
public sealed class DpiScaledWindowPlacementTests
{
    private static readonly WorkAreaRect WorkArea = new(X: 0, Y: 0, Width: 1920, Height: 1040);

    /// <summary>The settings shell's declared size, in design pixels.</summary>
    private const int DesignWidth = 1100;

    /// <summary>And its height.</summary>
    private const int DesignHeight = 750;

    [Fact]
    public void At96Dpi_TheDesignSizePassesThroughUnchanged()
    {
        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 96, WorkArea);

        Assert.Equal(1100, rect.Width);
        Assert.Equal(750, rect.Height);

        // Centered on the work area: (1920-1100)/2 = 410, (1040-750)/2 = 145.
        Assert.Equal(410, rect.X);
        Assert.Equal(145, rect.Y);
    }

    [Fact]
    public void At150Percent_TheDesignSizeIsScaledAndStillCentered()
    {
        // 1100 * 1.5 = 1650, 750 * 1.5 = 1125, which is taller than the
        // 1040 work area, so the height clamps and the window sits at the
        // work area's top edge.
        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 144, WorkArea);

        Assert.Equal(1650, rect.Width);
        Assert.Equal(1040, rect.Height);
        Assert.Equal(135, rect.X);
        Assert.Equal(0, rect.Y);
    }

    [Fact]
    public void At150Percent_OnATallEnoughWorkAreaTheScaledHeightIsKept()
    {
        // The same 150% monitor with a work area that has room: the scaled
        // height is the honest answer and must not be trimmed.
        var tall = new WorkAreaRect(X: 0, Y: 0, Width: 2560, Height: 1400);

        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 144, tall);

        Assert.Equal(1650, rect.Width);
        Assert.Equal(1125, rect.Height);
        Assert.Equal((2560 - 1650) / 2, rect.X);
        Assert.Equal((1400 - 1125) / 2, rect.Y);
    }

    [Fact]
    public void At200Percent_TheDesignSizeDoubles()
    {
        var tall = new WorkAreaRect(X: 0, Y: 0, Width: 3840, Height: 2160);

        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 192, tall);

        Assert.Equal(2200, rect.Width);
        Assert.Equal(1500, rect.Height);
        Assert.Equal((3840 - 2200) / 2, rect.X);
        Assert.Equal((2160 - 1500) / 2, rect.Y);
    }

    [Fact]
    public void AnUnreadableDpiIsTreatedAsUnscaled()
    {
        // GetDpiForWindow returns 0 for a window that has gone or a monitor
        // the process cannot read. Zero must not mean a zero-sized window.
        var zero = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 0, WorkArea);
        var unscaled = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 96, WorkArea);

        Assert.Equal(unscaled, zero);
        Assert.Equal(1.0, DpiScaledWindowPlacement.Scale(0));
    }

    [Fact]
    public void AWorkAreaShorterThanTheWindowClampsTheHeightAndPinsTheTop()
    {
        // A 720-tall work area (a 1366x768 laptop with a taskbar) under a
        // 750-tall window. Centering that without a clamp gives y = -15, so
        // the title bar starts above the top of the screen.
        var low = new WorkAreaRect(X: 0, Y: 0, Width: 1366, Height: 720);

        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 96, low);

        Assert.Equal(720, rect.Height);
        Assert.Equal(0, rect.Y);
        Assert.Equal((1366 - 1100) / 2, rect.X);
    }

    [Fact]
    public void TheScaledWindowIsTheOneThatOffscreensOnAShortDisplay()
    {
        // The 768-tall work area is only too short once the design size is
        // scaled, which is the whole point of the clamp: at 150% the height
        // is 1125 against 728 of work area, and centering that unclamped
        // starts the window 198 pixels above the screen.
        var laptop = new WorkAreaRect(X: 0, Y: 0, Width: 1366, Height: 728);

        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 144, laptop);

        // 1650 of scaled width does not fit 1366 either, so both axes clamp
        // and the window takes the whole work area.
        Assert.Equal(1366, rect.Width);
        Assert.Equal(0, rect.X);
        Assert.Equal(728, rect.Height);
        Assert.Equal(0, rect.Y);
    }

    [Fact]
    public void AWorkAreaNarrowerThanTheWindowClampsTheWidthAndPinsTheLeft()
    {
        // A 1024-wide monitor: the window gets the work area rather than
        // starting half off the left of the screen.
        var narrow = new WorkAreaRect(X: 0, Y: 40, Width: 1024, Height: 1040);

        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 96, narrow);

        Assert.Equal(1024, rect.Width);
        Assert.Equal(0, rect.X);
        Assert.Equal(40 + (1040 - 750) / 2, rect.Y);
    }

    [Fact]
    public void ASecondMonitorLeftOfThePrimaryKeepsItsOwnOrigin()
    {
        // Monitors to the left of the primary have negative X. The window
        // must center on THAT monitor and never come back past its origin.
        var left = new WorkAreaRect(X: -1920, Y: -120, Width: 1920, Height: 1080);

        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 96, left);

        Assert.Equal(-1920 + (1920 - 1100) / 2, rect.X);
        Assert.Equal(-120 + (1080 - 750) / 2, rect.Y);
        Assert.True(rect.X >= left.X, $"x {rect.X} is left of the work area origin {left.X}");
        Assert.True(rect.Y >= left.Y, $"y {rect.Y} is above the work area origin {left.Y}");
    }

    [Fact]
    public void ASecondMonitorRightOfThePrimaryCentersOnThatMonitor()
    {
        var right = new WorkAreaRect(X: 1920, Y: 0, Width: 1280, Height: 1024);

        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 96, right);

        Assert.Equal(1920 + (1280 - 1100) / 2, rect.X);
        Assert.Equal((1024 - 750) / 2, rect.Y);
    }

    [Fact]
    public void AWorkAreaWithATaskbarOffsetIsHonouredOnBothAxes()
    {
        // The work area is not the monitor: a taskbar docked left or on top
        // moves its origin off 0, and centering has to follow it.
        var offset = new WorkAreaRect(X: 80, Y: 40, Width: 1840, Height: 1000);

        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 96, offset);

        Assert.Equal(80 + (1840 - 1100) / 2, rect.X);
        Assert.Equal(40 + (1000 - 750) / 2, rect.Y);
    }

    [Fact]
    public void AScaledSizeIsRoundedRatherThanTruncated()
    {
        // 101 * 1.5 = 151.5. Truncating would land a pixel short every time
        // a design size is odd, which is how a scale factor leaks into the
        // window's content budget.
        var tall = new WorkAreaRect(X: 0, Y: 0, Width: 2560, Height: 1400);

        var rect = DpiScaledWindowPlacement.Compute(
            101, 101, dpi: 144, tall);

        Assert.Equal(152, rect.Width);
        Assert.Equal(152, rect.Height);
    }

    [Fact]
    public void ADegenerateWorkAreaStillYieldsAUsableRect()
    {
        // A display that reports nothing must not produce a zero-sized or
        // negative-offset window; the OS answers a zero with its own
        // minimum, on a screen we did not pick.
        var rect = DpiScaledWindowPlacement.Compute(
            DesignWidth, DesignHeight, dpi: 96,
            new WorkAreaRect(X: 0, Y: 0, Width: 0, Height: 0));

        Assert.Equal(1, rect.Width);
        Assert.Equal(1, rect.Height);
        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
    }

    [Fact]
    public void TheAboutPanelsDesignSizeFollowsTheSameRule()
    {
        // 420x560 at 150% on a 4K monitor. Named because it is the other
        // window this helper exists for, and it is a different size.
        var tall = new WorkAreaRect(X: 0, Y: 0, Width: 3840, Height: 2160);

        var rect = DpiScaledWindowPlacement.Compute(420, 560, dpi: 144, tall);

        Assert.Equal(630, rect.Width);
        Assert.Equal(840, rect.Height);
        Assert.Equal((3840 - 630) / 2, rect.X);
        Assert.Equal((2160 - 840) / 2, rect.Y);
    }
}
