using System;
using Ghostty.Core.Sponsor.Update;
using Ghostty.Mvvm;
using Microsoft.UI.Dispatching;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// ViewModel for the update popover. Exposes one command per button
/// with state-aware enablement. CanExecute is re-queried on every
/// state transition so disabled buttons correctly gray out.
/// </summary>
internal sealed class UpdatePopoverViewModel : NotifyBase, IDisposable
{
    private readonly UpdateService _service;
    private readonly UpdateSkipList _skipList;
    private readonly DispatcherQueue _dispatcher;

    public UpdatePopoverViewModel(
        UpdateService service,
        UpdateSkipList skipList,
        DispatcherQueue dispatcher)
    {
        _service = service;
        _skipList = skipList;
        _dispatcher = dispatcher;

        // Every command dismisses the popover via CloseRequested before
        // doing its work. The pill control translates the event into a
        // Flyout.Hide() call, so clicks feel responsive even while the
        // underlying driver operation is still running.
        SkipCommand = new RelayCommand(() => { RaiseClose(); Skip(); }, CanSkip);
        InstallAndRelaunchCommand = new AsyncRelayCommand(async () =>
        {
            RaiseClose();
            await _service.DownloadAsync();
        }, CanInstallNow);
        RestartNowCommand = new AsyncRelayCommand(async () =>
        {
            RaiseClose();
            await _service.ApplyAndRestartAsync();
        }, CanRestart);
        RetryCommand = new AsyncRelayCommand(async () =>
        {
            RaiseClose();
            await _service.CheckNowAsync();
        }, CanRetry);
        CancelDownloadCommand = new RelayCommand(() =>
        {
            RaiseClose();
            _ = _service.CancelDownloadAsync();
        });

        var c = _service.Current;
        State = c.State;
        TargetVersion = c.TargetVersion;
        ErrorMessage = c.ErrorMessage;
        ReleaseNotesUrl = c.ReleaseNotesUrl;
        TechnicalDetail = c.TechnicalDetail;

        _service.StateChanged += OnStateChanged;
    }

    public UpdateState State
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    public string? TargetVersion
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    public string? ErrorMessage
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    public string? ReleaseNotesUrl
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    /// <summary>
    /// Driver-supplied technical detail (exception type, HTTP status,
    /// simulator label). Popover shows it in a Debug-only second line
    /// so it's available for bug reports without cluttering release UX.
    /// </summary>
    public string? TechnicalDetail
    {
        get;
        private set { if (field == value) return; field = value; Raise(); }
    }

    // Regular computed property; raised manually in OnStateChanged because
    // the C# 14 field-keyword pattern doesn't apply to derived properties.
    public bool ShowCancel => State == UpdateState.Downloading;

    public RelayCommand SkipCommand { get; }
    public AsyncRelayCommand InstallAndRelaunchCommand { get; }
    public AsyncRelayCommand RestartNowCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }

    /// <summary>
    /// Raised when the popover should be dismissed. The pill control
    /// listens and calls <c>Flyout.Hide()</c>; the VM stays decoupled
    /// from the Flyout instance.
    /// </summary>
    public event EventHandler? CloseRequested;

    private void RaiseClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Close the popover and tell the driver to clear its current state.
    /// Used by the Dismiss button (Error state) so the user can stop
    /// seeing a stuck Error without waiting for the next check cycle.
    /// </summary>
    public void DismissRequested()
    {
        RaiseClose();
        _ = _service.DismissAsync();
    }

    /// <summary>Close the popover without doing anything else. Used by
    /// code-behind before showing a ContentDialog so the Flyout doesn't
    /// fight the dialog's modal layer.</summary>
    public void RequestClose() => RaiseClose();

    private void Skip()
    {
        if (!string.IsNullOrEmpty(TargetVersion))
        {
            _skipList.Skip(TargetVersion);
        }
    }

    private bool CanSkip() =>
        State == UpdateState.UpdateAvailable && !string.IsNullOrEmpty(TargetVersion);

    private bool CanInstallNow() => State == UpdateState.UpdateAvailable;
    private bool CanRestart() => State == UpdateState.RestartPending;
    private bool CanRetry() => State == UpdateState.Error;

    private void OnStateChanged(object? sender, UpdateStateSnapshot snap)
    {
        _dispatcher.TryEnqueue(() =>
        {
            State = snap.State;
            TargetVersion = snap.TargetVersion;
            ErrorMessage = snap.ErrorMessage;
            ReleaseNotesUrl = snap.ReleaseNotesUrl;
            TechnicalDetail = snap.TechnicalDetail;
            Raise(nameof(ShowCancel));
            SkipCommand.RaiseCanExecuteChanged();
            InstallAndRelaunchCommand.RaiseCanExecuteChanged();
            RestartNowCommand.RaiseCanExecuteChanged();
            RetryCommand.RaiseCanExecuteChanged();
        });
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
    }
}
