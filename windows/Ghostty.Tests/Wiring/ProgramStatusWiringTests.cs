using System;
using Ghostty.Tests.Wiring;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Source-level pins for the OSC 7501 baseline: the host decodes the
/// action inside the callback and raises it on the control, the pane host
/// folds every leaf into one tab-level worst, the tab carries it, and
/// both strips render a small state glyph that stands down when a richer
/// presentation claims the slot. The pro family's agents feature is that
/// richer presentation; this baseline is what every tier ships.
/// </summary>
public class ProgramStatusWiringTests
{
    [Fact]
    public void Host_DecodesInsideTheCallback_AndRaisesOnTheUiThread()
    {
        var host = ShellSource.Load("Hosting.GhosttyHost.cs");
        var section = host.Case("OnAction", "GhosttyActionTag.ProgramStatus");
        var decode = section.ToString().IndexOf("ProgramStatusActionDecoder.TryDecode(actionPtr + 8", StringComparison.Ordinal);
        var enqueue = section.ToString().IndexOf("_dispatcher.TryEnqueue", StringComparison.Ordinal);
        Assert.True(decode >= 0, "the payload is not decoded inside the callback");
        Assert.True(enqueue > decode, "the decode must happen before the enqueue: the report is borrowed");
        Assert.Contains("RaiseProgramStatus(programStatus)", section.ToString());
    }

    [Fact]
    public void Host_OptsInOnceTheAppExists()
    {
        var host = ShellSource.Load("Hosting.GhosttyHost.cs").Root.ToString();
        var created = host.IndexOf("NativeMethods.AppNew(", StringComparison.Ordinal);
        var optedIn = host.IndexOf("NativeMethods.AppSetProgramStatus(", StringComparison.Ordinal);
        Assert.True(created >= 0 && optedIn > created,
            "the opt-in follows the app's creation, in the bootstrap host");
    }

    [Fact]
    public void TerminalControl_FoldsEventsIntoItsCurrentContribution()
    {
        var control = ShellSource.Load("Controls.TerminalControl.xaml.cs");
        var raise = control.Method("RaiseProgramStatus").ToString();
        Assert.Contains("TabProgramStatusRules.Apply(", raise);
        Assert.Contains("CurrentProgramStatus =", raise);
        Assert.Contains("event EventHandler<Ghostty.Core.Interop.ProgramStatusEvent>? ProgramStatusChanged",
            control.Root.ToString());
        var viewed = control.Method("MarkProgramStatusViewed").ToString();
        Assert.Contains("TabProgramStatusRules.Viewed(", viewed);
    }

    [Fact]
    public void PaneHost_SubscribesEveryLeaf_AndEmitsTheWorstAcrossThem()
    {
        var paneHost = ShellSource.Load("Panes.PaneHost.cs");
        Assert.Contains("ProgramStatusChanged += OnTerminalProgramStatus",
            paneHost.Root.ToString());
        var emit = paneHost.Method("EmitProgramStatus").ToString();
        Assert.Contains("PaneTree.Leaves", emit);
        Assert.Contains("TabProgramStatusRules.Worst(", emit);
        // Focus is what retires a done: the pane being looked at has no
        // unviewed done left to show.
        Assert.Contains("MarkProgramStatusViewed", paneHost.Root.ToString());
    }

    [Fact]
    public void TabManager_ForwardsTheHostsStatusOntoTheTab_InBothWiringPaths()
    {
        var manager = ShellSource.Load("Tabs.TabManager.cs").Root.ToString();
        // Both spellings (host in CreateTab, tab.PaneHost in the adopter)
        // end the same way, so the tail count is the wiring count.
        Assert.Equal(2, Count(manager, "ProgramStatusChanged += statusHandler"));
        Assert.Equal(2, Count(manager, "ProgramStatusChanged -= statusHandler"));
    }

    [Fact]
    public void TabModel_CarriesTheStatus_AndTheClaimedFlag()
    {
        var model = ShellSource.Load("Tabs.TabModel.cs").Root.ToString();
        Assert.Contains("public TabProgramStatus ProgramStatus", model);
        Assert.Contains("ProgramStatusPresentationClaimed", model);
    }

    [Fact]
    public void BothStrips_RenderTheStateGlyph_AndHonorAClaimedSlot()
    {
        var strip = ShellSource.Load("Tabs.TabHost.xaml.cs").Root.ToString();
        Assert.Contains("TabProgramStatusChrome.ProgramStatusGlyph(", strip);
        var row = ShellSource.Load("Tabs.VerticalTabNavRow.cs").Root.ToString();
        Assert.Contains("TabProgramStatusChrome.ProgramStatusGlyph(", row);
        // The claim check lives once, in the chrome both strips share, so
        // the baseline glyph and a richer presentation can never stack.
        var chrome = ShellSource.Load("Tabs.TabProgramStatusChrome.cs").Root.ToString();
        Assert.Contains("!TabModel.ProgramStatusPresentationClaimed", chrome);
    }

    private static int Count(string haystack, string needle) =>
        haystack.Split(needle).Length - 1;
}
