using System;
using System.IO;
using System.Threading;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Config;

/// <summary>
/// Watches the config file for edits and reports each settled edit once,
/// through <c>onSettled</c>, after a trailing-edge debounce.
///
/// Built for the way editors actually save. Most do not rewrite the file in
/// place: they write a temp file and then swap it in, by deleting the
/// original and renaming the temp over it, by <c>ReplaceFile</c> (which
/// renames the original away first), or by renaming the original to a backup
/// and writing a fresh file under the old name. Each of those passes through
/// a moment where the config file does not exist, and each raises a burst of
/// watcher events (Deleted, Renamed away, Renamed in, Changed) rather than
/// one.
///
/// How wide that moment is, measured on this platform at 150 saves per shape
/// and roughly 40k existence samples each: rename-away-then-create is the
/// widest at 22.0ms, <c>ReplaceFile</c> 21.2ms, delete-then-rename 8.4ms.
///
/// A single <c>MoveFileEx</c> with REPLACE_EXISTING, which is
/// <c>File.Move</c> with overwrite and what this app's own writes use, is NOT
/// one of those shapes: it leaves no gap at all, 0 of 150 saves observed
/// absent. It was named here as a gap producer until the measurement said
/// otherwise, so read the list above as the measured set rather than as
/// every swap that exists.
///
/// Two rules follow from that:
///
/// Every event kind re-arms the debounce, Deleted included, so the whole
/// burst collapses into a single settle and nothing is reported between the
/// two halves of a swap.
///
/// A settle that finds the file missing reports no edit. The missing file is
/// not the end of the save: the rename that completes it raises its own
/// event, which re-arms the debounce, and that settle finds the file and
/// reports it. A reload in the gap would have loaded no user config at all,
/// so this saves the host a rebuild it would only decline.
///
/// It does report the absence, through <c>onVanished</c>, and that report is
/// NOT evidence of a deletion on its own. The quiet period the debounce buys
/// belongs to the settle, while the existence check runs later still, inside
/// the posted delivery, so an ordinary atomic save reaches it whenever the
/// swap straddles that hop. <c>onVanished</c> means "gone as of this
/// delivery" and nothing more, and a host acting on one observation of it
/// lowers what it knows in the middle of a save (issue #1146). It is
/// reported at all because the host is otherwise left refusing every later
/// reload to protect a save that is never going to land (issue #676).
///
/// It used to be load-bearing rather than an early-out, because libghostty's
/// default-file loader answered "no config file" by writing its template, at
/// the path the save was about to land on. The check and the load are two
/// steps, so this could only narrow that window; closing it needed a loader
/// that creates nothing, which is what
/// <c>ghostty_config_load_default_files</c> now is (issue #676).
///
/// The existence check runs where the config is loaded, not on the timer: a
/// settle is handed to <c>post</c>, and the check runs inside the posted
/// delivery, immediately before <c>onSettled</c>.
///
/// The other way editors save, rewriting in place, leaves the file present
/// and zero bytes for the length of the write, and nothing here guards that.
/// See issue #1138: it is a separate defect with a separate mechanism, and
/// two attempts at fixing it from this class made it measurably worse.
///
/// The watcher is directory-scoped (the file name is only its filter), so
/// the file being deleted and replaced does not stop it. Two kinds of
/// <see cref="FileSystemWatcher.Error"/> are handled differently:
///
/// An internal buffer overflow leaves the watcher running but may have
/// dropped the event that carried the save, so it re-arms the debounce.
/// One burst can raise dozens of them; the first per settle is a warning,
/// the rest are debug.
///
/// Any other error (the watched directory deleted, a share gone) ends the
/// watcher for good, even though it still reads as enabled inside the
/// handler. So the handler only marks it failed and schedules a rebuild on
/// the timer; the rebuild runs after the handler has returned, disposes
/// the failed watcher and builds a new one. While the directory is missing
/// it retries with a doubling delay capped at <see cref="MaxRebuildDelay"/>,
/// until the directory comes back or this is disposed. A successful rebuild
/// settles once, since a save may have landed while nothing was watching,
/// and a failed one still delivers its tick: while the directory is missing
/// that tick is the only thing that can report the file gone, and without
/// it a deleted directory is unconfirmable, with every later reload
/// declined for the life of the process (issue #676).
/// </summary>
public sealed partial class ConfigFileWatcher : IDisposable
{
    /// <summary>Longest wait between two attempts to rebuild a failed
    /// watcher while its directory is missing.</summary>
    internal static readonly TimeSpan MaxRebuildDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MinRebuildDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _path;
    private readonly string? _dir;
    private readonly string? _file;
    private readonly ISchedulerTimer _timer;
    private readonly TimeSpan _debounce;
    private readonly Func<bool> _ignoreEvents;
    private readonly Action<Action> _post;
    private readonly Action _onSettled;
    private readonly Action? _onVanished;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private FileSystemWatcher? _watcher;
    private bool _rebuildPending;
    private TimeSpan _rebuildDelay;
    private bool _overflowLogged;
    private bool _disposed;

