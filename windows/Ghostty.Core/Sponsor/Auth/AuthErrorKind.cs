namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Coarse failure categories for Worker auth calls (refresh / revoke)
/// and the OAuth handshake. Mirrors <see cref="Ghostty.Core.Sponsor.Update.UpdateErrorKind"/>
/// style from D.2 so the token provider can translate at the driver boundary.
/// </summary>
internal enum AuthErrorKind
{
    /// <summary>DNS, TCP, TLS, or timeout failure reaching the Worker.</summary>
    Network,

    /// <summary>Worker returned 401/403 or the token has been revoked.</summary>
    Unauthorized,

    /// <summary>Worker returned 5xx or malformed response.</summary>
    ServerError,

    /// <summary>Protocol-shape violation or invariant failure (nonce mismatch, malformed JWT).</summary>
    Unknown,
}
