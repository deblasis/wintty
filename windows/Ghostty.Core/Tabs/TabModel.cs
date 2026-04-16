using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ghostty.Core.Panes;

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

    public string EffectiveTitle =>
        UserOverrideTitle ?? ShellReportedTitle ?? "Ghostty";

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
