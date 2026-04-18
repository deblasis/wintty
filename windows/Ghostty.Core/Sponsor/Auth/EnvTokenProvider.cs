using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Development-only <see cref="ISponsorTokenProvider"/> that reads the
/// JWT from an environment variable once at construction and caches it
/// for the process lifetime. Intended for local smoke testing against
/// <c>api.wintty.io</c> using a token minted by
/// <c>wintty-release/worker/scripts/smoke-mint-jwt.ts</c>.
///
/// Never ships to end users. D.2.5 adds <c>OAuthTokenProvider</c>
/// which runs the interactive loopback flow and persists via DPAPI.
/// </summary>
public sealed class EnvTokenProvider : ISponsorTokenProvider
{
    private readonly string? _cachedToken;

    public EnvTokenProvider(string environmentVariableName = "WINTTY_DEV_JWT")
    {
        _cachedToken = Environment.GetEnvironmentVariable(environmentVariableName);
    }

    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_cachedToken);

    public void Invalidate()
    {
        // No-op: the env var is authoritative for the process lifetime.
        // Re-reading it won't help — the dev needs to fix the var and
        // relaunch. D.2.5's OAuth provider implements this meaningfully.
    }

#pragma warning disable CS0067 // Event is never used: that's the contract.
    public event EventHandler? TokenInvalidated;
#pragma warning restore CS0067
}
