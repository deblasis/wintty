using Ghostty.Core.Interop;

namespace Ghostty.Core.Tabs;

/// <summary>
/// What a tab says about the programs running in its panes, from their
/// OSC 7501 reports: the worst state across the panes that reported one.
/// The order is the enum's own: blocked asks for input now, then error,
/// then working, then a done nobody has looked at yet. A pane that has
/// never reported anything contributes nothing, in any state.
/// </summary>
public enum TabProgramStatus
{
    None = 0,
    Done = 1,
    Working = 2,
    Error = 3,
    Blocked = 4,
}

/// <summary>
/// The tab-level rules for program status: how one report's state maps to
/// what the tab may show, how the worst state wins across panes, and when
/// a done stops counting because its pane was looked at.
/// </summary>
public static class TabProgramStatusRules
{
    /// <summary>
    /// What one report's state contributes. Idle and clear earn nothing:
    /// idle is a rest state, and a clear takes the record away.
    /// </summary>
    public static TabProgramStatus From(ProgramStatusState state) => state switch
    {
        ProgramStatusState.Blocked => TabProgramStatus.Blocked,
        ProgramStatusState.Error => TabProgramStatus.Error,
        ProgramStatusState.Working => TabProgramStatus.Working,
        ProgramStatusState.Done => TabProgramStatus.Done,
        _ => TabProgramStatus.None,
    };

    /// <summary>
    /// Fold one action into a pane's current contribution. A reset clears
    /// it; a report replaces it with the report's state; a prompt start
    /// changes nothing. A done that lands on the pane the user is looking
    /// at was seen the moment it arrived, so it never shows.
    /// </summary>
    public static TabProgramStatus Apply(
        TabProgramStatus current, ProgramStatusEvent e, bool paneActive)
    {
        var reported = e.Kind switch
        {
            ProgramStatusEventKind.Reset => TabProgramStatus.None,
            ProgramStatusEventKind.Report => From(e.Report.State),
            _ => current,
        };
        return reported == TabProgramStatus.Done && paneActive
            ? TabProgramStatus.None
            : reported;
    }

    /// <summary>
    /// What a done contributes once its pane takes focus: nothing. The
    /// user has seen it; only a fresh report brings the state back.
    /// </summary>
    public static TabProgramStatus Viewed(TabProgramStatus status) =>
        status == TabProgramStatus.Done ? TabProgramStatus.None : status;

    /// <summary>
    /// The worst of two panes' contributions. The enum's own order is the
    /// priority, so this is the larger of the two.
    /// </summary>
    public static TabProgramStatus Worst(TabProgramStatus a, TabProgramStatus b) =>
        a > b ? a : b;
}