    /// <summary>The watcher <see cref="CreateWatcher"/> is enabling right
    /// now, so <see cref="HandleWatcherError"/> can recognize a synchronous
    /// failure raised from inside that call, before the watcher is
    /// published anywhere. Written only while <see cref="_lock"/> is held
    /// by <see cref="CreateWatcher"/>'s caller, and read only under the
    /// same lock in <see cref="HandleWatcherError"/>: an async error can
    /// still land on another thread during that window, and without the
    /// lock that read would race the write.</summary>
    private FileSystemWatcher? _startingWatcher;
    private Exception? _startFailure;

    /// <summary>Test-only replacement for <c>watcher.EnableRaisingEvents =
    /// true</c>, so a test can simulate the synchronous <see
    /// cref="FileSystemWatcher.Error"/> the real implementation raises on
    /// the calling thread when the underlying watch fails immediately (an
    /// unsupported filesystem, or the directory vanishing right there).
    /// Null in production.</summary>
    internal Action<FileSystemWatcher>? TestEnable;

    /// <param name="path">The config file. Its directory must exist for
    /// <see cref="Start"/> to arm anything.</param>
    /// <param name="timer">Debounce and rebuild timer. Owned: disposed with
    /// this.</param>
    /// <param name="debounce">Quiet period after the last event.</param>
    /// <param name="ignoreEvents">Consulted when each event arrives, not when
    /// the debounce fires, so a write the host brackets with a suppression
    /// flag stays ignored even though the settle would land after the flag
    /// is lowered.</param>
    /// <param name="post">Called on the timer's thread with the delivery of
    /// one settle. Run it on the thread that loads the config (the UI
    /// dispatcher); it does nothing else on the timer's thread.</param>
    /// <param name="onSettled">Called from inside the posted delivery, once
    /// per settled edit, only if the file exists at that moment.</param>
    /// <param name="onVanished">Called instead of <paramref name="onSettled"/>
    /// when the delivery finds the file gone. It says "gone as of this
    /// delivery" and no more than that: an ordinary atomic save reaches it
    /// whenever the swap straddles the hop between the timer and the posted
    /// delivery, which is measured rather than theoretical. Optional, and
    /// the watcher does nothing else about it; the host decides what a
    /// missing config file means and is expected to ask again before
    /// treating one report as a deletion.</param>
    public ConfigFileWatcher(
        string path,
        ISchedulerTimer timer,
        TimeSpan debounce,
        Func<bool> ignoreEvents,
        Action<Action> post,
        Action onSettled,
        ILogger logger,
        Action? onVanished = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(ignoreEvents);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(onSettled);
        ArgumentNullException.ThrowIfNull(logger);

        _path = path;
        _dir = Path.GetDirectoryName(path);
        _file = Path.GetFileName(path);
        _timer = timer;
        _debounce = debounce;
        _ignoreEvents = ignoreEvents;
        _post = post;
        _onSettled = onSettled;
        _onVanished = onVanished;
        _logger = logger;
        _timer.Callback = OnTimerFired;
    }

    /// <summary>
    /// Begin watching. Returns false, arming nothing, when the directory is
    /// missing, the path has no directory or file name part, or the watcher
    /// cannot be created (logged). Never throws for those. Idempotent.
    /// </summary>
    public bool Start()
    {
        if (string.IsNullOrEmpty(_dir) || string.IsNullOrEmpty(_file)) return false;
        if (!Directory.Exists(_dir)) return false;

        Exception? error;
        lock (_lock)
        {
            if (_disposed) return false;
            if (_watcher is not null || _rebuildPending) return true;

            _watcher = CreateWatcher(out error);
            if (_watcher is not null) return true;
        }

        LogWatcherError(error!, _dir, "auto-reload is off");
        return false;
    }

