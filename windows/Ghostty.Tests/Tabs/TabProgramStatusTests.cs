using System;
using Ghostty.Core.Interop;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The tab-level program status rules (OSC 7501's minimal consumer):
/// which report states a tab may show, that the worst state wins across
/// panes, that a plain shell never contributes anything, and that a done
/// stops counting once its pane was looked at.
/// </summary>
public class TabProgramStatusTests
{
    private static ProgramStatusEvent Report(ProgramStatusState state) =>
        new(ProgramStatusEventKind.Report, new ProgramStatusReport(
            state, ProgramStatusKind.None, -1, "id", "app", "", ""));

    private static ProgramStatusEvent Reset => new(ProgramStatusEventKind.Reset, default);

    private static ProgramStatusEvent PromptStart => new(ProgramStatusEventKind.PromptStart, default);

    // ── what one report's state contributes ──────────────────────────

    [Theory]
    [InlineData(ProgramStatusState.Working, TabProgramStatus.Working)]
    [InlineData(ProgramStatusState.Blocked, TabProgramStatus.Blocked)]
    [InlineData(ProgramStatusState.Error, TabProgramStatus.Error)]
    [InlineData(ProgramStatusState.Done, TabProgramStatus.Done)]
    public void AReportedState_ContributesItself(ProgramStatusState state, TabProgramStatus expected)
        => Assert.Equal(expected, TabProgramStatusRules.From(state));

    [Theory]
    [InlineData(ProgramStatusState.Idle)]
    [InlineData(ProgramStatusState.Clear)]
    public void IdleAndClear_EarnNothing(ProgramStatusState state)
        => Assert.Equal(TabProgramStatus.None, TabProgramStatusRules.From(state));

    // ── folding actions into a pane's contribution ───────────────────

    [Fact]
    public void AReport_ReplacesThePanesContribution()
    {
        var folded = TabProgramStatusRules.Apply(
            TabProgramStatus.None, Report(ProgramStatusState.Working), paneActive: false);
        Assert.Equal(TabProgramStatus.Working, folded);
        var replaced = TabProgramStatusRules.Apply(
            folded, Report(ProgramStatusState.Done), paneActive: false);
        Assert.Equal(TabProgramStatus.Done, replaced);
    }

    [Fact]
    public void AReset_ClearsWhateverThePaneHad()
        => Assert.Equal(TabProgramStatus.None,
            TabProgramStatusRules.Apply(TabProgramStatus.Blocked, Reset, paneActive: false));

    [Fact]
    public void APromptStart_ChangesNothing()
        => Assert.Equal(TabProgramStatus.Working,
            TabProgramStatusRules.Apply(TabProgramStatus.Working, PromptStart, paneActive: false));

    [Fact]
    public void ADoneOnThePaneBeingLookedAt_NeverShows()
    {
        // The user is watching the pane: the done was seen the moment it
        // landed, and a tab headline for something already seen is noise.
        Assert.Equal(TabProgramStatus.None,
            TabProgramStatusRules.Apply(TabProgramStatus.None, Report(ProgramStatusState.Done), paneActive: true));
        // The same report on a background pane is news.
        Assert.Equal(TabProgramStatus.Done,
            TabProgramStatusRules.Apply(TabProgramStatus.None, Report(ProgramStatusState.Done), paneActive: false));
    }

    [Fact]
    public void ViewingThePane_RetiresADone_OnlyADone()
    {
        Assert.Equal(TabProgramStatus.None, TabProgramStatusRules.Viewed(TabProgramStatus.Done));
        Assert.Equal(TabProgramStatus.Working, TabProgramStatusRules.Viewed(TabProgramStatus.Working));
        Assert.Equal(TabProgramStatus.Blocked, TabProgramStatusRules.Viewed(TabProgramStatus.Blocked));
    }

    // ── the worst across panes ───────────────────────────────────────

    [Theory]
    [InlineData(TabProgramStatus.Done, TabProgramStatus.Working, TabProgramStatus.Working)]
    [InlineData(TabProgramStatus.Working, TabProgramStatus.Error, TabProgramStatus.Error)]
    [InlineData(TabProgramStatus.Error, TabProgramStatus.Blocked, TabProgramStatus.Blocked)]
    [InlineData(TabProgramStatus.Done, TabProgramStatus.Blocked, TabProgramStatus.Blocked)]
    [InlineData(TabProgramStatus.Working, TabProgramStatus.Done, TabProgramStatus.Working)]
    public void TheWorseStateWins(TabProgramStatus a, TabProgramStatus b, TabProgramStatus expected)
    {
        Assert.Equal(expected, TabProgramStatusRules.Worst(a, b));
        Assert.Equal(expected, TabProgramStatusRules.Worst(b, a));
    }

    [Fact]
    public void APlainPane_ContributesNothingBesideARealState()
    {
        // A pane that never reported starts at None and stays there: no
        // glyph for a plain pwsh shell, alone or beside a reporting pane.
        var plain = TabProgramStatusRules.Apply(TabProgramStatus.None, PromptStart, paneActive: false);
        Assert.Equal(TabProgramStatus.None, plain);
        Assert.Equal(TabProgramStatus.Working,
            TabProgramStatusRules.Worst(plain, TabProgramStatusRules.From(ProgramStatusState.Working)));
    }
}
