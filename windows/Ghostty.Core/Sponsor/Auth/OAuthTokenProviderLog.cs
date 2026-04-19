using System;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Sponsor.Auth;

internal sealed partial class OAuthTokenProvider
{
    [LoggerMessage(EventId = LogEvents.Auth.ReactiveRefreshSucceeded,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] reactive refresh succeeded")]
    private partial void LogReactiveRefreshSucceeded();

    [LoggerMessage(EventId = LogEvents.Auth.ReactiveRefreshRejected,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] reactive refresh rejected; clearing cache")]
    private partial void LogReactiveRefreshRejected();

    [LoggerMessage(EventId = LogEvents.Auth.ReactiveRefreshTransient,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] reactive refresh transient failure")]
    private partial void LogReactiveRefreshTransient(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.ReactiveRefreshUnexpected,
                   Level = LogLevel.Error,
                   Message = "[sponsor/auth] reactive refresh unexpected failure")]
    private partial void LogReactiveRefreshUnexpected(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.DevJwtLoaded,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] using WINTTY_DEV_JWT (expires {Exp})")]
    private partial void LogDevJwtLoaded(DateTime exp);

    [LoggerMessage(EventId = LogEvents.Auth.DevJwtMalformed,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] WINTTY_DEV_JWT is malformed; ignoring")]
    private partial void LogDevJwtMalformed(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.StoreReadFailed,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] JWT store read failed")]
    private partial void LogStoreReadFailed(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.NoCachedJwt,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] no cached JWT; sign-in required")]
    private partial void LogNoCachedJwt();

    [LoggerMessage(EventId = LogEvents.Auth.CachedJwtMalformed,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] cached JWT malformed; deleting")]
    private partial void LogCachedJwtMalformed(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.CachedJwtStale,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] cached JWT stale by more than 24h; deleting")]
    private partial void LogCachedJwtStale();

    [LoggerMessage(EventId = LogEvents.Auth.BootRefreshSucceeded,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] refreshed expired JWT on boot")]
    private partial void LogBootRefreshSucceeded();

    [LoggerMessage(EventId = LogEvents.Auth.BootRefreshRejected,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] expired JWT rejected on refresh; deleting")]
    private partial void LogBootRefreshRejected();

    [LoggerMessage(EventId = LogEvents.Auth.BootRefreshFailed,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] refresh failed on boot; keeping nothing cached")]
    private partial void LogBootRefreshFailed(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.LoadedCachedJwt,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] loaded cached JWT (expires {Exp})")]
    private partial void LogLoadedCachedJwt(DateTime exp);

    [LoggerMessage(EventId = LogEvents.Auth.StoreDeleteFailed,
                   Level = LogLevel.Debug,
                   Message = "[sponsor/auth] store delete failed")]
    private partial void LogStoreDeleteFailed(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.ProactiveRefreshSucceeded,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] proactive refresh succeeded")]
    private partial void LogProactiveRefreshSucceeded();

    [LoggerMessage(EventId = LogEvents.Auth.ProactiveRefreshRejected,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] proactive refresh rejected; clearing")]
    private partial void LogProactiveRefreshRejected();

    [LoggerMessage(EventId = LogEvents.Auth.ProactiveRefreshTransient,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] proactive refresh transient; retry in 10m")]
    private partial void LogProactiveRefreshTransient(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.ProactiveRefreshUnexpected,
                   Level = LogLevel.Error,
                   Message = "[sponsor/auth] proactive refresh unexpected failure")]
    private partial void LogProactiveRefreshUnexpected(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.RevokeAcknowledged,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] revoke acknowledged")]
    private partial void LogRevokeAcknowledged();

    [LoggerMessage(EventId = LogEvents.Auth.RevokeBestEffortFailed,
                   Level = LogLevel.Debug,
                   Message = "[sponsor/auth] revoke best-effort failed")]
    private partial void LogRevokeBestEffortFailed(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.LoopbackBindFailed,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] loopback bind failed")]
    private partial void LogLoopbackBindFailed(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.SignInTimedOut,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] sign-in timed out or cancelled")]
    private partial void LogSignInTimedOut();

    [LoggerMessage(EventId = LogEvents.Auth.OAuthErrorQuery,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] OAuth error query ({Length} chars)")]
    private partial void LogOAuthErrorQuery(int length);

    [LoggerMessage(EventId = LogEvents.Auth.NonceMismatch,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] nonce mismatch on callback")]
    private partial void LogNonceMismatch();

    [LoggerMessage(EventId = LogEvents.Auth.CallbackMissingToken,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] callback missing token")]
    private partial void LogCallbackMissingToken();

    [LoggerMessage(EventId = LogEvents.Auth.CallbackMalformedJwt,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] callback returned malformed JWT")]
    private partial void LogCallbackMalformedJwt(Exception ex);

    [LoggerMessage(EventId = LogEvents.Auth.CallbackJwtPreExpired,
                   Level = LogLevel.Warning,
                   Message = "[sponsor/auth] callback JWT is pre-expired")]
    private partial void LogCallbackJwtPreExpired();

    [LoggerMessage(EventId = LogEvents.Auth.SignInComplete,
                   Level = LogLevel.Information,
                   Message = "[sponsor/auth] sign-in complete (exp {Exp})")]
    private partial void LogSignInComplete(DateTime exp);
}
