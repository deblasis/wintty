using System.Collections.ObjectModel;

namespace Ghostty.Core.Notifications;

/// <summary>
/// App-wide queue of transient in-window notices. The notification host in each
/// window binds to <see cref="Active"/> and renders one banner per notice.
///
/// <para>
/// Scope: this is for <b>app-level</b> in-window notices and inline user
/// choices (e.g. the NO_COLOR notice) — things the shell wants to tell the user
/// about itself. Terminal-originated desktop notifications (libghostty's
/// <c>desktop_notification</c> action, OSC 9 / OSC 777) are a separate concern
/// and belong on the native Windows notification surface (toasts), matching how
/// the macOS app routes them to the system notification center rather than into
/// an in-window banner.
/// </para>
///
/// <para>
/// Not thread-safe: <see cref="Show"/> and <see cref="Dismiss"/> mutate the
/// bound collection and must be called on the UI thread. Callers reacting to
/// off-thread events (e.g. a libghostty action on a thread-pool thread) must
/// marshal first.
/// </para>
/// </summary>
public interface INotificationService
{
    /// <summary>Active notices, oldest first. Bound by the UI host.</summary>
    ReadOnlyObservableCollection<Notice> Active { get; }

    /// <summary>
    /// Show a notice. If its <see cref="Notice.DedupKey"/> matches an already
    /// active notice, this is a no-op.
    /// </summary>
    void Show(Notice notice);

    /// <summary>
    /// Remove a notice and fire its <see cref="Notice.OnDismiss"/> once. No-op
    /// if the notice is not currently active.
    /// </summary>
    void Dismiss(Notice notice);
}
