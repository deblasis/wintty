using System;
using System.Diagnostics;
using Ghostty.Core.Sponsor.Update;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Hooks <see cref="Window.Closed"/> preparation to show a
/// <c>ContentDialog</c> when the user is closing with a
/// RestartPending update. Replaces the macOS NSAlert pattern
/// with the Windows-native ContentDialog on XamlRoot.
///
/// WinUI 3 doesn't expose a cancellable Closing on Window directly;
/// we use AppWindow.Closing which does.
///
/// Marshals <see cref="UpdateService.StateChanged"/> to the UI thread via <see cref="DispatcherQueue"/>; see <see cref="TaskbarOverlayProvider"/>.
/// </summary>
internal sealed class RestartPendingExitInterceptor : IDisposable
{
    private readonly UpdateService _service;
    private readonly DispatcherQueue _dispatcher;
    private Window? _window;
    private bool _intercepting;

    public RestartPendingExitInterceptor(UpdateService service, DispatcherQueue dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
    }

    public void Attach(Window window)
    {
        _window = window;
        _service.StateChanged += OnStateChanged;
        UpdateIntercept(_service.Current.State);
    }

    private void OnStateChanged(object? sender, UpdateStateSnapshot snap)
    {
        // AppWindow.Closing subscription is UI-thread-affine; hop.
        _dispatcher.TryEnqueue(() => UpdateIntercept(snap.State));
    }

    private void UpdateIntercept(UpdateState state)
    {
        _intercepting = state == UpdateState.RestartPending;
        if (_window is null) return;
        var appWindow = _window.AppWindow;
        if (appWindow is null) return;
        if (_intercepting)
        {
            appWindow.Closing -= OnAppWindowClosing;
            appWindow.Closing += OnAppWindowClosing;
        }
        else
        {
            appWindow.Closing -= OnAppWindowClosing;
        }
    }

    private async void OnAppWindowClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // async void handler: any escaping exception crashes the process.
        // Wrap the whole body so a throw from args.Cancel, Close(), or
        // ApplyAndRestartAsync doesn't terminate the app mid-shutdown.
        try
        {
            if (!_intercepting) return;
            if (_window?.Content?.XamlRoot is not { } xamlRoot) return;

            args.Cancel = true;
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Restart to finish updating?",
                Content = "Wintty is ready to install the downloaded update. Restart now, or quit and apply later.",
                PrimaryButtonText = "Restart Now",
                SecondaryButtonText = "Quit Without Updating",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary)
            {
                await _service.ApplyAndRestartAsync();
            }
            else if (choice == ContentDialogResult.Secondary)
            {
                _intercepting = false;
                _window.Close();
            }
            // Cancel: stay open.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] exit dialog failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
        if (_window?.AppWindow is { } aw)
        {
            aw.Closing -= OnAppWindowClosing;
        }
    }
}
