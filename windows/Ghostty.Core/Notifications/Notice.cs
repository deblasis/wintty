using System;
using System.Collections.Generic;

namespace Ghostty.Core.Notifications;

/// <summary>
/// A transient in-window notice or user-choice prompt shown by
/// <see cref="INotificationService"/> and rendered by the app's notification
/// host as a dismissable banner. Informational notices carry no actions (just
/// the close X); user-choice notices carry one or more <see cref="NoticeAction"/>s.
///
/// <para>
/// This is deliberately UI-free (no XAML types) so notices can be created and
/// the service unit-tested without the WinUI runtime. The macOS app models the
/// same request → choice → response shape on its update view-model; here a
/// choice is an action whose <see cref="NoticeAction.Invoke"/> is the reply.
/// </para>
/// </summary>
public sealed class Notice
{
    /// <summary>Short bold headline.</summary>
    public required string Title { get; init; }

    /// <summary>Body text. May be empty for a title-only notice.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Visual severity. Default <see cref="NoticeSeverity.Informational"/>.</summary>
    public NoticeSeverity Severity { get; init; } = NoticeSeverity.Informational;

    /// <summary>
    /// Whether the notice shows a close (X) affordance. Set false for a forced
    /// choice the user must resolve via an action.
    /// </summary>
    public bool IsClosable { get; init; } = true;

    /// <summary>Buttons, in display order. Empty for an informational notice.</summary>
    public IReadOnlyList<NoticeAction> Actions { get; init; } = Array.Empty<NoticeAction>();

    /// <summary>
    /// When set, <see cref="INotificationService.Show"/> ignores a second
    /// notice with the same key while one is already active — prevents the same
    /// event stacking duplicate banners.
    /// </summary>
    public string? DedupKey { get; init; }

    /// <summary>
    /// Invoked once when the notice leaves the screen — via the close X, an
    /// action that dismisses, or <see cref="INotificationService.Dismiss"/>.
    /// </summary>
    public Action? OnDismiss { get; init; }
}
