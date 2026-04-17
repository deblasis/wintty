using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ghostty.Logging;

/// <summary>
/// Static accessor for <see cref="ILogger{T}"/> instances used by
/// <c>Ghostty</c>-project types whose call sites genuinely cannot
/// receive a logger through a constructor argument. Every remaining
/// entry here has a documented reason below; new components should
/// take <c>ILogger&lt;T&gt;</c> in their ctor via the <see cref="ILoggerFactory"/>
/// threaded through <see cref="App.OnLaunched"/>, not grow this class.
///
/// Populated once from <c>App.OnLaunched</c> via
/// <see cref="Initialize(ILoggerFactory)"/>. Before that call every
/// accessor returns <see cref="NullLogger{T}"/> (or
/// <see cref="NullLogger.Instance"/> for the string-category
/// <see cref="WindowStateMigration"/> entry), so early call sites
/// are safe no-ops.
///
/// Why each remaining site stays here:
///   - <see cref="App"/>: logs AUMID/jump-list failures in
///     <c>App.OnLaunched</c> BEFORE the factory is built. No ctor
///     available in the early-startup scope.
///   - <see cref="ConfigService"/>: constructed before the factory
///     exists because the factory reads <c>log-level</c> /
///     <c>log-filter</c> off <c>ConfigService</c>. Chicken-and-egg,
///     so ctor injection is impossible.
///   - <see cref="WindowStateMigration"/>: <c>static class</c>, so
///     there is no ctor to inject into.
///   - <see cref="WindowState"/>: data class deserialized by
///     <c>System.Text.Json</c> via a static <c>Load()</c> method.
///     Ctor injection would require threading a logger through
///     every deserialization site.
///   - <see cref="GeneralPage"/>: XAML page constructed by the
///     WinUI 3 Frame via a parameterless ctor; ctor injection
///     requires a DI-enabled <c>Frame</c> which is a larger
///     refactor than this slot is worth.
///
/// Tests should use <see cref="Install"/> which returns an
/// <see cref="IDisposable"/> scope that restores the pre-install
/// state on disposal.
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
    private static ILogger<Ghostty.Services.ConfigService>? _configService;

    // WindowStateMigration is a static class, so it uses the non-generic
    // ILogger with an explicit category name; see class docstring.
    private const string WindowStateMigrationCategory = "Ghostty.Settings.WindowStateMigration";
    private static ILogger? _windowStateMigration;
    private static ILogger<Ghostty.Settings.WindowState>? _windowState;
    private static ILogger<Ghostty.Settings.Pages.GeneralPage>? _generalPage;
    private static ILogger<App>? _app;

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
        _configService, _windowStateMigration, _windowState, _generalPage, _app);

    private readonly record struct Snapshot(
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
            _configService = _prior.ConfigService;
            _windowStateMigration = _prior.WindowStateMigration;
            _windowState = _prior.WindowState;
            _generalPage = _prior.GeneralPage;
            _app = _prior.App;
        }
    }
}
