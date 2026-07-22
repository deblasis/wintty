using System.Collections.ObjectModel;

namespace Ghostty.Core.Notifications;

/// <summary>
/// App-wide queue of transient in-window notices. The notification host in each
/// window binds to <see cref="Active"/> and renders one banner per notice.
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
