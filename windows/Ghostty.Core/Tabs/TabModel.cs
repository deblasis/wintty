using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;

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

    /// <summary>Profile id, set when this tab was created from a
    /// jump-list profile or context-menu duplicate.</summary>
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

        // Keep the cached TabIcon's subscribers attached across snapshot
        // attaches; cheaper than tearing down and rebuilding the VM, and
        // future-proofs the hot-apply path that lands when this guard
        // becomes a no-op.
        _tabIcon?.SetIcon(snapshot.Icon, snapshot.DisplayName);
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
                    ? new TabIconViewModel(snap.Icon, snap.DisplayName)
                    : new TabIconViewModel(new IconSpec.BundledKey("default"), "Terminal");
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
            Raise();
            // EffectiveTitle is computed; classic bindings listen for
            // the exact property name, so raise it explicitly.
            Raise(nameof(EffectiveTitle));
        }
    }

    public string? ShellReportedTitle
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise();
            Raise(nameof(EffectiveTitle));
        }
    }

    public TabProgressState Progress
    {
        get;
        set { if (!field.Equals(value)) { field = value; Raise(); } }
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
        set { if (field != value) { field = value; Raise(); } }
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
        set { if (field != value) { field = value; Raise(); } }
    }

    // Title precedence: explicit user override beats anything; then
    // the shell's OSC 0/2 reported title (which knows what the user
    // is actually running, e.g. "vim file.txt"); then the profile's
    // display name (so a tab opened from the new-tab split
    // button reads its profile Name rather than the generic fallback
    // before the shell sends a title); then the product name for the
    // no-profile / pre-OSC-2 cold-start case.
    //
    // Each level is coalesced on whitespace, not just on null. A shell
    // can report a title that is empty or all spaces (`printf
    // '\033]2;\007'` does it), and that arrives here as a non-null empty
    // string, which would otherwise win the precedence chain and leave
    // the tab with a blank label and a blank name.
    public string EffectiveTitle =>
        Titled(UserOverrideTitle)
        ?? Titled(ShellReportedTitle)
        ?? Titled(ProfileSnapshot?.DisplayName)
        ?? AppIdentity.ProductName;

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

        var mapped = exeBasename is null
            ? null
            : ProcessIconTable.TryMap(exeBasename, commandLine);

        // Touching the TabIcon getter lazily constructs the VM from the
        // profile snapshot before we install or clear any override.
        var vm = TabIcon;

        if (mapped is null)
        {
            vm.RevertToProfile();
            return;
        }

        // Suppress when the mapped spec equals the profile spec: steady
        // state at the launch shell's prompt produces zero visual change
        // and the tooltip stays as the profile's display name.
        var profileIcon = ProfileSnapshot?.Icon;
        if (profileIcon is not null && Equals(mapped, profileIcon))
        {
            vm.RevertToProfile();
            return;
        }

        // Tooltip is the exe basename without extension: matches how the
        // user thinks about the running command ("vim", not "vim.exe").
        var tooltip = System.IO.Path.GetFileNameWithoutExtension(exeBasename) ?? string.Empty;
        vm.SetOverride(mapped, tooltip);
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
    /// process tracker can register / unregister its descendant walk.
    /// Today's Windows pty layer returns 0 for the foreground pid, so
    /// this stays null on Windows until libghostty exposes the spawned
    /// child pid; the lifecycle wiring here is the same shape we'll need
    /// once that lands.
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
