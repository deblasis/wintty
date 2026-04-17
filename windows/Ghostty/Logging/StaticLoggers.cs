using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ghostty.Logging;

/// <summary>
/// Static accessor for <see cref="ILogger{T}"/> instances used by
/// <c>Ghostty</c>-project types whose call sites are static methods
/// or whose construction happens before the logger factory exists
/// (see <c>ConfigService</c>, which the factory reads its filter
/// config from at build time).
///
/// Populated once from <c>App.OnLaunched</c> via
/// <see cref="Initialize(ILoggerFactory)"/>. Before that call every
/// accessor returns <see cref="NullLogger{T}"/> (or
/// <see cref="NullLogger.Instance"/> for the string-category
/// <see cref="WindowStateMigration"/> entry), so early call sites
/// are safe no-ops.
///
/// Tests (if any are added in the future) should use
/// <see cref="Install"/> which returns an <see cref="IDisposable"/>
/// scope that restores the pre-install state on disposal.
///
/// Note: <c>Ghostty.Settings.WindowStateMigration</c> is a
/// <c>static class</c> so it cannot appear as a type argument to
/// <see cref="ILogger{T}"/>. Its accessor uses the non-generic
/// <see cref="ILogger"/> bound to the same category name the
/// generic path would have produced, so filter rules keyed on
/// "Ghostty.Settings.WindowStateMigration" behave identically.
/// </summary>
internal static class StaticLoggers
{
    // Phase 3 (this file's initial population)
    private static ILogger<Ghostty.Clipboard.WinUiClipboardBackend>? _winUiClipboardBackend;
    private static ILogger<Ghostty.Clipboard.DialogClipboardConfirmer>? _dialogClipboardConfirmer;
    private static ILogger<Ghostty.Hosting.ClipboardBridge>? _clipboardBridge;
    private static ILogger<Ghostty.MainWindow>? _mainWindow;
    private static ILogger<Ghostty.Shell.TaskbarHost>? _taskbarHost;
    private static ILogger<Ghostty.Shell.AcrylicBackdrop>? _acrylicBackdrop;
    private static ILogger<Ghostty.Services.ThemePreviewService>? _themePreviewService;
    private static ILogger<Ghostty.Services.ConfigService>? _configService;

    // Phase 4 (prefilled so Phase 4 is a drop-in migration).
    // WindowStateMigration is a static class, so it uses the non-generic
    // ILogger with an explicit category name; see class docstring.
    private const string WindowStateMigrationCategory = "Ghostty.Settings.WindowStateMigration";
    private static ILogger? _windowStateMigration;
    private static ILogger<Ghostty.Settings.WindowState>? _windowState;
    private static ILogger<Ghostty.Settings.Pages.GeneralPage>? _generalPage;
    private static ILogger<App>? _app;

    internal static ILogger<Ghostty.Clipboard.WinUiClipboardBackend> WinUiClipboardBackend
        => _winUiClipboardBackend ?? NullLogger<Ghostty.Clipboard.WinUiClipboardBackend>.Instance;
    internal static ILogger<Ghostty.Clipboard.DialogClipboardConfirmer> DialogClipboardConfirmer
        => _dialogClipboardConfirmer ?? NullLogger<Ghostty.Clipboard.DialogClipboardConfirmer>.Instance;
    internal static ILogger<Ghostty.Hosting.ClipboardBridge> ClipboardBridge
        => _clipboardBridge ?? NullLogger<Ghostty.Hosting.ClipboardBridge>.Instance;
    internal static ILogger<Ghostty.MainWindow> MainWindow
        => _mainWindow ?? NullLogger<Ghostty.MainWindow>.Instance;
    internal static ILogger<Ghostty.Shell.TaskbarHost> TaskbarHost
        => _taskbarHost ?? NullLogger<Ghostty.Shell.TaskbarHost>.Instance;
    internal static ILogger<Ghostty.Shell.AcrylicBackdrop> AcrylicBackdrop
        => _acrylicBackdrop ?? NullLogger<Ghostty.Shell.AcrylicBackdrop>.Instance;
    internal static ILogger<Ghostty.Services.ThemePreviewService> ThemePreviewService
        => _themePreviewService ?? NullLogger<Ghostty.Services.ThemePreviewService>.Instance;
    internal static ILogger<Ghostty.Services.ConfigService> ConfigService
        => _configService ?? NullLogger<Ghostty.Services.ConfigService>.Instance;
    internal static ILogger WindowStateMigration
        => _windowStateMigration ?? NullLogger.Instance;
    internal static ILogger<Ghostty.Settings.WindowState> WindowState
        => _windowState ?? NullLogger<Ghostty.Settings.WindowState>.Instance;
    internal static ILogger<Ghostty.Settings.Pages.GeneralPage> GeneralPage
        => _generalPage ?? NullLogger<Ghostty.Settings.Pages.GeneralPage>.Instance;
    internal static ILogger<App> App
        => _app ?? NullLogger<App>.Instance;

