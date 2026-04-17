using System;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;
using Ghostty.Taskbar;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

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
internal sealed class TaskbarHost : IDisposable
{
    private readonly TaskbarList3Facade? _facade;
    private readonly TaskbarProgressCoordinator? _coordinator;
    private readonly DispatcherQueueTimer? _tickTimer;

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

    public void Dispose()
    {
        _tickTimer?.Stop();
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
