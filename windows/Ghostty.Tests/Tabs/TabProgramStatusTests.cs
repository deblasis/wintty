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

    // The child-exit edge is delivered as the protocol's prompt-start
    // fold (see TerminalControl.RetireEphemeralProgramStatus): both mean
    // "the working and asking states are over".
    private static ProgramStatusEvent RetireOnExit => PromptStart;

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
    public void APromptStart_RetiresWorkingAndBlocked_Only()
    {
        // The spec's rule, and the headers' guidance: a new shell prompt
        // means the program that was working or asking is gone. Done and
        // error are results -- they wait to be seen.
        Assert.Equal(TabProgramStatus.None,
            TabProgramStatusRules.Apply(TabProgramStatus.Working, PromptStart, paneActive: false));
        Assert.Equal(TabProgramStatus.None,
            TabProgramStatusRules.Apply(TabProgramStatus.Blocked, PromptStart, paneActive: false));
        Assert.Equal(TabProgramStatus.Done,
            TabProgramStatusRules.Apply(TabProgramStatus.Done, PromptStart, paneActive: false));
        Assert.Equal(TabProgramStatus.Error,
            TabProgramStatusRules.Apply(TabProgramStatus.Error, PromptStart, paneActive: false));
        Assert.Equal(TabProgramStatus.None,
            TabProgramStatusRules.Apply(TabProgramStatus.None, PromptStart, paneActive: false));
    }

    [Fact]
    public void AChildExit_RetiresWorkingAndBlocked_Only()
    {
        // The same fold, from the child-exit edge: a done the program
        // reported right before exiting is news the pane still owes the
        // user, so it survives the exit and retires on viewing.
        Assert.Equal(TabProgramStatus.None,
            TabProgramStatusRules.Apply(TabProgramStatus.Blocked,
                RetireOnExit, paneActive: false));
        Assert.Equal(TabProgramStatus.Done,
            TabProgramStatusRules.Apply(TabProgramStatus.Done,
                RetireOnExit, paneActive: false));
        Assert.Equal(TabProgramStatus.Error,
            TabProgramStatusRules.Apply(TabProgramStatus.Error,
                RetireOnExit, paneActive: false));
    }

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
