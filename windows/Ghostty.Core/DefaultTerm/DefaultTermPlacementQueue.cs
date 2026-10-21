namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// Where a handed-off session lands. The app side implements this over
/// its real windows; the queue itself stays UI-free and testable.
/// Window handles are opaque objects exactly so no WinUI type leaks in.
/// </summary>
public interface IDefaultTermSurfaceHost
{
    /// <summary>
    /// The window the user last activated, or null when there is none.
    /// </summary>
    object? LastActivatedWindow();

    /// <summary>Any open window, or null when the process has none.</summary>
    object? AnyWindow();

    /// <summary>
    /// Adopt the handed-off session into <paramref name="window"/> as a
    /// new tab, or into a new window when it is null. Returns whether
    /// the session was adopted; false degrades that launch to conhost.
    /// </summary>
    bool Adopt(object? window, HandoffPlacementRequest request);
}

/// <summary>
/// The in-process, COM-thread-owned queue holding handoffs that arrive
/// before the UI is ready. Bounded: a queue at capacity refuses the
/// launch, which degrades to conhost. Every item carries its own
/// budget; an item past its budget is completed with the failure result
/// wherever it is found, and only that launch degrades. Placement runs
/// on the thread that drains, which the app wires to the UI dispatcher
/// via <see cref="DrainRequested"/>; the window rule is last-activated,
/// then any, then a new window. A secondary receiver never forwards: it
/// hosts in its own window, and the queue has no forwarding path at
/// all.
/// </summary>
public sealed class DefaultTermPlacementQueue
{
    private readonly int _capacity;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly List<QueuedItem> _items = new();
    private IDefaultTermSurfaceHost? _host;

    /// <summary>
    /// Invoked after an item parks, on the parking (COM) thread: the
    /// app posts <see cref="Drain"/> to the UI dispatcher. Null until
    /// wired; a drain can also arrive from
    /// <see cref="AttachHost"/> or a plain <see cref="Drain"/> call.
    /// </summary>
    public Action? DrainRequested { get; set; }

    /// <summary>
    /// The no-forwarding canary: nothing in this class may ever call
    /// it. It exists so a test can prove the single-instance forwarding
    /// path is never entered from placement.
    /// </summary>
    internal Action<HandoffPlacementRequest>? ForwardingProbe;

    public DefaultTermPlacementQueue(int capacity, Func<DateTime>? clock = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _clock = clock ?? StaticClock;
    }

    private static DateTime StaticClock() => DateTime.UtcNow;

    /// <summary>Items parked and not yet placed or expired.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// The first surface host is ready: remember it and drain what
    /// parked before it existed. Runs on the UI thread.
    /// </summary>
    public void AttachHost(IDefaultTermSurfaceHost host)
    {
        lock (_gate)
        {
            _host = host;
        }

        Drain();
    }

    /// <summary>
    /// Place every parkable item into the host, most-recent-window rule
    /// per item. Safe to call from any thread the host tolerates; the
    /// app calls it on the UI thread.
    /// </summary>
    public void Drain()
    {
        IDefaultTermSurfaceHost? host;
        QueuedItem[] snapshot;
        lock (_gate)
        {
            host = _host;
            if (host is null)
            {
                return;
            }

            SweepExpiredUnsafe();
            snapshot = _items.ToArray();
        }

        foreach (var item in snapshot)
        {
            lock (_gate)
            {
                // Expired or placed between the snapshot and here.
                if (!_items.Contains(item))
                {
                    continue;
                }

                _items.Remove(item);
            }

            // The window rule, in order: last-activated, then any, then
            // a new window.
            var window = host.LastActivatedWindow() ?? host.AnyWindow();

            bool adopted;
            try
            {
                adopted = host.Adopt(window, item.Request);
            }
            catch
            {
                // A host failure degrades this launch only; the queue
                // keeps serving later items.
                adopted = false;
            }

            item.Complete(adopted
                ? HandoffPlacementResult.Adopted
                : HandoffPlacementResult.Timeout);
        }
    }

    /// <summary>
    /// Park one handed-off session from the COM thread and wait, within
    /// its budget, for placement. Returns the placement outcome: the
    /// request is completed exactly once, with the result the caller
    /// also sees here, so a degraded launch reports failure through
    /// both channels. A queue at capacity refuses immediately.
    /// </summary>
    public HandoffPlacementResult ParkAndWait(
        HandoffPlacementRequest request,
        TimeSpan budget)
    {
        var item = new QueuedItem(request, _clock().Add(budget));
        lock (_gate)
        {
            SweepExpiredUnsafe();
            if (_items.Count >= _capacity)
            {
                return HandoffPlacementResult.Timeout;
            }

            _items.Add(item);
        }

        DrainRequested?.Invoke();

        var remaining = item.Deadline - _clock();
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        if (!item.Completed.Wait(remaining))
        {
            lock (_gate)
            {
                if (!item.Done)
                {
                    _items.Remove(item);
                    item.Complete(HandoffPlacementResult.Timeout);
                }
            }
        }

        return item.Result;
    }

    /// <summary>Caller holds the gate lock.</summary>
    private void SweepExpiredUnsafe()
    {
        var now = _clock();
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Deadline <= now)
            {
                var expired = _items[i];
                _items.RemoveAt(i);
                expired.Complete(HandoffPlacementResult.Timeout);
            }
        }
    }

    private sealed class QueuedItem
    {
        public QueuedItem(HandoffPlacementRequest request, DateTime deadline)
        {
            Request = request;
            Deadline = deadline;
        }

        public HandoffPlacementRequest Request { get; }

        public DateTime Deadline { get; }

        public ManualResetEventSlim Completed { get; } = new(false);

        public bool Done { get; private set; }

        public HandoffPlacementResult Result { get; private set; }

        public void Complete(HandoffPlacementResult result)
        {
            lock (Completed)
            {
                if (Done)
                {
                    return;
                }

                Done = true;
                Result = result;
            }

            _ = Request.Complete(result);
            Completed.Set();
        }
    }
}
