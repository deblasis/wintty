using System;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That <c>WindowHelper.DpiForWorkArea</c> reads the DPI of the monitor that
/// owns the work area, not a system-wide number.
///
/// The sizing wiring pins that the sized windows ask this helper and feed its
/// answer to the scale; nothing there pins WHAT the helper reads. A rewrite
/// to <c>GetDpiForSystem</c> - one number for every monitor - keeps every one
/// of those rows green and reintroduces the mixed-DPI wrong-screen sizing one
/// layer down. The two joints that make the read per-monitor are pinned
/// here: the monitor resolves from the work area's own centre, and the DPI
/// read from it is the effective (scaled) one.
/// </summary>
public class WindowHelperDpiWiringTests
{
    private const string File = "Branding.WindowHelper.cs";

    [Fact]
    public void DpiComesFromTheMonitorThatOwnsTheWorkArea()
    {
        var body = ShellSource.Load(File).Method("DpiForWorkArea").Body!;

        var resolve = Assert.Single(body.Calls("PInvoke.MonitorFromPoint"));
        Assert.True(
            resolve.ArgumentList.Arguments.Count == 2
            && resolve.ArgumentList.Arguments[1].ToString().Contains(
                "MONITOR_DEFAULTTONEAREST", StringComparison.Ordinal),
            "the monitor must resolve from a point with MONITOR_DEFAULTTONEAREST; "
            + $"found `{resolve}`");

        var read = Assert.Single(body.Calls("PInvoke.GetDpiForMonitor"));
        Assert.True(
            read.ArgumentList.Arguments.Count == 4
            && read.ArgumentList.Arguments[1].ToString().Contains(
                "MDT_EFFECTIVE_DPI", StringComparison.Ordinal),
            "the DPI must be the monitor's EFFECTIVE DPI (the scaled one, what "
            + $"physical-pixel placement needs); found `{read}`");
    }
}
