using System;
using System.IO;
using Ghostty.Core;
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

    // No Directory.CreateDirectory here. Reading the path is not a reason to
    // mutate the filesystem, and the splash asks for it on the pre-XAML
    // thread whose whole job is to be on screen first. Save creates the
    // directory, because writing is the only operation that needs one.
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppIdentity.StateDirName);

    internal static string FilePath => Path.Combine(Dir, "session.json");

    /// <summary>
    /// Read and deserialize the session file, or null when there is none.
    /// Throws on an unreadable file; callers decide how to report it.
    /// </summary>
    /// <remarks>
    /// Static and logger-free so the pre-XAML splash can share it: that
    /// thread needs the same path and, more importantly, the same share
    /// mode, and it runs long before DI exists.
    ///
    /// FileShare.ReadWrite rather than File.ReadAllText's implicit
    /// FileShare.Read. The splash reads this file during startup while
    /// SessionManager.LoadForRestore is rewriting it to arm the dirty flag,
    /// and a reader that locks writers out makes that write fail -- silently,
    /// because Save swallows and logs. The cost of losing it is that a crash
    /// this run leaves the previous clean snapshot on disk for `default` to
    /// wrongly restore, which is the exact outcome the arming prevents.
    /// </remarks>
    internal static SessionState? ReadFile()
    {
        if (!File.Exists(FilePath)) return null;

        using var stream = new FileStream(
            FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return SessionSerializer.Deserialize(reader.ReadToEnd());
    }

    public SessionState? Load()
    {
        try
        {
            return ReadFile();
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
            Directory.CreateDirectory(Dir);
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
