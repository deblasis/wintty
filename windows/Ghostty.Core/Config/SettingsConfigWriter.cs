using System;
using System.IO;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Config;

/// <summary>
/// Outcome of a <see cref="SettingsConfigWriter.Write"/> call.
/// <see cref="WriteSucceeded"/> is false when the editor mutation threw a
/// disk error (the file is unchanged or only partially written);
/// <see cref="Reloaded"/> is the result of the post-write
/// <see cref="IConfigService.Reload"/>; <see cref="Error"/> carries the
/// caught exception so a page with a status affordance can surface it.
/// <para>
/// <see cref="Reloaded"/> is independent of <see cref="WriteSucceeded"/>:
/// the reload runs even after a caught write failure, so a true
/// <see cref="Reloaded"/> means "the runtime re-read the file", not "the
/// write took effect". Callers that care whether the change landed must
/// check <see cref="WriteSucceeded"/> first.
/// </para>
/// </summary>
public readonly record struct SettingsWriteResult(
    bool WriteSucceeded,
    bool Reloaded,
    Exception? Error);

/// <summary>
/// Shared helper for the Settings pages' synchronous "immediate write"
/// handlers (toggles, sliders, color pickers, raw save). Each page used
/// to inline the same idiom:
/// <code>
/// SuppressWatcher(true);
/// try { editor.SetValue(...); }
/// finally { SuppressWatcher(false); }
/// Reload();
/// </code>
/// which leaves an <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
/// (file locked, disk full, permission denied) to propagate unhandled out
/// of the WinUI routed-event handler into <c>Application.UnhandledException</c>,
/// fail-fasting the whole process. This helper keeps the watcher-balancing
/// and reload behavior but catches those disk errors, logs them, and
/// reports the outcome so the page can keep running (and optionally show a
/// message). It is the synchronous sibling of
/// <see cref="ConfigWriteScheduler"/>, which already owns this discipline
/// for debounced writes.
/// </summary>
public sealed partial class SettingsConfigWriter
{
    private readonly IConfigService _configService;
    private readonly ILogger<SettingsConfigWriter> _logger;

    public SettingsConfigWriter(
        IConfigService configService,
        ILogger<SettingsConfigWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(logger);
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Run <paramref name="edit"/> (the editor mutation) with the file
    /// watcher suppressed, catching disk failures, then reload. The
    /// watcher flag is always balanced via <c>finally</c>, even when the
    /// edit throws.
    /// </summary>
    /// <param name="edit">The editor mutation (SetValue / RemoveValue /
    /// WriteRaw / SetRepeatableValues, or a conditional combination).</param>
    /// <param name="context">Optional human-readable label for what is
    /// being written (typically the config key) so a caught failure names
    /// the affected setting in the log. The <see cref="IOException"/>
    /// usually carries the file path but not which setting failed.</param>
    /// <remarks>
    /// Only <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>
    /// are caught -- the realistic disk-write failures. Any other exception
    /// is a programming error and is left to propagate rather than be
    /// silently swallowed; the watcher flag is still reset on the way out.
    /// The reload runs after a caught write failure too, so the live runtime
    /// resyncs to whatever actually made it to disk (mirroring
    /// <see cref="ConfigWriteScheduler"/>, which signals a reload even after
    /// a failed batch item).
    /// </remarks>
    public SettingsWriteResult Write(Action edit, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(edit);

        Exception? error = null;
        _configService.SuppressWatcher(true);
        try
        {
            edit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            LogWriteFailed(ex, context ?? "(unspecified)");
        }
        finally
        {
            _configService.SuppressWatcher(false);
        }

        var reloaded = _configService.Reload();
        return new SettingsWriteResult(error is null, reloaded, error);
    }

    [LoggerMessage(EventId = LogEvents.Config.SettingsWriteErr,
                   Level = LogLevel.Warning,
                   Message = "[SettingsConfigWriter] config write failed for {Context}")]
    private partial void LogWriteFailed(Exception ex, string context);
}
