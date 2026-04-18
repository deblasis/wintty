using System;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Update;
using Velopack;
using Velopack.Sources;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Adapts Velopack's <see cref="UpdateManager"/> to
/// <see cref="IVelopackManager"/>. Owned by
/// <see cref="SponsorOverlayBootstrapper"/>; disposed with it.
/// </summary>
internal sealed class VelopackManagerAdapter : IVelopackManager
{
    private readonly UpdateManager _manager;

    // 0.0.1298: UpdateManager(IUpdateSource, UpdateOptions?, IVelopackLocator?)
    // The plan's ctor included an ILogger? parameter that Velopack removed;
    // options and locator are optional so we pass null for both.
    public VelopackManagerAdapter(IUpdateSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _manager = new UpdateManager(source);
    }

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<VelopackUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct)
    {
        // 0.0.1298: CheckForUpdatesAsync() takes no arguments.
        // ct is accepted by the interface but not forwarded - Velopack
        // 0.0.1298 has no overload that accepts a CancellationToken here.
        var native = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (native is null) return null;

        // Velopack's UpdateInfo exposes TargetFullRelease.NotesMarkdown.
        // The manifest we publish does not include release notes URLs today
        // (Plan B.3 adds them); stay defensive and default to null.
        var notesUrl = native.TargetFullRelease?.NotesMarkdown is { Length: > 0 } notes
            ? notes
            : null;

        return new VelopackUpdateInfo(
            Version: native.TargetFullRelease!.Version.ToString(),
            ReleaseNotesUrl: notesUrl,
            NativeInfo: native);
    }

    public Task DownloadUpdatesAsync(
        VelopackUpdateInfo info,
        IProgress<int> progress,
        CancellationToken ct)
    {
        var native = (UpdateInfo)info.NativeInfo;

        // 0.0.1298: DownloadUpdatesAsync(UpdateInfo, Action<int>?, CancellationToken)
        // The interface surface uses IProgress<int> for testability; we bridge
        // to the Action<int> delegate that Velopack actually requires.
        return _manager.DownloadUpdatesAsync(native, progress.Report, ct);
    }

    public void ApplyUpdatesAndRestart(VelopackUpdateInfo info)
    {
        var native = (UpdateInfo)info.NativeInfo;

        // 0.0.1298: ApplyUpdatesAndRestart(VelopackAsset, string[]?)
        // The plan passed UpdateInfo directly; the real API wants the asset.
        // TargetFullRelease is non-null here because we only get called after
        // a successful CheckForUpdatesAsync + DownloadUpdatesAsync.
        _manager.ApplyUpdatesAndRestart(native.TargetFullRelease);
    }
}
