using System.Collections.Generic;
using Ghostty.Core.Notifications;

namespace Ghostty.Core.Taskbar;

/// <summary>Overlay kinds in priority order: the first active kind owns the slot.</summary>
public enum TaskbarBadgeKind { Bell = 0, UpdateError = 1, RestartPending = 2, UpdateAvailable = 3 }

/// <summary>
/// One reason to badge the taskbar icon. <paramref name="Key"/> names the
/// producer's slot ("bell", "update"); <paramref name="Signature"/> names the
/// situation, so a dismissed situation stays dismissed until it changes and a
/// bell episode gets a fresh one every time.
/// </summary>
public sealed record TaskbarBadge(
    string Key,
    TaskbarBadgeKind Kind,
    string Signature,
    string Title,
    string Message,
    NoticeSeverity Severity,
    IReadOnlyList<NoticeAction> Actions);
