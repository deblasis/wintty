namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Classes of failure surfaced by the update driver. Maps 1:1 with the
/// user-facing error text in <c>UpdateStateMapping.FromError</c>.
/// Expected (auth, offline) and unexpected (manifest parse, apply
/// failure) cases carry different log severities but share this enum.
/// </summary>
internal enum UpdateErrorKind
{
    /// <summary>No JWT available; user hasn't signed in.</summary>
    NoToken,
    /// <summary>Worker returned 401; cached JWT is dead.</summary>
    AuthExpired,
    /// <summary>Worker returned 403; channel not in the JWT's channel_allow.</summary>
    NotEntitled,
    /// <summary>Network / socket / timeout before reaching the Worker.</summary>
    Offline,
    /// <summary>Worker returned 5xx.</summary>
    ServerError,
    /// <summary>Manifest JSON couldn't be parsed.</summary>
    ManifestInvalid,
    /// <summary>Downloaded NUPKG's SHA1 didn't match the manifest entry.</summary>
    HashMismatch,
    /// <summary>Velopack's Apply stage threw before the process was killed.</summary>
    ApplyFailed,
    /// <summary>The running exe isn't a Velopack install (dev checkout or portable
    /// zip). Check / download paths can't proceed; user sees a targeted message
    /// rather than being misrouted to the ServerError fallback.</summary>
    NotInstalled,
}
