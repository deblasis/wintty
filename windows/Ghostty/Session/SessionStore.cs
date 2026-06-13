using System;
using System.IO;
using Ghostty.Core.Session;
using Ghostty.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Session;

/// <summary>
/// File-backed persistence for <see cref="SessionState"/> at
/// <c>%APPDATA%\Wintty\session.json</c>. Thin wrapper over the pure
/// <see cref="SessionSerializer"/>; a malformed/inaccessible file never
/// blocks startup.
/// </summary>
internal sealed class SessionStore
{
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(ILogger<SessionStore> logger) => _logger = logger;

    private static string Dir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Wintty");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    internal static string FilePath => Path.Combine(Dir, "session.json");

    public SessionState? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? SessionSerializer.Deserialize(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogSessionLoadFailed(ex);
            return null;
        }
    }

    public void Save(SessionState state)
    {
        try
        {
            File.WriteAllText(FilePath, SessionSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            _logger.LogSessionSaveFailed(ex);
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogSessionDeleteFailed(ex);
        }
    }
}

internal static partial class SessionStoreLogExtensions
{
    [LoggerMessage(EventId = LogEvents.Session.LoadFailed,
                   Level = LogLevel.Warning, Message = "Session load failed, ignoring saved session")]
    internal static partial void LogSessionLoadFailed(this ILogger<SessionStore> logger, Exception ex);

    [LoggerMessage(EventId = LogEvents.Session.SaveFailed,
                   Level = LogLevel.Warning, Message = "Session save failed")]
    internal static partial void LogSessionSaveFailed(this ILogger<SessionStore> logger, Exception ex);

    [LoggerMessage(EventId = LogEvents.Session.DeleteFailed,
                   Level = LogLevel.Warning, Message = "Session delete failed")]
    internal static partial void LogSessionDeleteFailed(this ILogger<SessionStore> logger, Exception ex);
}
