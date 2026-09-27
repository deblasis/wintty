using Ghostty.Core;
using Ghostty.Core.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

/// <summary>
/// The exit-reason line's format is the contract (#967): support asks
/// for a grep of the log, and the grep only lands if the line is exactly
/// tag, code and reason in one shape. Tested through the
/// <see cref="ExitReason.Line"/> seam rather than by swapping the
/// process-global Console.Error under a parallel test host; the wiring
/// guards prove the call sites, and Log's single write is thin enough to
/// read - the live-exe runs outside this suite cover the write itself.
/// </summary>
public class ExitReasonTests
{
    [Fact]
    public void The_line_carries_tag_code_and_reason_in_order() =>
        Assert.Equal(
            AppIdentity.LogTag + " exit 0: help rendered",
            ExitReason.Line(0, "help rendered"));

    [Fact]
    public void Nonzero_codes_and_quoted_reasons_survive_the_format() =>
        Assert.Equal(
            AppIdentity.LogTag + " exit 3: cli action '+list-themes' finished",
            ExitReason.Line(3, "cli action '+list-themes' finished"));

    [Fact]
    public void Negative_codes_format_as_themselves() =>
        Assert.EndsWith(" exit -1: dropped", ExitReason.Line(-1, "dropped"));

    [Fact]
    public void Newlines_in_a_reason_cannot_forge_lines()
    {
        Assert.EndsWith(" exit 0: a b", ExitReason.Line(0, "a\nb"));
        Assert.EndsWith(" exit 0: a  b", ExitReason.Line(0, "a\r\nb"));
    }
}
