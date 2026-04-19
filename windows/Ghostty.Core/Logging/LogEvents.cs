namespace Ghostty.Core.Logging;

/// <summary>
/// EventId constants for <c>Ghostty.Core</c>-resident components.
/// Id ranges are disjoint per component. Each id appears in exactly two
/// places: the definition here and one <c>[LoggerMessage(EventId = ...)]</c>
/// attribute - `grep` for the constant name should return exactly two hits.
/// </summary>
internal static class LogEvents
{
    // 1000-1099: Config
    internal static class Config
    {
        public const int ReloadFailed      = 1000; // reserved, populated in Phase 3 (ConfigService)
        public const int WriteSchedulerErr = 1001;
        public const int TimerDisposeSlow  = 1002;
    }

    // 1100-1199: Frecency / command history
    internal static class Frecency
    {
        public const int ParseFailed = 1100;
        public const int LoadFailed  = 1101;
        public const int SaveFailed  = 1102;
    }

    // 1200-1299: Sponsor/Auth (OAuthTokenProvider)
    internal static class Auth
    {
        public const int ReactiveRefreshSucceeded    = 1200;
        public const int ReactiveRefreshRejected     = 1201;
        public const int ReactiveRefreshTransient    = 1202;
        public const int ReactiveRefreshUnexpected   = 1203;
        public const int DevJwtLoaded                = 1204;
        public const int DevJwtMalformed             = 1205;
        public const int StoreReadFailed             = 1206;
        public const int NoCachedJwt                 = 1207;
        public const int CachedJwtMalformed          = 1208;
        public const int CachedJwtStale              = 1209;
        public const int BootRefreshSucceeded        = 1210;
        public const int BootRefreshRejected         = 1211;
        public const int BootRefreshFailed           = 1212;
        public const int LoadedCachedJwt             = 1213;
        public const int StoreDeleteFailed           = 1214;
        public const int ProactiveRefreshSucceeded   = 1215;
        public const int ProactiveRefreshRejected    = 1216;
        public const int ProactiveRefreshTransient   = 1217;
        public const int ProactiveRefreshUnexpected  = 1218;
        public const int RevokeAcknowledged          = 1219;
        public const int RevokeBestEffortFailed      = 1220;
        public const int LoopbackBindFailed          = 1221;
        public const int SignInTimedOut              = 1222;
        public const int OAuthErrorQuery             = 1223;
        public const int NonceMismatch               = 1224;
        public const int CallbackMissingToken        = 1225;
        public const int CallbackMalformedJwt        = 1226;
        public const int CallbackJwtPreExpired       = 1227;
        public const int SignInComplete              = 1228;
    }
}
