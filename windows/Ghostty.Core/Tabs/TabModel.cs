using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ghostty.Core.Panes;

namespace Ghostty.Core.Tabs;

/// <summary>
/// One tab's worth of state. Pure C# (no WinUI types) so the test
/// project can reference it directly via the Ghostty.Core ProjectReference.
///
/// INPC is hand-rolled here rather than depending on
/// CommunityToolkit.Mvvm: only two reactive properties, not worth a
/// NuGet dependency.
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

    private string? _userOverrideTitle;
    public string? UserOverrideTitle
    {
        get => _userOverrideTitle;
        set
        {
            if (_userOverrideTitle == value) return;
            _userOverrideTitle = value;
            Raise();
            // EffectiveTitle is computed; classic bindings listen for
            // the exact property name, so raise it explicitly.
            Raise(nameof(EffectiveTitle));
        }
    }

    private string? _shellReportedTitle;
    public string? ShellReportedTitle
    {
        get => _shellReportedTitle;
        set
        {
            if (_shellReportedTitle == value) return;
            _shellReportedTitle = value;
            Raise();
            Raise(nameof(EffectiveTitle));
        }
    }

    private TabProgressState _progress = TabProgressState.None;
    public TabProgressState Progress
    {
        get => _progress;
        set { if (!_progress.Equals(value)) { _progress = value; Raise(); } }
    }

    public string EffectiveTitle =>
        UserOverrideTitle ?? ShellReportedTitle ?? "Ghostty";

    public TabModel(IPaneHost paneHost)
    {
        PaneHost = paneHost;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
