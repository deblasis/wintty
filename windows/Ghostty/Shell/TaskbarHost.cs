using System;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;
using Ghostty.Taskbar;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Ghostty.Shell;

/// <summary>
/// Wires the per-tab progress reporting (Ghostty.Core.Taskbar.
/// TaskbarProgressCoordinator) into the OS taskbar via
/// ITaskbarList3, and pauses the cycling timer when the window is
/// minimized.
///
/// Construction is wrapped in a try/catch because a COM failure
/// here must not block window construction — the taskbar indicator
/// is a nice-to-have. <see cref="IsAvailable"/> reports whether the
/// wiring is live; if not, every call is a no-op.
/// </summary>
internal sealed partial class TaskbarHost : IDisposable
{
    private readonly TaskbarList3Facade? _facade;
    private readonly TaskbarProgressCoordinator? _coordinator;
    private readonly DispatcherQueueTimer? _tickTimer;
    private readonly TaskbarOverlayFacade? _overlayFacade;
    private readonly TaskbarAttentionCoordinator? _attention;
    private readonly Window? _window;

    public bool IsAvailable => _coordinator is not null;

    public TaskbarHost(Window window, TabManager tabs, ILogger<TaskbarHost> logger)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            _facade = new TaskbarList3Facade(hwnd);
            _coordinator = new TaskbarProgressCoordinator(
                tabs,
                _facade,
                () => DateTime.UtcNow);

            _overlayFacade = new TaskbarOverlayFacade(hwnd);
            _attention = new TaskbarAttentionCoordinator(tabs, _overlayFacade);
            _window = window;
            // Window focus drives the attention badge: an unfocused bell
            // sets it, regaining focus clears it. Seed from the current
            // foreground state so a bell that arrives before the first
            // Activated event is judged against real focus, not the
            // coordinator's pessimistic default; Activated then tracks
            // every later transition.
            _attention.SetFocused(PInvoke.GetForegroundWindow() == new HWND(hwnd));
            window.Activated += OnWindowActivated;

            _tickTimer = window.DispatcherQueue.CreateTimer();
            _tickTimer.Interval = TimeSpan.FromSeconds(2);
            _tickTimer.IsRepeating = true;
            _tickTimer.Tick += (_, _) => _coordinator.Tick();
            _tickTimer.Start();
        }
        catch (Exception ex)
        {
            logger.LogTaskbarWiringFailed(ex);
        }
    }

    /// <summary>
    /// Hand-off from MainWindow's <c>AppWindow.Changed</c> handler.
    /// Pause the cycling timer when minimized so the taskbar does
    /// not churn while the window is hidden, and resume otherwise.
    /// </summary>
    public void OnAppWindowChanged(AppWindow appWindow)
    {
        if (_coordinator is null) return;
        if (appWindow.Presenter is OverlappedPresenter op)
        {
            if (op.State == OverlappedPresenterState.Minimized)
                _coordinator.Pause();
            else
                _coordinator.Resume();
        }
    }

    /// <summary>
    /// Drive the attention badge from window focus. Gaining focus clears
    /// any pending badge; an unfocused bell sets it.
    /// </summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _attention?.SetFocused(
            args.WindowActivationState != WindowActivationState.Deactivated);
    }

    public void Dispose()
    {
        _tickTimer?.Stop();
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _overlayFacade?.Dispose();
    }
}

internal static partial class TaskbarHostLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Shell.TaskbarWiringFailed,
                   Level = LogLevel.Warning,
                   Message = "Taskbar progress wiring failed")]
    internal static partial void LogTaskbarWiringFailed(
        this ILogger<TaskbarHost> logger, System.Exception ex);
}
