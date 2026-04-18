using System;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Immutable observation of the update lifecycle at a point in time.
/// Carried on the wire in <see cref="IUpdateDriver.StateChanged"/>.
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="TargetVersion">Version string of the pending update (null when Idle/NoUpdatesFound).</param>
/// <param name="Progress">Download/apply progress in [0, 1], null when N/A.</param>
/// <param name="ErrorMessage">Human-readable error (Error state only).</param>
/// <param name="Timestamp">When this snapshot was produced.</param>
public sealed record UpdateStateSnapshot(
    UpdateState State,
    string? TargetVersion,
    double? Progress,
    string? ErrorMessage,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Absolute URL to the release notes page (GitHub Releases, appcast
    /// link, or similar). Populated by the driver for UpdateAvailable /
    /// RestartPending when a notes URL is known. The popover surfaces
    /// it as a "See what's new" link.
    /// </summary>
    public string? ReleaseNotesUrl { get; init; }

    /// <summary>Idle snapshot with a now-timestamp.</summary>
    public static UpdateStateSnapshot Idle() =>
        new(UpdateState.Idle, null, null, null, DateTimeOffset.UtcNow);
}
