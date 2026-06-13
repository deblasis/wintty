using System;
using Ghostty.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Ghostty.Notifications;

/// <summary>
/// <see cref="IToastNotifier"/> backed by WinAppSDK
/// <see cref="AppNotificationManager"/>. Each toast carries a constant Tag
/// plus a per-surface Group (the surface key): Windows replaces a toast when
/// both match, so a newer toast for a surface supersedes the older one, and
/// <see cref="ClearForSurface"/> removes a surface's toast by group on focus
/// regain. Every call is guarded -- a toast failure must never propagate back
/// through GhosttyHost into the libghostty callback.
/// </summary>
internal sealed class AppNotificationToastNotifier : IToastNotifier
{
    // Constant Tag + per-surface Group means two toasts for the same surface
    // collide (same Tag+Group) and the second replaces the first.
    private const string NotificationTag = "wintty-notification";

    private readonly ILogger<AppNotificationToastNotifier> _logger;

    public AppNotificationToastNotifier(ILogger<AppNotificationToastNotifier> logger)
        => _logger = logger;

    public void Show(ToastRequest request)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(request.Title)
                .AddText(request.Body)
                .BuildNotification();
            notification.Tag = NotificationTag;
            notification.Group = request.SurfaceKey;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            _logger.LogToastShowFailed(ex);
        }
    }

    public void ClearForSurface(string surfaceKey)
    {
        try
        {
            // Best-effort cleanup; fire-and-forget. We deliberately do not
            // await -- ClearForSurface is called from a UI focus handler and
            // a stale toast lingering a few ms longer is harmless.
            _ = AppNotificationManager.Default.RemoveByGroupAsync(surfaceKey);
        }
        catch (Exception ex)
        {
            _logger.LogToastClearFailed(ex);
        }
    }
}

internal static partial class ToastNotifierLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Notifications.ShowFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to show toast notification")]
    internal static partial void LogToastShowFailed(
        this ILogger<AppNotificationToastNotifier> logger, Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Notifications.ClearFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to clear toast notifications")]
    internal static partial void LogToastClearFailed(
        this ILogger<AppNotificationToastNotifier> logger, Exception ex);
}
