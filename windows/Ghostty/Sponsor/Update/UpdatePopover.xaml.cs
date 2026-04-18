using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Ghostty.Core.Sponsor.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Flyout content for the update pill. Uses code-behind binding
/// (same pattern as <c>CommandPaletteControl</c>): an internal VM is
/// wired via <see cref="Bind"/>, which subscribes to PropertyChanged
/// and refreshes element properties on every transition.
/// </summary>
internal sealed partial class UpdatePopover : UserControl
{
    private UpdatePopoverViewModel? _vm;

    public UpdatePopover()
    {
        InitializeComponent();
    }

    public void Bind(UpdatePopoverViewModel vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = vm;
        // Click+CanExecuteChanged instead of Button.Command -- WinUI 3 +
        // CsWinRT CCW tables aren't emitted for managed ICommand impls
        // assigned in code-behind. See UpdatePill.WireCommand.
        WireCommand(SkipButton,    vm.SkipCommand);
        WireCommand(InstallButton, vm.InstallAndRelaunchCommand);
        WireCommand(RetryButton,   vm.RetryCommand);

        // Restart needs a confirmation step so users don't lose unsaved
        // terminal work on an accidental click. Keep the CanExecute wiring
        // from WireCommand, but route Click through the confirm dialog.
        RestartButton.IsEnabled = vm.RestartNowCommand.CanExecute(null);
        vm.RestartNowCommand.CanExecuteChanged += (_, _) =>
            RestartButton.IsEnabled = vm.RestartNowCommand.CanExecute(null);
        RestartButton.Click += OnRestartClick;

        // Dismiss: close the popover and ask the driver to go Idle so the
        // pill goes away. The flyout's own light-dismiss (click outside)
        // only closes the popover without touching state.
        DismissButton.Click += (_, _) => vm.DismissRequested();

        // Release notes link: open the URL in the user's default browser.
        // HyperlinkButton.NavigateUri doesn't fire for non-http schemes in
        // unpackaged apps, so use Launcher.LaunchUriAsync explicitly.
        ReleaseNotesLink.Click += OnReleaseNotesClick;

        vm.PropertyChanged += OnVmPropertyChanged;
        Refresh();
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        // async void handler: any escaping exception crashes the process.
        // Wrap the whole body, not just ShowAsync, so a throw from Execute()
        // (e.g. driver disposed mid-click) is caught too.
        try
        {
            if (_vm is null) return;
            if (!_vm.RestartNowCommand.CanExecute(null)) return;

            var xamlRoot = XamlRoot;
            if (xamlRoot is null)
            {
                // Popover not in the visual tree: fall back to an unconfirmed
                // invocation. Shouldn't happen under normal operation.
                _vm.RestartNowCommand.Execute(null);
                return;
            }

            // Close the flyout first so the dialog modal layer can acquire
            // input without fighting the popover. The dialog shows on the
            // XamlRoot (the window) after the flyout starts its close anim.
            _vm.RequestClose();

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Restart wintty?",
                Content = "Any running terminals will be closed. Open sessions may lose unsaved work.",
                PrimaryButtonText = "Restart",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary
                && _vm.RestartNowCommand.CanExecute(null))
            {
                _vm.RestartNowCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] restart confirm handler failed: {ex.Message}");
        }
    }

    private async void OnReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        if (_vm?.ReleaseNotesUrl is not { } url) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] LaunchUriAsync failed: {ex.Message}");
        }
    }

    private static void WireCommand(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button, ICommand command)
    {
        button.Click += (_, _) =>
        {
            if (command.CanExecute(null)) command.Execute(null);
        };
        button.IsEnabled = command.CanExecute(null);
        command.CanExecuteChanged += (_, _) => button.IsEnabled = command.CanExecute(null);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_vm is null) return;

        HeaderBlock.Text = HeaderFromState(_vm.State);
        BodyBlock.Text = BodyFromState(_vm.State, _vm.TargetVersion, _vm.ErrorMessage);

        // Skip label carries the version so the user can see exactly
        // what they're silencing. "Skip this version" is the fallback
        // for the brief window where TargetVersion is null mid-transition.
        SkipButton.Content = string.IsNullOrEmpty(_vm.TargetVersion)
            ? "Skip this version"
            : $"Skip {_vm.TargetVersion}";
        SkipButton.Visibility = ShowFor(_vm.State, UpdateState.UpdateAvailable);
        InstallButton.Visibility = ShowFor(_vm.State, UpdateState.UpdateAvailable);
        RestartButton.Visibility = ShowFor(_vm.State, UpdateState.RestartPending);
        RetryButton.Visibility = ShowFor(_vm.State, UpdateState.Error);
        DismissButton.Visibility = _vm.State == UpdateState.Error
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Release notes link visible for any state where the driver
        // attached a URL - typically UpdateAvailable and RestartPending.
        var hasNotes = !string.IsNullOrEmpty(_vm.ReleaseNotesUrl);
        ReleaseNotesDivider.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesLink.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string HeaderFromState(UpdateState s) => s switch
    {
        UpdateState.UpdateAvailable => "Update Available",
        UpdateState.RestartPending => "Restart to Finish Updating",
        UpdateState.Error => "Update Error",
        UpdateState.NoUpdatesFound => "Up to date",
        UpdateState.Downloading => "Downloading update",
        UpdateState.Extracting => "Preparing update",
        UpdateState.Installing => "Installing update",
        _ => string.Empty,
    };

    private static string BodyFromState(UpdateState s, string? v, string? err) => s switch
    {
        UpdateState.UpdateAvailable => $"Version {v ?? "?"} is ready to install.",
        UpdateState.RestartPending => "Restart to apply the downloaded update.",
        UpdateState.Error => FriendlyError(err),
        UpdateState.NoUpdatesFound => "You are running the latest version of wintty.",
        UpdateState.Downloading => "Retrieving the update package from the release server.",
        UpdateState.Extracting => "Verifying and extracting the downloaded package.",
        UpdateState.Installing => "Applying the update to the on-disk install. This usually takes a few seconds.",
        _ => string.Empty,
    };

    // Lead with a plain-language sentence. In Debug builds we append the
    // driver-supplied detail so devs can see what the simulator / real
    // driver actually reported; in Release the raw error (HTTP code, etc.)
    // stays in the log, not the user-facing body.
    private static string FriendlyError(string? err)
    {
        const string friendly = "We couldn't finish downloading the update. Check your internet connection and try Retry.";
#if DEBUG
        return string.IsNullOrWhiteSpace(err) ? friendly : $"{friendly}\n\nTechnical detail: {err}";
#else
        _ = err;
        return friendly;
#endif
    }

    private static Visibility ShowFor(UpdateState current, UpdateState expected) =>
        current == expected ? Visibility.Visible : Visibility.Collapsed;
}