    internal static void Initialize(ILoggerFactory factory)
    {
        _winUiClipboardBackend = factory.CreateLogger<Ghostty.Clipboard.WinUiClipboardBackend>();
        _dialogClipboardConfirmer = factory.CreateLogger<Ghostty.Clipboard.DialogClipboardConfirmer>();
        _clipboardBridge = factory.CreateLogger<Ghostty.Hosting.ClipboardBridge>();
        _mainWindow = factory.CreateLogger<Ghostty.MainWindow>();
        _taskbarHost = factory.CreateLogger<Ghostty.Shell.TaskbarHost>();
        _acrylicBackdrop = factory.CreateLogger<Ghostty.Shell.AcrylicBackdrop>();
        _themePreviewService = factory.CreateLogger<Ghostty.Services.ThemePreviewService>();
        _configService = factory.CreateLogger<Ghostty.Services.ConfigService>();
        _windowStateMigration = factory.CreateLogger(WindowStateMigrationCategory);
        _windowState = factory.CreateLogger<Ghostty.Settings.WindowState>();
        _generalPage = factory.CreateLogger<Ghostty.Settings.Pages.GeneralPage>();
        _app = factory.CreateLogger<App>();
    }

    internal static IDisposable Install(ILoggerFactory factory)
    {
        var prior = CaptureSnapshot();
        Initialize(factory);
        return new Scope(prior);
    }

    private static Snapshot CaptureSnapshot() => new(
        _winUiClipboardBackend, _dialogClipboardConfirmer, _clipboardBridge,
        _mainWindow, _taskbarHost, _acrylicBackdrop,
        _themePreviewService, _configService,
        _windowStateMigration, _windowState, _generalPage, _app);

    private readonly record struct Snapshot(
        ILogger<Ghostty.Clipboard.WinUiClipboardBackend>? WinUiClipboardBackend,
        ILogger<Ghostty.Clipboard.DialogClipboardConfirmer>? DialogClipboardConfirmer,
        ILogger<Ghostty.Hosting.ClipboardBridge>? ClipboardBridge,
        ILogger<Ghostty.MainWindow>? MainWindow,
        ILogger<Ghostty.Shell.TaskbarHost>? TaskbarHost,
        ILogger<Ghostty.Shell.AcrylicBackdrop>? AcrylicBackdrop,
        ILogger<Ghostty.Services.ThemePreviewService>? ThemePreviewService,
        ILogger<Ghostty.Services.ConfigService>? ConfigService,
        ILogger? WindowStateMigration,
        ILogger<Ghostty.Settings.WindowState>? WindowState,
        ILogger<Ghostty.Settings.Pages.GeneralPage>? GeneralPage,
        ILogger<App>? App);

    private sealed class Scope : IDisposable
    {
        private readonly Snapshot _prior;
        public Scope(Snapshot prior) => _prior = prior;

        public void Dispose()
        {
            _winUiClipboardBackend = _prior.WinUiClipboardBackend;
            _dialogClipboardConfirmer = _prior.DialogClipboardConfirmer;
            _clipboardBridge = _prior.ClipboardBridge;
            _mainWindow = _prior.MainWindow;
            _taskbarHost = _prior.TaskbarHost;
            _acrylicBackdrop = _prior.AcrylicBackdrop;
            _themePreviewService = _prior.ThemePreviewService;
            _configService = _prior.ConfigService;
            _windowStateMigration = _prior.WindowStateMigration;
            _windowState = _prior.WindowState;
            _generalPage = _prior.GeneralPage;
            _app = _prior.App;
        }
    }
}
