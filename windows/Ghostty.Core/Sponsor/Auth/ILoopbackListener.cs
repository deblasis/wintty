using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Abstraction over the ephemeral <c>http://127.0.0.1:N/cb</c>
/// listener that receives the OAuth callback. Production impl uses
/// <see cref="System.Net.HttpListener"/>; tests drive the provider with
/// pre-baked <see cref="LoopbackResult"/> values.
/// </summary>
internal interface ILoopbackListener : IDisposable
{
    /// <summary>
    /// Port the listener is bound to. Set after <see cref="Start"/>.
    /// Read by <c>OAuthTokenProvider</c> to construct the redirect URL.
    /// </summary>
    int Port { get; }

    /// <summary>
    /// Binds an ephemeral port on <c>127.0.0.1</c>. Throws on bind
    /// failure. Must be called exactly once per listener instance.
    /// </summary>
    void Start();

    /// <summary>
    /// Awaits the first incoming request on <c>/cb</c>, parses query
    /// params, and returns a <see cref="LoopbackResult"/>. On cancellation,
    /// throws <see cref="OperationCanceledException"/>. Unknown paths
    /// return a 404 and keep listening.
    /// </summary>
    Task<LoopbackResult> AwaitCallbackAsync(CancellationToken ct);
}

/// <summary>
/// Parsed query-string payload from the <c>/cb</c> request. Exactly one
/// of <see cref="Token"/> / <see cref="Error"/> is non-null on a
/// well-formed callback; both null indicates a malformed request and
/// <c>OAuthTokenProvider</c> treats it as a silent failure.
/// </summary>
internal sealed record LoopbackResult(string? Token, string? Nonce, string? Error);
