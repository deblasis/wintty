using System;
using Ghostty.Core.Sponsor.Update;
using Ghostty.Mvvm;
using Microsoft.UI.Dispatching;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// ViewModel for <c>UpdatePill</c>. Subscribes to
/// <see cref="UpdateService.StateChanged"/>, marshals to the UI thread,
/// and re-projects via <see cref="PillDisplayModel.MapFromState"/>.
/// </summary>
internal sealed class UpdatePillViewModel : NotifyBase, IDisposable
{
    // "No Updates Found" is a positive acknowledgement, not a call to
    // action. Upstream Ghostty (macOS) auto-dismisses after 3s; we match.
    private static readonly TimeSpan NoUpdatesAutoDismissDelay = TimeSpan.FromSeconds(3);

    private readonly UpdateService _service;
    private readonly DispatcherQueue _dispatcher;
    private readonly UpdateSkipList _skipList;
    private UpdateStateSnapshot _last = UpdateStateSnapshot.Idle();
    private DispatcherQueueTimer? _autoDismissTimer;

    public UpdatePillViewModel(UpdateService service, DispatcherQueue dispatcher, UpdateSkipList skipList)
    {
        _service = service;
        _dispatcher = dispatcher;
        _skipList = skipList;
        TogglePopoverCommand = new RelayCommand(() => IsPopoverOpen = !IsPopoverOpen);
        _service.StateChanged += OnStateChanged;
        _skipList.Changed += OnSkipListChanged;
        Project(_service.Current);
    }

    public bool IsVisible
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    public string Label
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    } = string.Empty;

    public string IconGlyph
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    } = string.Empty;

    public string ThemeBrushKey
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    } = PillDisplayModel.BrushNeutral;

    public bool ShowProgressRing
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    public double ProgressValue
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    public bool IsIndeterminate
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    /// <summary>Popover open flag, toggled by the pill click command.</summary>
    public bool IsPopoverOpen
    {
        get;
        set { if (field == value) return; field = value; Raise(); }
    }

    public RelayCommand TogglePopoverCommand { get; }

    private void OnStateChanged(object? sender, UpdateStateSnapshot snap)
    {
        _dispatcher.TryEnqueue(() => Project(snap));
    }

    private void OnSkipListChanged(object? sender, EventArgs e)
    {
        // Re-evaluate visibility against the last snapshot so Skip takes
        // effect immediately. The popover's Flyout dismisses automatically
        // once the pill button goes Collapsed.
        _dispatcher.TryEnqueue(() => Project(_last));
    }

    private void Project(UpdateStateSnapshot snap)
    {
        _last = snap;
        var d = PillDisplayModel.MapFromState(snap);
        var skipped = snap.State == UpdateState.UpdateAvailable
            && !string.IsNullOrEmpty(snap.TargetVersion)
            && _skipList.IsSkipped(snap.TargetVersion);
        IsVisible = d.IsVisible && !skipped;
        if (skipped) IsPopoverOpen = false;
        Label = d.Label;
        IconGlyph = d.IconGlyph;
        ThemeBrushKey = d.ThemeBrushKey;
        ShowProgressRing = d.ShowProgressRing;
        ProgressValue = d.ProgressValue;
        IsIndeterminate = d.IsIndeterminate;

        ScheduleAutoDismissIfNeeded(snap.State);
    }

    private void ScheduleAutoDismissIfNeeded(UpdateState state)
    {
        _autoDismissTimer?.Stop();
        _autoDismissTimer = null;

        if (state != UpdateState.NoUpdatesFound) return;

        _autoDismissTimer = _dispatcher.CreateTimer();
        _autoDismissTimer.Interval = NoUpdatesAutoDismissDelay;
        _autoDismissTimer.IsRepeating = false;
        _autoDismissTimer.Tick += (_, _) =>
        {
            // Only hide if the state hasn't moved on in the meantime.
            if (_last.State == UpdateState.NoUpdatesFound)
            {
                IsVisible = false;
                IsPopoverOpen = false;
            }
        };
        _autoDismissTimer.Start();
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
        _skipList.Changed -= OnSkipListChanged;
        _autoDismissTimer?.Stop();
        _autoDismissTimer = null;
    }
}