    /// <summary>Builds and enables a watcher. Call under the lock, so an
    /// event it raises cannot be handled before it is published. Returns
    /// null, with <paramref name="error"/> set, both when construction
    /// throws and when enabling it fails synchronously: either way nothing
    /// here is published as a working watcher.</summary>
    private FileSystemWatcher? CreateWatcher(out Exception? error)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(_dir!, _file!)
            {
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.FileName
                    | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;

            // The underlying watch (ReadDirectoryChangesW) can fail right
            // here, and .NET raises Error synchronously, on this thread,
            // when it does: an unsupported filesystem, or the directory
            // vanishing between the existence check and this call.
            // _startingWatcher lets the handler report that failure back to
            // us instead of silently treating it as the current watcher
            // dying later, which would publish a dead watcher as healthy.
            _startingWatcher = watcher;
            _startFailure = null;
            try
            {
                if (TestEnable is not null) TestEnable(watcher);
                else watcher.EnableRaisingEvents = true;
            }
            finally
            {
                _startingWatcher = null;
            }

            if (_startFailure is not null)
            {
                DisposeQuietly(watcher);
                error = _startFailure;
                return null;
            }

            error = null;
            return watcher;
        }
        catch (Exception ex)
        {
            // The directory can vanish, or deny access, between the
            // existence check and the watch being opened.
            _startingWatcher = null;
            DisposeQuietly(watcher);
            error = ex;
            return null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Rearm();

    private void OnFileRenamed(object sender, RenamedEventArgs e) => Rearm();

    private void OnWatcherError(object sender, ErrorEventArgs e)
        => HandleWatcherError(sender, e.GetException());

    /// <summary>The Error handler's body. Internal so tests can raise a
    /// buffer overflow without flooding a directory.</summary>
    internal void HandleWatcherError(object? sender, Exception ex)
    {
        lock (_lock)
        {
            if (_startingWatcher is not null && ReferenceEquals(sender, _startingWatcher))
            {
                // Raised synchronously from inside CreateWatcher, before this
                // watcher is published anywhere (not yet _watcher, and if this
                // is a rebuild, _rebuildPending is still true). Report it back
                // there; CreateWatcher disposes it and returns the failure, so
                // Start or TryRebuild treats this exactly like a construction
                // failure instead of publishing a dead watcher as healthy.
                // Taking the lock here (CreateWatcher's caller already holds
                // it for the synchronous case) also covers the narrow window
                // where an async error for the same watcher arrives on
                // another thread while CreateWatcher is still running.
                _startFailure = ex;
                return;
            }
        }

        if (ex is InternalBufferOverflowException)
        {
            bool first;
            lock (_lock)
            {
                if (_disposed) return;
                first = !_overflowLogged;
                _overflowLogged = true;
            }
            if (first) LogWatcherOverflow();
            else LogWatcherErrorRepeat(ex.Message);
            Rearm();
            return;
        }

        // Any other error ends the watcher, but it still reads as enabled
        // here and turns off only after this handler returns, so re-enabling
        // it from inside is a no-op. Rebuild it from the timer instead.
        // A failed watcher can report more than one error, and a replaced
        // one can still have errors in flight: only the first from the
        // current watcher starts a rebuild.
        bool rebuild;
        lock (_lock)
        {
            if (_disposed) return;
            rebuild = !_rebuildPending && ReferenceEquals(sender, _watcher);
            if (rebuild)
            {
                _rebuildPending = true;
                _rebuildDelay = _debounce > MinRebuildDelay ? _debounce : MinRebuildDelay;
                _timer.Schedule(_rebuildDelay);
            }
        }
        if (rebuild) LogWatcherError(ex, _dir!, "rebuilding it");
        else LogWatcherErrorRepeat(ex.Message);
    }

    /// <summary>
    /// Schedule one more settled delivery, as if an event had just arrived.
    /// Returns whether one was actually scheduled.
    /// </summary>
    /// <remarks>
    /// For a host that was handed a settle and could not act on it because
    /// the config file existed but would not open: an editor or an indexer
    /// holding it, a sync client hydrating a placeholder. That save has
    /// already landed and already raised its events, so nothing further is
    /// coming, and without this the user's edit waits for the next save.
    /// This only schedules; the caller bounds how often it asks.
    ///
    /// What the answer means is the timer, and only that: true says a tick
    /// was armed, and every tick posts its delivery, including one consumed
    /// by a failed watcher rebuild. That is what keeps a deleted watched
    /// DIRECTORY making progress: the rebuilds fail while it is missing, the
    /// ticks still deliver, and the host's asks are answered by real
    /// deliveries rather than swallowed.
    ///
    /// What it does NOT mean is anything about the question the host is
    /// asking. An ask is dropped while the host is suppressing its own
    /// writes, and after disposal, and there is no watcher here at all when
    /// <c>auto-reload-config</c> is off, which is the default. A host that
    /// treats a false as evidence stops making progress in the case where it
    /// has the least help, so this is an ACCELERATOR: it buys a sooner look
    /// and never stands in for one. See <c>ConfigVanishConfirmer</c>, where
    /// gating on this answer was a defect with a name (wintty#1155).
    /// </remarks>
    public bool Resettle() => Rearm();

    private bool Rearm()
    {
        if (_ignoreEvents()) return false;
        lock (_lock)
        {
            if (_disposed) return false;
            _timer.Schedule(_debounce);
            return true;
        }
    }

    private void OnTimerFired()
    {
        FileSystemWatcher? failed = null;
        bool rebuild;
        lock (_lock)
        {
            if (_disposed) return;
            _overflowLogged = false;
            rebuild = _rebuildPending;
            if (rebuild)
            {
                failed = _watcher;
                _watcher = null;
            }
        }

        if (rebuild)
        {
            DisposeQuietly(failed);
            // The delivery posts whether or not the rebuild took. While
            // the watched directory is missing, this tick is the only
            // thing still running on the watcher's schedule, and a tick
            // that returns without posting is a delivery the host never
            // hears: a deleted directory would then be unconfirmable,
            // with every later reload declined for the life of the
            // process (issue #676 down the directory path).
            TryRebuild();
        }

        _post(Deliver);
    }

    /// <summary>
    /// One attempt to replace a failed watcher. On failure schedules the
    /// next attempt with a doubled delay, capped, and returns false.
    /// Internal so a test can call it directly, back to back, to exercise
    /// the overlap guard below without racing real threads.
    /// </summary>
    internal bool TryRebuild()
    {
        var present = Directory.Exists(_dir);
        Exception? error = null;
        FileSystemWatcher? created = null;
        TimeSpan retry;
        lock (_lock)
        {
            // Two timer callbacks can overlap (OnTimerFired's own read of
            // _rebuildPending is not atomic with this method), and each
            // would otherwise reach here believing it owns the rebuild.
            // Re-checking under the lock means only the first one through
            // creates a watcher; the loser leaves the winner's watcher and
            // its already-posted catch-up delivery alone instead of
            // silently overwriting _watcher and leaking the winner's.
            if (_disposed || !_rebuildPending || _watcher is not null) return false;
            if (present) created = CreateWatcher(out error);
            if (created is not null)
            {
                _watcher = created;
                _rebuildPending = false;
                retry = TimeSpan.Zero;
            }
            else
            {
                var doubled = _rebuildDelay + _rebuildDelay;
                _rebuildDelay = doubled < MaxRebuildDelay ? doubled : MaxRebuildDelay;
                retry = _rebuildDelay;
                _timer.Schedule(retry);
            }
        }

        if (created is not null)
        {
            LogWatcherRebuilt(_dir!);
            return true;
        }
        LogWatcherRebuildRetry(_dir!, retry, error?.Message ?? "the directory is missing");
        return false;
    }

    /// <summary>Runs on the thread <c>post</c> delivers to.</summary>
    private void Deliver()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }

