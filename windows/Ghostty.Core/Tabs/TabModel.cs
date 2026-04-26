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
    /// jump-list profile or context-menu duplicate. Null today
    /// because the config layer does not exist; reserved for plan 3.</summary>
    public string? ProfileId { get; set; }

    /// <summary>
    /// Resolved snapshot of the profile this tab was opened with, or
    /// null when opened via the legacy no-profile path (today's cold
    /// start with no <c>profile.*</c> blocks). Set exactly once at tab
    /// creation by <see cref="TabManager.NewTab(ProfileSnapshot?)"/>
    /// <b>before</b> <see cref="TabManager.TabAdded"/> fires; downstream
    /// listeners can read it synchronously.
    ///
    /// PR 6 will replace the once-only setter with a hot-apply path
    /// that calls <see cref="ProfileSnapshotStore.Refresh"/> when
    /// <c>IProfileRegistry.ProfilesChanged</c> fires.
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

    // Title precedence: explicit user override beats anything; then
    // the shell's OSC 0/2 reported title (which knows what the user
    // is actually running, e.g. "vim file.txt"); then the profile's
    // display name (PR 4 - so a tab opened from the new-tab split
    // button reads its profile Name rather than the generic fallback
    // before the shell sends a title); then the hardcoded fallback
    // for the no-profile / pre-OSC-2 cold-start case.
    public string EffectiveTitle =>
        UserOverrideTitle ?? ShellReportedTitle ?? ProfileSnapshot?.DisplayName ?? "Ghostty";

    public TabModel(IPaneHost paneHost)
    {
        PaneHost = paneHost;
    }

    /// <summary>
    /// Disposer assigned by <see cref="TabManager.CreateTab"/> so
    /// <see cref="TabManager.CloseTab"/> can unwire the per-tab
    /// <c>IPaneHost.ProgressChanged</c> handler captured as a local
    /// closure, without maintaining a side dictionary.
    /// </summary>
    internal Action? OnClose { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
