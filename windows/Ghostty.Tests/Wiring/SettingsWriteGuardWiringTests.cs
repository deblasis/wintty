using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Three guards on the settings pages advanced before the write they stood for
/// had happened, so a write that never landed was remembered as one that had:
/// re-typing the value then read as "unchanged" and was suppressed, leaving a
/// box that looked saved over a file that was not.
/// <para>
/// AdvancedPage's two boxes advance their guard from the debounced write's own
/// outcome now (the scheduler reports per-key outcomes; see
/// ConfigWriteSchedulerTests). AppearancePage's gradient points counted a
/// reload it never checked for a write, so a failed write left the count
/// holding a skip for the next change's echo and the editor showing points
/// that were never saved. Wiring guards, because the shell assembly cannot be
/// loaded into a test host.
/// </para>
/// </summary>
public sealed class SettingsWriteGuardWiringTests
{
    private static ShellSource AdvancedPage() =>
        ShellSource.Load("Settings.Pages.AdvancedPage.xaml.cs");

    private static ShellSource AppearancePage() =>
        ShellSource.Load("Settings.Pages.AppearancePage.xaml.cs");

    [Fact]
    public void The_debounced_write_helper_moves_the_guard_only_on_a_landed_write()
    {
        var helper = AdvancedPage().Method("WriteDebounced");

        // The callback the page hands the scheduler, and the single condition
        // under which the guard moves. Anything that moved it earlier -- on the
        // intent, on the queueing -- is the defect.
        var schedule = helper.Body!.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "scheduler.Schedule");

        var outcomeGuard = schedule.ArgumentList.Arguments
            .Select(a => a.Expression)
            .SelectMany(e => e.DescendantNodesAndSelf().OfType<IfStatementSyntax>())
            .Single(i => i.Condition.ToString().Contains("ConfigWriteOutcome.Written", System.StringComparison.Ordinal));

        var enqueue = schedule.ArgumentList.Arguments
            .SelectMany(a => a.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Single(c => c.CalleeText() == "DispatcherQueue.TryEnqueue");

        Assert.True(
            outcomeGuard.SpanStart < enqueue.SpanStart,
            "the guard must move only inside the Written branch; moving it "
            + "outside is what remembered a write that never happened");
    }

    [Fact]
    public void Both_boxes_route_through_the_helper_instead_of_assigning_their_guards()
    {
        var page = AdvancedPage();

        var quake = page.Method("QuakeKeyBox_LostFocus");
        Assert.Equal("() => _quakeKeyWritten = raw", quake.Call("WriteDebounced").Arg(2));
        Assert.Empty(quake.AssignsTo("_quakeKeyWritten").Where(a => a.Parent is StatementSyntax));

        var filter = page.Method("LogFilterBox_LostFocus");
        Assert.Equal("() => _logFilterWritten = filter", filter.Call("WriteDebounced").Arg(2));
        Assert.Empty(filter.AssignsTo("_logFilterWritten").Where(a => a.Parent is StatementSyntax));
    }

    [Fact]
    public void The_gradient_reload_skip_is_gated_on_the_write_having_landed()
    {
        var write = AppearancePage().Method("WriteAllPoints");

        var guard = write.Body!.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("WriteSucceeded", System.StringComparison.Ordinal));

        Assert.Contains(
            "Reloaded",
            guard.Condition.ToString(),
            System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The sibling that already had it right, pinned so the two cannot drift:
    /// WriteShaderPathValue returns before its guard moves when the write
    /// failed, and that guard is what makes a retry from the same page read as
    /// a change again.
    /// </summary>
    [Fact]
    public void The_shader_path_guard_still_gates_on_the_write_outcome()
    {
        var write = AppearancePage().Method("WriteShaderPathValue");

        var guard = write.Body!.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("WriteSucceeded", System.StringComparison.Ordinal));

        var advance = write.AssignsTo("_shaderPathWritten").Single();
        Assert.True(
            guard.SpanStart < advance.SpanStart,
            "_shaderPathWritten advances before the write outcome is checked");
    }
}
