namespace Ghostty.Logging;

/// <summary>
/// EventId constants for components resident in the WinUI shell
/// (<c>Ghostty</c> project). Disjoint from <c>Ghostty.Core.Logging.LogEvents</c>.
/// </summary>
internal static class LogEvents
{
    // 2000-2099: Startup
    internal static class Startup
    {
        public const int AumidFailed    = 2000;
        public const int JumpListFailed = 2001;
        public const int ToastRegisterFailed = 2002;
    }

    // 2100-2199: Clipboard
    internal static class Clipboard
    {
        public const int ReadFailed         = 2100;
        public const int WriteFailed        = 2101;
        public const int WriteRetryFailed   = 2102;
        public const int ConfirmDialogErr   = 2103; // DialogClipboardConfirmer
        public const int ReadHandlerErr     = 2104; // ClipboardBridge
        public const int ConfirmHandlerErr  = 2105; // ClipboardBridge
        public const int WriteHandlerErr    = 2106; // ClipboardBridge
    }

    // 2200-2299: ThemePreview
    internal static class ThemePreview
    {
        public const int PipeWaiting       = 2200;
        public const int ClientConnected   = 2201;
        public const int PreviewCancelled  = 2202;
        public const int PreviewConfirmed  = 2203;
        public const int PipeError         = 2204;
        public const int InvalidThemeName  = 2205;
        public const int PipeServerUnavailable = 2206;
    }

    // 2300-2399: WindowState + migration
    internal static class WindowState
    {
        public const int LoadFailed                    = 2300;
        public const int SaveFailed                    = 2301;
        public const int MigrationFailed               = 2302;
        public const int MigrationLegacyDeleteFailed   = 2303;
        public const int MigrationScanFailed           = 2304;
    }

    // 2400-2499: Shell (taskbar, backdrop)
    internal static class Shell
    {
        public const int TaskbarWiringFailed      = 2400;
        public const int AcrylicDefaultConfigFired = 2401;
    }

    // 2500-2599: MainWindow
    internal static class MainWindow
    {
        public const int ConfigOpenFailed  = 2500;
        public const int DialogDrainFailed = 2501;
    }

    // 2600-2699: Settings UI
    internal static class SettingsUi
    {
        public const int ConfigOpenFailed   = 2600;
        public const int KeybindWriteFailed = 2601;
        public const int CheatSheetShowFailed = 2602;
        public const int CheatSheetExportFailed = 2603;
    }

    // 2700-2799: Notifications
    internal static class Notifications
    {
        public const int ShowFailed  = 2701;
        public const int ClearFailed = 2702;
    }

    // 2800-2899: Session restoration
    internal static class Session
    {
        public const int LoadFailed   = 2800;
        public const int SaveFailed   = 2801;
        public const int DeleteFailed = 2802;
    }

    // 2900-2999: Single-instance mode
    internal static class SingleInstance
    {
        public const int PipeUnavailable   = 2900;
        public const int PipeError         = 2901;
        public const int BadPayload        = 2902;
        public const int MutexFailed       = 2903;
        public const int ForwardFailed     = 2904;
        public const int ServerStartFailed = 2905;
    }
}
