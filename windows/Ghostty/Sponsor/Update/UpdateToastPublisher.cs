using System;
using System.Diagnostics;
using Ghostty.Core.Sponsor.Update;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WinRT.Interop;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Fires a single toast when the state transitions to UpdateAvailable
/// or RestartPending *and* the window is not foreground *and* Focus
/// Assist isn't suppressing notifications. The pill remains the
/// primary surface when the user can see it; the toast is for users
/// who aren't looking at the window.
///
/// The handler marshals UI-affine work to the provided <see cref="DispatcherQueue"/>;
/// see <see cref="TaskbarOverlayProvider"/> for the same pattern.
/// </summary>
internal sealed class UpdateToastPublisher : IDisposable
{
    private static bool s_registered;
    private readonly UpdateService _service;
    private readonly DispatcherQueue _dispatcher;
    private Window? _window;
    private UpdateState _lastSeen = UpdateState.Idle;

    public UpdateToastPublisher(UpdateService service, DispatcherQueue dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
    }

    public void Attach(Window window)
    {
        _window = window;
        TryRegister();
        _service.StateChanged += OnStateChanged;
    }

    private static void TryRegister()
    {
        if (s_registered) return;
        try
        {
            AppNotificationManager.Default.Register();
            s_registered = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] AppNotificationManager.Register failed: {ex.Message}. Toast publisher will no-op for this session.");
        }
    }

    private void OnStateChanged(object? sender, UpdateStateSnapshot snap)
    {
        var prev = _lastSeen;
        _lastSeen = snap.State;

        if (snap.State != UpdateState.UpdateAvailable &&
            snap.State != UpdateState.RestartPending)
        {
            return;
        }
        if (prev == snap.State) return;
        if (!s_registered) return;

        _dispatcher.TryEnqueue(() => ShowToastIfAppropriate(snap));
    }

    private void ShowToastIfAppropriate(UpdateStateSnapshot snap)
    {
        if (IsWindowForeground()) return;
        if (!FocusAssistProbe.CanNotify()) return;

        try
        {
            var builder = snap.State == UpdateState.UpdateAvailable
                ? new AppNotificationBuilder()
                    .AddText("Update Available")
                    .AddText(snap.TargetVersion is null ? string.Empty : $"Version {snap.TargetVersion} is ready to install.")
                    .AddButton(new AppNotificationButton("Install and Restart")
                        .AddArgument("action", "install-restart"))
                : new AppNotificationBuilder()
                    .AddText("Restart to Finish Updating")
                    .AddText("Restart to apply the downloaded update.")
                    .AddButton(new AppNotificationButton("Restart Now")
                        .AddArgument("action", "install-restart"));

            // Tag + Group: successive publishes with the same (tag, group)
            // replace the previous toast in Action Center instead of stacking.
            // Without Group, a user who sees UpdateAvailable then RestartPending
            // would find both entries still sitting in Action Center later.
            builder.SetTag("wintty-update").SetGroup("wintty-update");
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] toast Show failed: {ex.Message}");
        }
    }

    private bool IsWindowForeground()
    {
        if (_window is null) return true;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            var fg = Windows.Win32.PInvoke.GetForegroundWindow();
            return fg == new Windows.Win32.Foundation.HWND(hwnd);
        }
        catch { return true; }
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
    }
}
