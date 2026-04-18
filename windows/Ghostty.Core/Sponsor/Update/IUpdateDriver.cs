using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Abstraction over the update source. D.1 provides <c>UpdateSimulator</c>
/// (synthetic state transitions for UI exercise); D.2 will provide a
/// Velopack-backed driver that hits api.wintty.io.
///
/// Implementations are free to raise StateChanged on any thread.
/// Consumers marshal to the UI thread at their own boundary.
/// </summary>
public interface IUpdateDriver
{
    /// <summary>Last snapshot emitted. Never null; Idle by default.</summary>
    UpdateStateSnapshot Current { get; }

    /// <summary>Raised every time the driver transitions state.</summary>
    event EventHandler<UpdateStateSnapshot> StateChanged;

    /// <summary>
    /// Poll for a new version. D.1 simulator ignores this call.
    /// D.2 implementation contacts api.wintty.io with the sponsor JWT.
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Begin downloading the pending update, if any.</summary>
    Task DownloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply the downloaded update and restart the process.
    /// Does not return under normal operation (process replaces itself).
    /// </summary>
    Task ApplyAndRestartAsync();

    /// <summary>
    /// Return the driver to a quiet state. For the simulator this means
    /// emitting an Idle snapshot so the pill disappears. For the real
    /// Velopack driver (D.2) this clears any transient Error state; the
    /// next scheduled poll may discover an update again. Used by the
    /// popover's Dismiss button when the user wants to stop seeing a
    /// stuck Error without waiting for the next check cycle.
    /// </summary>
    Task DismissAsync(CancellationToken cancellationToken = default);
}
