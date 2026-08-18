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
/// regain. The same surface key also travels in the toast's launch arguments,
/// which is the only part of it a click can read back. Every call is guarded
/// -- a toast failure must never propagate back through GhosttyHost into the
/// libghostty callback.
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
            // OSC 9 carries only a body (title is empty); OSC 777 and the
            // child-exited toast set both. Skip the title line when empty so
            // OSC 9 does not render a blank bold heading above the message.
            var builder = new AppNotificationBuilder();
            if (!string.IsNullOrEmpty(request.Title)) builder.AddText(request.Title);
            builder.AddText(request.Body);

            // Name the surface in the toast's launch arguments. Group already
            // carries it, but Group is only readable by us for replace/remove;
            // the activation callback sees arguments and nothing else, so
            // without this a click knows the app was clicked and not which
            // pane asked for attention.
            if (!string.IsNullOrEmpty(request.SurfaceKey))
            {
                builder.AddArgument(
                    Ghostty.Core.Activation.ToastActivation.SurfaceArgumentKey,
                    request.SurfaceKey);
            }

            var notification = builder.BuildNotification();
            notification.Tag = NotificationTag;
            notification.Group = request.SurfaceKey;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            // Intentional catch-all: Show is reached from the libghostty
            // action callback, and a managed exception must never cross back
            // into native code. COMException/InvalidOperationException are the
            // expected failures; anything else is logged, not propagated.
            _logger.LogToastShowFailed(ex);
        }
    }

    public void ClearForSurface(string surfaceKey)
    {
        try
        {
            // Best-effort cleanup; fire-and-forget. We deliberately do not
            // await and do not observe the returned IAsyncAction --
            // ClearForSurface is called from a UI focus handler and a stale
            // toast lingering a few ms longer (or a failed removal) is
            // harmless. The try/catch covers only a synchronous throw from
            // kicking off the call.
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
