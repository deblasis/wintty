using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Test seam wrapping Velopack's <c>UpdateManager</c>. The driver talks
/// through this so unit tests can substitute a fake without touching
/// disk, network, or process APIs. <c>VelopackManagerAdapter</c> (in
/// the shell assembly, imports Velopack) is the one production impl.
/// Kept in Ghostty.Core so the driver (also Core) compiles without
/// pulling in Velopack and so the test project can reference both.
/// </summary>
internal interface IVelopackManager
{
    /// <summary>
    /// True when the running exe was installed by Velopack (i.e.
    /// Update.exe is on disk). Callers gate apply-and-restart behaviour
    /// on this - a dev checkout returns false.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Query the source for the latest release. Null when we're already
    /// on the latest version.
    /// </summary>
    Task<VelopackUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct);

    /// <summary>
    /// Download the NUPKG bytes for the supplied info, reporting 0-100
    /// progress. Throws <see cref="OperationCanceledException"/> if
    /// cancellation is requested.
    /// </summary>
    Task DownloadUpdatesAsync(
        VelopackUpdateInfo info,
        IProgress<int> progress,
        CancellationToken ct);

    /// <summary>
    /// Apply the downloaded update and restart. Does not return under
    /// normal operation - Velopack spawns Update.exe, which kills the
    /// current process.
    /// </summary>
    void ApplyUpdatesAndRestart(VelopackUpdateInfo info);
}
