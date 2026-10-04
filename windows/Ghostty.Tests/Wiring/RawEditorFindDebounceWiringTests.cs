using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The Raw Editor's find debounce survived every way of leaving the find bar.
/// <c>HideFindBar</c> wiped the highlights but left the timer armed, so the
/// tick it had already queued arrived ~300ms later and called
/// <c>UpdateFindMatches</c> against a collapsed bar: the highlights came back
/// (Esc straight after typing) and the match count was written to a control
/// nobody can see. The page's <c>Unloaded</c> did not stop it either, and the
/// DispatcherQueue outlives both the page and the settings window, so the
/// same tick could reach an <c>Editor</c> that had left the visual tree.
/// </summary>
/// <para>
/// Wiring guards, because the page cannot be loaded into a test host. They pin
/// the three sites the fix touched -- the bar's close, the page's unload, and
/// the tick itself -- because a page that stops the timer in two of the three
/// still loses the text to the third.
/// </para>
/// </summary>
public sealed class RawEditorFindDebounceWiringTests
{
    private static ShellSource Page() =>
        ShellSource.Load("Settings.Pages.RawEditorPage.xaml.cs");

    [Fact]
    public void Closing_the_find_bar_stops_the_debounce_before_it_clears()
    {
        var hide = Page().Method("HideFindBar");

        var stop = hide.Call("StopFindDebounce");
        var clear = hide.Call("ClearPreviousHighlights");

        Assert.True(
            stop.SpanStart < clear.SpanStart,
            "the timer must be stopped before the highlights are wiped; "
            + "otherwise the queued tick repaints what this call just cleared");
    }

    [Fact]
    public void Leaving_the_page_stops_the_debounce()
    {
        var unloaded = Page().Method("OnUnloaded");

        Assert.NotNull(unloaded.Body);
        unloaded.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "StopFindDebounce");
    }

    [Fact]
    public void The_debounce_stop_detaches_the_handler_as_well_as_stopping()
    {
        // Stop() alone does not unqueue a Tick the dispatcher already took:
        // the raise reads the invocation list, so a still-subscribed handler
        // runs against a torn-down editor. The SettingsWindow search timer
        // stops for the same reason.
        var stop = Page().Method("StopFindDebounce");

        var assignment = stop.Body!.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_findDebounce.Tick");
        Assert.Equal("-=", assignment.OperatorToken.ValueText);

        stop.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "_findDebounce.Stop");
    }

    [Fact]
    public void The_tick_asks_whether_the_bar_is_still_open_before_it_updates()
    {
        var tick = Page().Method("OnFindDebounce");

        var guard = tick.Body!.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("FindBar.Visibility", System.StringComparison.Ordinal));
        var update = tick.Call("UpdateFindMatches");

        Assert.True(
            guard.SpanStart < update.SpanStart,
            "UpdateFindMatches runs before the collapsed-bar check, so a tick "
            + "that outlived the find bar still repaints its highlights");

        // And the tick still stops itself: it is the only place the timer is
        // disarmed on the normal path.
        tick.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "StopFindDebounce");
    }
}
