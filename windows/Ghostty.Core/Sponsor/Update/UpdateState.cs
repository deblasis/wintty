namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Lifecycle state of a pending update as observed by the client.
/// Mirrors the macOS UpdateViewModel.UpdateState but Windows-native in UX.
/// </summary>
public enum UpdateState
{
    /// <summary>No update known; pill hidden.</summary>
    Idle,
    /// <summary>Check completed, no update available; pill auto-dismisses.</summary>
    NoUpdatesFound,
    /// <summary>Update available and awaiting user choice.</summary>
    UpdateAvailable,
    /// <summary>Download in progress.</summary>
    Downloading,
    /// <summary>Post-download extraction.</summary>
    Extracting,
    /// <summary>Applying update to on-disk install.</summary>
    Installing,
    /// <summary>Update applied; restart pending.</summary>
    RestartPending,
    /// <summary>Check or apply failed.</summary>
    Error,
}
