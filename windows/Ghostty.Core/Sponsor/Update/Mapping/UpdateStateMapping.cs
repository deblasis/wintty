using System;

namespace Ghostty.Core.Sponsor.Update.Mapping;

/// <summary>
/// Pure functions that project Velopack events (or exceptions) onto
/// <see cref="UpdateStateSnapshot"/>. Extracted so the state machine
/// is testable without any Velopack or WinUI machinery.
/// </summary>
internal static class UpdateStateMapping
{
    public static UpdateStateSnapshot FromCheckResult(VelopackUpdateInfo? info)
    {
        if (info is null)
        {
            return new UpdateStateSnapshot(
                UpdateState.NoUpdatesFound,
                TargetVersion: null,
                Progress: null,
                ErrorMessage: null,
                Timestamp: DateTimeOffset.UtcNow);
        }

        return new UpdateStateSnapshot(
            UpdateState.UpdateAvailable,
            TargetVersion: info.Version,
            Progress: null,
            ErrorMessage: null,
            Timestamp: DateTimeOffset.UtcNow)
        {
            ReleaseNotesUrl = info.ReleaseNotesUrl,
        };
    }

    public static UpdateStateSnapshot FromDownloadProgress(
        int percent,
        string targetVersion,
        string? releaseNotesUrl)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return new UpdateStateSnapshot(
            UpdateState.Downloading,
            TargetVersion: targetVersion,
            Progress: clamped / 100.0,
            ErrorMessage: null,
            Timestamp: DateTimeOffset.UtcNow)
        {
            ReleaseNotesUrl = releaseNotesUrl,
        };
    }

    public static UpdateStateSnapshot FromDownloadComplete(
        string targetVersion,
        string? releaseNotesUrl)
    {
        return new UpdateStateSnapshot(
            UpdateState.RestartPending,
            TargetVersion: targetVersion,
            Progress: null,
            ErrorMessage: null,
            Timestamp: DateTimeOffset.UtcNow)
        {
            ReleaseNotesUrl = releaseNotesUrl,
        };
    }

    public static UpdateStateSnapshot FromCancel(
        string? targetVersion,
        string? releaseNotesUrl)
    {
        return new UpdateStateSnapshot(
            UpdateState.UpdateAvailable,
            TargetVersion: targetVersion,
            Progress: null,
            ErrorMessage: null,
            Timestamp: DateTimeOffset.UtcNow)
        {
            ReleaseNotesUrl = releaseNotesUrl,
        };
    }

    public static UpdateStateSnapshot FromError(
        UpdateCheckException ex,
        string? targetVersion)
    {
        var message = ex.Kind switch
        {
            UpdateErrorKind.NoToken => "Sign in to check for updates.",
            UpdateErrorKind.AuthExpired => "Sponsor session expired. Sign in again.",
            UpdateErrorKind.NotEntitled => "This channel isn't available for your sponsorship tier.",
            UpdateErrorKind.Offline => "Can't reach wintty.io. Check your connection.",
            UpdateErrorKind.ServerError => "Update server is having a hiccup. Try again later.",
            UpdateErrorKind.ManifestInvalid => "Update manifest unreadable. This is a bug - please report.",
            UpdateErrorKind.HashMismatch => "Downloaded update didn't verify. Try again.",
            UpdateErrorKind.ApplyFailed => "Couldn't apply the update. Try again or reinstall.",
            UpdateErrorKind.NotInstalled => "Updates are only available on signed installs. This build was started from a dev checkout or portable zip.",
            _ => "Update failed. Try again later.",
        };

        var head = $"{ex.GetType().Name}: {ex.Detail ?? ex.Message}";
        var detail = ex.InnerException is null
            ? head
            : $"{head} <- {ex.InnerException.GetType().Name}";

        return new UpdateStateSnapshot(
            UpdateState.Error,
            TargetVersion: targetVersion,
            Progress: null,
            ErrorMessage: message,
            Timestamp: DateTimeOffset.UtcNow)
        {
            TechnicalDetail = detail,
        };
    }
}
