using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Ghostty.Core.Notifications;

/// <summary>
/// Default <see cref="INotificationService"/>: owns the active-notice
/// collection the UI host binds to. See the interface for threading rules.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly ObservableCollection<Notice> _active = new();

    public ReadOnlyObservableCollection<Notice> Active { get; }

    public NotificationService()
    {
        Active = new ReadOnlyObservableCollection<Notice>(_active);
    }

    public void Show(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (notice.DedupKey is { } key &&
            _active.Any(n => string.Equals(n.DedupKey, key, StringComparison.Ordinal)))
        {
            return;
        }

        _active.Add(notice);
    }

    public void Dismiss(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (_active.Remove(notice))
        {
            notice.OnDismiss?.Invoke();
        }
    }
}
