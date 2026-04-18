using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Provides a bearer JWT for authenticating to <c>api.wintty.io</c>.
/// D.2 ships <c>EnvTokenProvider</c> (reads <c>WINTTY_DEV_JWT</c>) as
/// the only impl. D.2.5 adds an <c>OAuthTokenProvider</c> backed by
/// a DPAPI-encrypted cache.
/// </summary>
public interface ISponsorTokenProvider
{
    /// <summary>
    /// Returns a bearer JWT or null if no sponsor session is available.
    /// Drivers treat null as "emit Error(NoToken)" — the user needs to
    /// sign in (D.2.5) or set <c>$env:WINTTY_DEV_JWT</c> (D.2 dev path).
    /// </summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops any cached token. <c>WinttyUpdateSource</c> calls this on
    /// a Worker 401 so the next <see cref="GetTokenAsync"/> can decide
    /// whether to re-auth. No-op for providers that don't cache
    /// (e.g. <c>EnvTokenProvider</c>, which treats the env var as
    /// authoritative for the process lifetime).
    /// </summary>
    void Invalidate();

    /// <summary>
    /// Fires when the current token is known to be invalid. The driver
    /// subscribes to surface a user-visible <c>Error(AuthExpired)</c>
    /// snapshot when invalidation happens outside a caller context
    /// (e.g. D.2.5's background refresh).
    /// </summary>
    event EventHandler? TokenInvalidated;
}
