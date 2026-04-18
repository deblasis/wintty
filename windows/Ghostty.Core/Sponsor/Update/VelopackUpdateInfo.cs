namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Driver-facing projection of a Velopack update. The adapter in the
/// shell assembly converts Velopack's native <c>UpdateInfo</c> into
/// this record so <c>Ghostty.Core</c> stays Velopack-free (and thus
/// OSS-buildable). <c>NativeInfo</c> is opaque to everyone except
/// <c>VelopackManagerAdapter</c>, which round-trips it through to
/// <c>DownloadUpdatesAsync</c> and <c>ApplyUpdatesAndRestart</c>.
/// </summary>
/// <param name="Version">Semver of the pending update.</param>
/// <param name="ReleaseNotesUrl">
/// Optional URL of the release notes (usually a GitHub Releases page);
/// propagated to <c>UpdateStateSnapshot.ReleaseNotesUrl</c> and surfaced
/// in the popover.
/// </param>
/// <param name="NativeInfo">
/// Opaque Velopack <c>UpdateInfo</c>. The adapter casts and unwraps.
/// </param>
internal sealed record VelopackUpdateInfo(
    string Version,
    string? ReleaseNotesUrl,
    object NativeInfo);