        if (!File.Exists(_path))
        {
            LogSettledWithoutFile(_path);
            // Gone as of now, which is all this can say. The debounce has
            // already fired by the time this runs, so the quiet period it
            // bought is behind us and an ordinary swap reaches here whenever
            // it straddles the hop from the timer to this delivery. "Gone
            // for good" and "gone for the next few milliseconds" look
            // identical from this line; the host tells them apart by asking
            // again, which is what OnConfigFileVanished does.
            _onVanished?.Invoke();
            return;
        }

        _onSettled();
    }

    private static void DisposeQuietly(FileSystemWatcher? watcher)
    {
        if (watcher is null) return;
        try { watcher.EnableRaisingEvents = false; } catch { }
        watcher.Dispose();
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _timer.Cancel();
        }

        DisposeQuietly(watcher);
        _timer.Dispose();
    }

    [LoggerMessage(EventId = LogEvents.Config.WatcherError,
                   Level = LogLevel.Warning,
                   Message = "[ConfigFileWatcher] watching {Directory} failed; {Recovery}")]
    private partial void LogWatcherError(Exception ex, string directory, string recovery);

    [LoggerMessage(EventId = LogEvents.Config.WatcherOverflow,
                   Level = LogLevel.Warning,
                   Message = "[ConfigFileWatcher] watcher buffer overflowed, events may be lost; re-arming")]
    private partial void LogWatcherOverflow();

    [LoggerMessage(EventId = LogEvents.Config.WatcherErrorRepeat,
                   Level = LogLevel.Debug,
                   Message = "[ConfigFileWatcher] further watcher error: {Reason}")]
    private partial void LogWatcherErrorRepeat(string reason);

    [LoggerMessage(EventId = LogEvents.Config.WatcherRebuilt,
                   Level = LogLevel.Information,
                   Message = "[ConfigFileWatcher] watching {Directory} again")]
    private partial void LogWatcherRebuilt(string directory);

    [LoggerMessage(EventId = LogEvents.Config.WatcherRebuildRetry,
                   Level = LogLevel.Debug,
                   Message = "[ConfigFileWatcher] cannot watch {Directory} yet ({Reason}); retrying in {Delay}")]
    private partial void LogWatcherRebuildRetry(string directory, TimeSpan delay, string reason);

    [LoggerMessage(EventId = LogEvents.Config.WatcherFileMissing,
                   Level = LogLevel.Information,
                   Message = "[ConfigFileWatcher] config file absent when the edit settled, waiting for it to come back: {Path}")]
    private partial void LogSettledWithoutFile(string path);
}
