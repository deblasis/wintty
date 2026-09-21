using System;
using System.ComponentModel;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Core.Session;

namespace Ghostty.Core.Tabs;

/// <summary>
/// One tab's worth of state. Pure C# (no WinUI types) so the test
/// project can reference it directly via the Ghostty.Core ProjectReference.
///
/// INPC is hand-rolled with the C# 14 <c>field</c> keyword: no source
/// generator dependency, no per-property backing field declarations.
///
/// EffectiveTitle is a computed property; the model raises
/// PropertyChanged for both UserOverrideTitle and ShellReportedTitle so
/// downstream listeners can re-read it.
/// </summary>
internal sealed class TabModel : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public IPaneHost PaneHost { get; }

    /// <summary>Profile id of the profile this tab runs, populated when
    /// the snapshot is attached (and by the session-restore rebuild, which
    /// carries the saved id). The reopen-closed-tab, duplicate-tab and
    /// session-save captures read it so the rebuild can re-resolve the
    /// tab's profile (its icon, its title, its icon-tracking opt-out);
    /// null only on the legacy no-profile path, which has nothing to
    /// re-resolve.</summary>
    public string? ProfileId { get; set; }

    /// <summary>
    /// Resolved snapshot of the profile this tab was opened with, or
    /// null when opened via the legacy no-profile path (today's cold
    /// start with no <c>profile.*</c> blocks). Set exactly once at tab
    /// creation by <see cref="TabManager.NewTab(ProfileSnapshot?)"/>
    /// <b>before</b> <see cref="TabManager.TabAdded"/> fires; downstream
    /// listeners can read it synchronously.
    /// </summary>
    public ProfileSnapshot? ProfileSnapshot { get; private set; }

    /// <summary>
    /// One-time setter used by <see cref="TabManager.NewTab(ProfileSnapshot?)"/>.
    /// Throws if invoked twice -- guards the V1 contract that the
    /// property is effectively init-only.
    /// </summary>
    internal void AttachProfileSnapshot(ProfileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (ProfileSnapshot is not null)
            throw new InvalidOperationException(
                "TabModel.ProfileSnapshot is set exactly once for V1; " +
                "PR 6 introduces a hot-apply path that replaces this guard.");
        ProfileSnapshot = snapshot;

        // The snapshot is the tab's own record of the profile it runs, and
        // live-created tabs (NewTab, the window's seed) set nothing else:
        // without the id here, every capture saves a tab the rebuild
        // cannot re-resolve, so a reopened or restored tab comes back
        // without its profile. A restore rebuild sets the saved id first,
        // and an explicitly kept id wins over the snapshot's either way.
        if (!string.IsNullOrEmpty(snapshot.ProfileId))
            ProfileId ??= snapshot.ProfileId;

        // Keep the cached TabIcon's subscribers attached across snapshot
        // attaches; cheaper than tearing down and rebuilding the VM, and
        // future-proofs the hot-apply path that lands when this guard
        // becomes a no-op.
        _tabIcon?.SetIcon(snapshot.Icon, TabLabel.IconTooltip(snapshot));

        // The profile's display name is a tier of the label, so attaching
        // one moves the label and everything read off it. Raising was
        // missed here because the only caller attaches before the tab is
        // added, let alone activated, so nothing was listening yet -- but
        // "nobody is listening at the one call site we have" is not the
        // same claim as "this does not change the label", and the window
        // caption is written once per notification and never re-read.
        RaiseTitleDerived();
    }

    private TabIconViewModel? _tabIcon;

    /// <summary>
    /// Per-tab icon view-model rendered by the tab strip. Lazily
    /// constructed from <see cref="ProfileSnapshot"/> so tabs opened
    /// via the legacy no-profile path still get a default icon
    /// without consulting the resolver up front.
    ///
    /// Thread-safety: the getter is UI-thread-only. Lazy initialization
    /// is not synchronized; concurrent access from non-UI threads could
    /// construct two VMs and lose one. Callers from background threads
    /// must marshal via the DispatcherQueue first.
    /// </summary>
    public TabIconViewModel TabIcon
    {
        get
        {
            if (_tabIcon is null)
            {
                _tabIcon = ProfileSnapshot is { } snap
                    ? new TabIconViewModel(snap.Icon, TabLabel.IconTooltip(snap))
                    : new TabIconViewModel(new IconSpec.BundledKey("default"), TabLabel.UnnamedTab);
                _tabIcon.SetSettling(IsSettling);
            }
            return _tabIcon;
        }
    }

    public string? UserOverrideTitle
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise(Args.UserOverrideTitle);
            // EffectiveTitle and the tooltips are computed; classic bindings
            // listen for the exact property name, so raise them explicitly.
            RaiseTitleDerived();
        }
    }

    public string? ShellReportedTitle
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise(Args.ShellReportedTitle);
            RaiseTitleDerived();
            if (value is not null) Settle();
        }
    }

    private void RaiseTitleDerived()
    {
        Raise(Args.EffectiveTitle);
        Raise(Args.IsHome);
        Raise(Args.WordTitle);
        Raise(Args.TooltipText);
        Raise(Args.HoverText);
    }

    /// <summary>
    /// What a person calls the process this tab was launched into, or null
    /// until the pane has said. The pane's own child: the shell (or
    /// command) the surface spawned, not whatever is in the foreground now
    /// -- that is <see cref="OnActiveProcessChanged"/>, which moves the
    /// icon and leaves the label alone.
    ///
    /// It only names a tab that has nothing better: a tab opened from a
    /// profile is named by the profile, and any tab whose shell has spoken
    /// is named by what it said. This is the answer a tab with neither
    /// used to lack.
    ///
    /// SCOPE, and it is narrower than the tiers above it. Those follow the
    /// tab's ACTIVE pane; this one names the first pane to report a pid and
    /// never follows focus.
    ///
    /// They can already disagree. A pane split from the profile menu
    /// carries its own snapshot (<see cref="IPaneHost.Split"/> takes one,
    /// and <c>MainWindow</c>'s new-pane target passes it), so a tab with
    /// no profile can hold a pane running something else entirely, and
    /// focusing it does not move this tier. What keeps the window narrow
    /// is not that the case is unreachable but that it is short-lived:
    /// the moment that pane reports a directory or a title, two tiers that
    /// DO follow focus outrank this one. Closing it properly means a
    /// per-pane launch fact rather than a per-tab one, which is a change
    /// to the seam and not to this chain.
    /// </summary>
    public string? PaneLaunchName { get; private set; }

    /// <summary>
    /// Whether <see cref="PaneLaunchName"/> is the answer a complete read
    /// of the launch process gives, rather than the lesser one half a read
    /// gives. False while no name has been reported at all.
    ///
    /// The two facts behind the name are read from a live process and fail
    /// independently: an image path still answers while the command line
    /// is refused mid-exit. A name computed without the command line can
    /// be strictly poorer than the same process would have given a moment
    /// earlier -- "WSL" where "WSL: Ubuntu-24.04" was there to be had --
    /// so a caller that can ask again is told whether it is worth it.
    /// </summary>
    public bool PaneLaunchNameIsComplete { get; private set; }

    /// <summary>
    /// Told what this tab's pane spawned: the executable's basename and
    /// its raw command line, the same pair the active-process tracker
    /// delivers. The naming happens here rather than at the call site so
    /// the rule stays in the model and moves with it.
    ///
    /// SEAM, stated honestly because it is half portable and half not.
    /// This method is portable: it takes two strings and applies the rule,
    /// and a daemon that owns tab display names can call it unchanged.
    /// What is NOT portable is the supply. The pane reports a pid, and the
    /// shell layer asks the LOCAL OS about that pid, so a pid that crosses
    /// a machine or user boundary answers nothing and every tab with no
    /// profile quietly reads the generic word. Moving this seam means the
    /// pane reporting the pair rather than the pid; until then, a remote
    /// pane degrades silently and this comment is the only warning.
    ///
    /// The first COMPLETE report wins. What launched the pane happened
    /// once, and a later resolution must not rename a tab the user has
    /// been reading. A report that lost half its inputs still names the
    /// tab, because a poorer name beats the generic one, but it does not
    /// close the question: further reports may replace it until one is
    /// complete. Unbounded in principle, bounded to one in practice,
    /// because <see cref="ShellPid"/> is written once per tab and only its
    /// writers call this. Nothing to report at all (an exited process, a
    /// denied handle) leaves the tab as it was.
    ///
    /// The basename crosses from another process's image path, so it is
    /// held to <see cref="TabLabel.IsPlain"/> like every other string the
    /// tab renders from outside itself: a name carrying a line break or a
    /// bidi override would write a second line into the tooltip.
    ///
    /// Deliberately does NOT settle the tab. Learning what was spawned is
    /// not the pane speaking; the strips keep dimming the label until the
    /// surface paints or the shell says something.
    /// </summary>
    /// <remarks>
    /// UI thread only, like <see cref="OnActiveProcessChanged"/>: it
    /// raises PropertyChanged that WinUI bindings consume, and the latch
    /// is a plain read-then-write with no lock. Both callers reach it from
    /// the thread that writes <see cref="ShellPid"/>, which is the UI
    /// thread.
    /// </remarks>
    public void OnPaneLaunched(string? exeBasename, string? commandLine)
    {
        if (PaneLaunchNameIsComplete) return;
        if (string.IsNullOrWhiteSpace(exeBasename) || !TabLabel.IsPlain(exeBasename)) return;

        var name = ProcessDisplayName.For(exeBasename, commandLine);
        if (string.IsNullOrWhiteSpace(name) || !TabLabel.IsPlain(name)) return;

        // A command line that is present but says nothing is not an
        // answer, and it must not close the question. Whitespace rather
        // than null, like every other string test in this method: the
        // resolver returns null for an unreadable command line today, but
        // this is public, and a caller handing over "" would otherwise
        // latch the poorer name for good -- "WSL" where the distro was
        // there to be read. That mismatch between a null check and a
        // whitespace check is the same one that stranded a blank-named
        // profile on the generic word.
        var complete = !string.IsNullOrWhiteSpace(commandLine);

        // A repeat of the name already shown is not a change; raising for
        // it would retitle the window and re-announce the tab for nothing.
        if (name == PaneLaunchName)
        {
            PaneLaunchNameIsComplete = complete;
            return;
        }

        PaneLaunchName = name;
        PaneLaunchNameIsComplete = complete;
        Raise(Args.PaneLaunchName);
        RaiseTitleDerived();
    }

    /// <summary>
    /// The directory the active pane's shell last reported (OSC 7 / OSC 9;9),
    /// pushed by <see cref="TabManager"/> from
    /// <c>IPaneHost.CwdChanged</c>. Null until a shell reports one -- a
    /// profile without shell integration never will.
    /// </summary>
    public string? ShellReportedCwd
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise(Args.ShellReportedCwd);
            RaiseTitleDerived();
            if (value is not null) Settle();
        }
    }

    /// <summary>
    /// The directory that reads as <c>~</c> on this tab's surfaces, handed
    /// down by <see cref="TabManager"/> from the user's profile. Null
    /// collapses nothing. Only the label and the tooltip read it: the
    /// directory a tab respawns in is <see cref="ShellReportedCwd"/>,
    /// untouched.
    /// </summary>
    public string? HomeDirectory
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise(Args.HomeDirectory);
            RaiseTitleDerived();
        }
    }

    public TabProgressState Progress
    {
        get;
        set { if (!field.Equals(value)) { field = value; Raise(Args.Progress); } }
    } = TabProgressState.None;

    /// <summary>
    /// Preset tint applied to this tab's header. In-memory only;
    /// resets to <see cref="TabColor.None"/> on app restart. True
    /// cross-session persistence needs a durable tab id and a
    /// startup restore hook (out of scope, tracked as a followup).
    /// </summary>
    public TabColor Color
    {
        get;
        set { if (field != value) { field = value; Raise(Args.Color); } }
    } = TabColor.None;

    /// <summary>
    /// True while this tab has an unacknowledged bell. Set by the bell
    /// handler when <c>bell-features</c> includes <c>title</c>; cleared
    /// when the tab's active surface gains focus or takes a keystroke.
    /// The tab strip renders a bell glyph while true. In-memory only.
    /// </summary>
    public bool BellRinging
    {
        get;
        set { if (field != value) { field = value; Raise(Args.BellRinging); } }
    }

    /// <summary>
    /// True when nothing has touched this tab for the idle window (see
    /// <see cref="TabIdleTracker"/>): the strip dims the row and shows a
    /// moon glyph. Written by the tracker's sweep (and its eager clear on
    /// activation) -- one writer, so the value is always the computed
    /// truth, never a stale toggle. Never true for the active tab or a
    /// tab with an unacknowledged bell. In-memory only.
    /// </summary>
    public bool IsIdle
    {
        get;
        set { if (field != value) { field = value; Raise(Args.IsIdle); } }
    }

    /// <summary>
    /// Last time anything touched this tab, in
    /// <see cref="Environment.TickCount64"/> milliseconds: activation
    /// stamps here on the model itself, while keystrokes and pane output
    /// land on the surfaces and read back through
    /// <see cref="IPaneHost.LastActivityTick"/>. Zero until the first
    /// stamp. Deliberately not INPC -- it is an input to the idle
    /// tracker's sweep, not a rendered fact; <see cref="IsIdle"/> is the
    /// rendered fact.
    /// </summary>
    internal long LastActivityTick { get; set; }

    /// <summary>
    /// True while the tab sits in the pinned prefix. Set through
    /// <see cref="TabManager.SetPinned"/>, which relocates the tab to the
    /// zone boundary; writing it directly leaves the order to be repaired
    /// by the manager's next mutation.
    /// </summary>
    public bool IsPinned
    {
        get;
        set { if (field != value) { field = value; Raise(Args.IsPinned); } }
    }

    /// <summary>
    /// The tab's group, or null when ungrouped. Owned by
    /// <see cref="TabManager"/>: its mutators keep members contiguous and
    /// keep groups out of the pinned prefix, so a direct write is a state
    /// change the manager repairs on its next mutation, not an order
    /// change in itself.
    /// </summary>
    public TabGroup? Group
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Raise(Args.Group);
        }
    }

    // The rule: a tab is named by its active pane's title. Until the pane
    // reports one, it is named by what launched the tab -- the profile's
    // display name when there is one, otherwise the shell or command
    // actually running. The application's own name is not the FALLBACK.
    //
    // "Not the fallback", precisely, and not "never appears": a pane that
    // really runs this application names its tab after it, which is the
    // honest answer and what ProcessDisplayName gives. What is banned is
    // reaching for the product because nothing else was to hand.
    //
    // "The tab", not "the active pane", for the launch tier alone: the
    // three tiers above it follow focus and that one does not. See
    // PaneLaunchName for the scope and for what following focus would
    // take.
    //
    // As precedence: explicit user override beats anything; then the
    // shell's OSC 0/2 reported title (which knows what the user is
    // actually running, e.g. "vim file.txt"), minus the console's default
    // exe-path title, which names the interpreter the icon already shows;
    // then the folder the shell reported it is sitting in, which is what a
    // tab at a prompt is actually about; then the two tiers of "what
    // launched this" -- the profile's display name, else the process the
    // pane spawned (see PaneLaunchName), which is the only answer a tab
    // opened with no profile has; then the generic, for the moment before
    // any of them exists.
    //
    // The bottom tier used to be the product name. That was not a
    // placeholder anyone chose, just the end of the chain, and a cold
    // start with no resolvable default profile reached it: the window's
    // first tab read "Wintty" while every tab from the new-tab button --
    // which always carries a snapshot -- read its profile.
    //
    // Each level is coalesced on whitespace, not just on null. A shell
    // can report a title that is empty or all spaces (`printf
    // '\033]2;\007'` does it), and that arrives here as a non-null empty
    // string, which would otherwise win the precedence chain and leave
    // the tab with a blank label and a blank name.
    public string EffectiveTitle => Compose(NamedTitle, DisplayCwd);

    /// <summary>
    /// Whether the strips draw the home glyph for this tab: the shell is in
    /// the user's own directory AND nothing else has named the tab. Read
    /// from the directory tier rather than from the composed label, because
    /// a label of "~" can also come from a rename or from a shell that
    /// titles itself with its prompt -- and such a tab drawn as a bare
    /// house would be a tab with no name and nothing to hover.
    /// </summary>
    public bool IsHome => NamedTitle is null && TabLabel.IsHome(DisplayCwd ?? "");

    /// <summary>
    /// The label for surfaces that can only print: the window title, the
    /// taskbar, the palette. "Home" where the strips draw the glyph.
    /// </summary>
    public string WordTitle => IsHome ? TabLabel.Word("~") : EffectiveTitle;

    /// <summary>
    /// What a pointer resting on the tab is told: the whole directory (home
    /// written as <c>~</c>), under the rename or shell title when one
    /// outranks it. The label shows the directory's leaf; this is where the
    /// rest of it lives. Never null: the pinned square, which shows no
    /// label, always carries it.
    /// </summary>
    public string TooltipText
    {
        get
        {
            var named = NamedTitle;
            var cwd = DisplayCwd;
            return TabLabel.Tooltip(named, cwd, ShellReportedCwd, Compose(named, cwd));
        }
    }

    /// <summary>
    /// The tooltip for a surface that shows the label: null when the
    /// tooltip would only say the label again, so a pointer on a plain
    /// tab is told nothing rather than the same word twice.
    /// </summary>
    public string? HoverText
    {
        get
        {
            var tooltip = TooltipText;
            return tooltip == EffectiveTitle ? null : tooltip;
        }
    }

    /// <summary>
    /// True from the moment a manager opens the tab until its pane first
    /// paints or its shell first says anything -- a title or a directory.
    /// The strips show the app's own icon and dim the label meanwhile, the
    /// way a loading tab reads in other terminals. Off for a tab built
    /// directly or adopted from another window: those have already lived.
    /// </summary>
    public bool IsSettling
    {
        get;
        private set { if (field != value) { field = value; _tabIcon?.SetSettling(value); Raise(Args.IsSettling); } }
    }

    /// <summary>Marks the tab as starting; the manager calls it on every tab it opens.</summary>
    internal void BeginSettling() => IsSettling = true;

    /// <summary>The pane painted or the shell spoke: the start is over.</summary>
    internal void Settle() => IsSettling = false;

    /// <summary>
    /// The reported directory as something the app may act on -- copy it,
    /// open it -- or null. Reported at all, plain text (no control or bidi
    /// characters a program could smuggle onto the clipboard), and a
    /// directory the spawn policy would accept: the same verdict Duplicate
    /// Tab and restore live by, so the menu cannot reach a host they refuse.
    /// </summary>
    public string? ActionableCwd =>
        ShellReportedCwd is { } cwd && TabLabel.IsPlain(cwd) && SpawnCwdPolicy.MaySpawnAt(cwd)
            ? cwd
            : null;

    private string Compose(string? named, string? displayCwd) =>
        named
        ?? Titled(TabLabel.FolderName(displayCwd))
        ?? Titled(ProfileSnapshot?.DisplayName)
        ?? Titled(PaneLaunchName)
        ?? TabLabel.UnnamedTab;

    // The two tiers above the folder, as one: what the tab is called when
    // someone -- the user or the shell -- has actually named it.
    private string? NamedTitle =>
        Titled(UserOverrideTitle) ?? Titled(TabLabel.Meaningful(ShellReportedTitle));

    private string? DisplayCwd => TabLabel.Collapse(ShellReportedCwd, HomeDirectory);

    private static string? Titled(string? title)
        => string.IsNullOrWhiteSpace(title) ? null : title;

    public TabModel(IPaneHost paneHost)
    {
        PaneHost = paneHost;
    }

    /// <summary>
    /// Called by the active-process tracker when the foreground command
    /// inside the pty changes. Routes through <see cref="ProcessIconTable"/>
    /// to decide whether to install an icon override or revert to the
    /// profile icon. If the mapped spec equals the profile spec (e.g. the
    /// foreground IS the launch shell), the override is cleared -- no
    /// churn at the prompt of the launch shell.
    /// </summary>
    /// <remarks>
    /// Must be called on the UI thread. The method mutates the lazy
    /// TabIcon VM and raises PropertyChanged; off-thread invocation can
    /// tear lazy init or fire change notifications on a non-UI thread.
    /// Callers from background threads (e.g. the active-process tracker
    /// timer) must marshal via the DispatcherQueue first.
    /// </remarks>
    public void OnActiveProcessChanged(string? exeBasename, string? commandLine)
    {
        // If the profile opts out of foreground tracking, any pending override
        // is cleared and no new override is installed. The user pinned an icon
        // intentionally; respect it.
        if (ProfileSnapshot is { TabIconTracksForeground: false })
        {
            _tabIcon?.RevertToProfile();
            return;
        }

        // Touching the TabIcon getter lazily constructs the VM from the
        // profile snapshot before we install or clear any override.
        var vm = TabIcon;

        if (exeBasename is null || ProcessIconTable.TryMap(exeBasename, commandLine) is not { } mapped)
        {
            vm.RevertToProfile();
            return;
        }

        // Suppress when the foreground IS the launch shell: steady state at
        // its prompt produces zero visual change and the tooltip stays the
        // shell's name. Two ways to know: the mapped spec equals the
        // profile's, or the exe is the one the profile's command launches.
        // The second matters because discovered profiles carry a bundled
        // icon while the table maps a brand icon, so the specs differ for
        // the very same pwsh.exe.
        var profileIcon = ProfileSnapshot?.Icon;
        var launchExe = ProfileOrderResolver.CommandBasename(ProfileSnapshot?.ResolvedCommand);
        if ((profileIcon is not null && Equals(mapped, profileIcon))
            || string.Equals(launchExe, exeBasename, StringComparison.OrdinalIgnoreCase))
        {
            vm.RevertToProfile();
            return;
        }

        vm.SetOverride(mapped, TabLabel.ForegroundTooltip(exeBasename, commandLine, ProfileSnapshot));
    }

    /// <summary>
    /// Disposer assigned by <see cref="TabManager.CreateTab"/> so
    /// <see cref="TabManager.CloseTab"/> can unwire the per-tab
    /// <c>IPaneHost.ProgressChanged</c> handler captured as a local
    /// closure, without maintaining a side dictionary.
    /// </summary>
    internal Action? OnClose { get; set; }

    private int? _shellPid;

    /// <summary>
    /// Process id of the shell rooted at this tab's first leaf, or null
    /// before the surface has spawned (or after teardown). Set by the
    /// WinUI layer once <c>ghostty_surface_foreground_pid</c> returns a
    /// non-zero value; raises <see cref="ShellPidChanged"/> so the
    /// process tracker can register / unregister its descendant walk,
    /// and so the shell layer can resolve what the pane was launched
    /// into for <see cref="PaneLaunchName"/>. On Windows the value is
    /// the child the surface spawned, which is what both readers want:
    /// the tracker walks down from it, and the name is taken from it.
    /// </summary>
    public int? ShellPid
    {
        get => _shellPid;
        internal set
        {
            if (_shellPid == value) return;
            _shellPid = value;
            ShellPidChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// Fired when <see cref="ShellPid"/> changes. The active-process
    /// tracker subscribes per tab to register the new pid and unregister
    /// the old one; null means "no pid right now" (pre-spawn or
    /// post-exit).
    /// </summary>
    public event Action<TabModel, int?>? ShellPidChanged;

    /// <summary>
    /// One <see cref="PropertyChangedEventArgs"/> per property name, minted
    /// once for the process. The names are a closed set known at compile
    /// time, and a shell drives the title and directory setters at every
    /// prompt on every tab -- six raises each -- so a fresh args object per
    /// raise was garbage with nothing to say for itself.
    ///
    /// <c>nameof</c> ties each field to the property it names; what it
    /// cannot check is a setter reaching for the wrong field, which
    /// <c>[CallerMemberName]</c> made impossible. TabChangeArgsTests walks
    /// every settable property and holds that line.
    /// </summary>
    private static class Args
    {
        internal static readonly PropertyChangedEventArgs UserOverrideTitle = new(nameof(TabModel.UserOverrideTitle));
        internal static readonly PropertyChangedEventArgs ShellReportedTitle = new(nameof(TabModel.ShellReportedTitle));
        internal static readonly PropertyChangedEventArgs ShellReportedCwd = new(nameof(TabModel.ShellReportedCwd));
        internal static readonly PropertyChangedEventArgs PaneLaunchName = new(nameof(TabModel.PaneLaunchName));
        internal static readonly PropertyChangedEventArgs HomeDirectory = new(nameof(TabModel.HomeDirectory));
        internal static readonly PropertyChangedEventArgs Progress = new(nameof(TabModel.Progress));
        internal static readonly PropertyChangedEventArgs Color = new(nameof(TabModel.Color));
        internal static readonly PropertyChangedEventArgs BellRinging = new(nameof(TabModel.BellRinging));
        internal static readonly PropertyChangedEventArgs IsIdle = new(nameof(TabModel.IsIdle));
        internal static readonly PropertyChangedEventArgs IsPinned = new(nameof(TabModel.IsPinned));
        internal static readonly PropertyChangedEventArgs Group = new(nameof(TabModel.Group));
        internal static readonly PropertyChangedEventArgs IsSettling = new(nameof(TabModel.IsSettling));

        // Computed; raised explicitly by RaiseTitleDerived.
        internal static readonly PropertyChangedEventArgs EffectiveTitle = new(nameof(TabModel.EffectiveTitle));
        internal static readonly PropertyChangedEventArgs IsHome = new(nameof(TabModel.IsHome));
        internal static readonly PropertyChangedEventArgs WordTitle = new(nameof(TabModel.WordTitle));
        internal static readonly PropertyChangedEventArgs TooltipText = new(nameof(TabModel.TooltipText));
        internal static readonly PropertyChangedEventArgs HoverText = new(nameof(TabModel.HoverText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
}
