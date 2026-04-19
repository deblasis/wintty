using System;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Typed failure from a Worker auth call or the OAuth loopback flow.
/// <c>OAuthTokenProvider</c> catches these and maps <see cref="Kind"/>
/// to <c>ISponsorTokenProvider</c> state transitions (silent-skip,
/// reschedule, fire TokenInvalidated).
/// </summary>
internal sealed class AuthException : Exception
{
    public AuthErrorKind Kind { get; }
    public string? Detail { get; }

    public AuthException(
        AuthErrorKind kind,
        string? detail,
        Exception? innerException = null)
        : base($"{kind}: {detail ?? "(no detail)"}", innerException)
    {
        Kind = kind;
        Detail = detail;
    }
}
