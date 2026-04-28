using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ghostty.Core.Logging;

/// <summary>
/// Static accessor for <see cref="ILogger{T}"/> instances used by
/// Core types whose call sites are static methods or otherwise
/// cannot receive a logger through a constructor argument. Before
/// <see cref="Initialize(ILoggerFactory)"/> is called, all accessors
/// return <see cref="NullLogger{T}"/>, so early / test call sites are
/// safe no-ops.
///
/// Tests should use <see cref="Install"/> instead of Initialize so
/// the static state is restored to the pre-test NullLogger baseline
/// on scope disposal. Without that, test-owned CapturingLoggerProvider
/// factories get disposed while CoreStaticLoggers still holds their
/// loggers, producing stale captures or ObjectDisposedException in any
/// subsequent test that transitively triggers a Core log site.
/// </summary>
internal static class CoreStaticLoggers
{
    private static ILogger<Ghostty.Commands.FrecencyStore>? _frecencyStore;

    internal static ILogger<Ghostty.Commands.FrecencyStore> FrecencyStore
        => _frecencyStore ?? NullLogger<Ghostty.Commands.FrecencyStore>.Instance;

    /// <summary>
    /// Populate the static accessors from the process-wide factory.
    /// Safe to call more than once - each call atomically swaps the
    /// cached loggers. Used by <c>App.OnLaunched</c> in production.
    /// </summary>
    internal static void Initialize(ILoggerFactory factory)
    {
        _frecencyStore = factory.CreateLogger<Ghostty.Commands.FrecencyStore>();
    }

    /// <summary>
    /// Test-side scope: installs loggers from <paramref name="factory"/>
    /// and returns an <see cref="IDisposable"/> that restores the static
    /// accessors to their pre-install state on disposal. Use with
    /// <c>using var _ = CoreStaticLoggers.Install(factory);</c> in tests.
    /// </summary>
    internal static IDisposable Install(ILoggerFactory factory)
    {
        var priorFrecencyStore = _frecencyStore;
        Initialize(factory);
        return new Scope(priorFrecencyStore);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ILogger<Ghostty.Commands.FrecencyStore>? _priorFrecencyStore;

        public Scope(ILogger<Ghostty.Commands.FrecencyStore>? priorFrecencyStore)
        {
            _priorFrecencyStore = priorFrecencyStore;
        }

        public void Dispose()
        {
            _frecencyStore = _priorFrecencyStore;
        }
    }
}
