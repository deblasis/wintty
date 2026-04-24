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

    // 1200-1299: Profiles / discovery
    internal static class Profiles
    {
        public const int ProbeFailed             = 1200;
        public const int CacheReadFailed         = 1201;
        public const int CacheWriteFailed        = 1202;
        public const int RegistryRecomposed      = 1203;
        public const int DiscoveryRefreshFailed  = 1204;
        public const int ProfileParseWarning     = 1205;
    }
}
