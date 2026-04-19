using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Abstraction over JWT persistence. Production impl is
/// <c>DpapiJwtStore</c> (Windows-only); tests use in-memory fakes.
/// Implementations MUST perform atomic writes (write to a temp file +
/// rename) so a crash mid-write leaves the previous blob intact.
/// The contract is "return bytes of the persisted JWT as UTF-8" -
/// impls that encrypt at rest MUST decrypt before returning.
/// </summary>
internal interface IJwtStore
{
    /// <summary>Returns the stored UTF-8 JWT bytes, or <c>null</c> if no blob exists.</summary>
    Task<byte[]?> ReadAsync(CancellationToken ct);

    /// <summary>Persists the UTF-8 encoded JWT. Throws on IO failure.</summary>
    Task WriteAsync(byte[] utf8Token, CancellationToken ct);

    /// <summary>Deletes the stored blob. No-op if absent. Throws on
    /// filesystem failures; callers log once via
    /// <c>OAuthTokenProvider.SafeDeleteAsync</c>.</summary>
    Task DeleteAsync(CancellationToken ct);
}
