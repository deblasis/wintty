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
/// place: they write a temp file and then swap it in, by delete and rename
/// (<c>File.Move</c> with overwrite), by <c>ReplaceFile</c> (which renames the
/// original away first), or by renaming the original to a backup and writing
/// a fresh file under the old name. Each of those passes through a moment
/// where the config file does not exist, and each raises a burst of watcher
/// events (Deleted, Renamed away, Renamed in, Changed) rather than one.
///
/// Two rules follow from that:
///
/// Every event kind re-arms the debounce, Deleted included, so the whole
/// burst collapses into a single settle and a reload never fires between
/// the two halves of a swap.
///
/// A settle that finds the file missing reports nothing. A reload at that
/// moment would load no user config at all, and libghostty's default-file
/// loader answers "no config file" by writing its template to that exact
/// path, which is where the editor's rename is about to land. The missing
/// file is not the end of the save: the rename that completes it raises
/// its own event, which re-arms the debounce, and that settle finds the
/// file and reports it. A file that is deleted and never comes back simply
/// keeps the config that is already running.
///
/// The watcher is directory-scoped (the file name is only its filter), so
/// the file being deleted and replaced does not stop it. An internal
/// buffer overflow does not stop it either, but it can drop the event that
/// carried the save, so <see cref="FileSystemWatcher.Error"/> re-arms the
/// debounce too, and re-enables raising if the watcher turned it off.
/// </summary>
public sealed partial class ConfigFileWatcher : IDisposable
{
    private readonly string _path;
    private readonly ISchedulerTimer _timer;
    private readonly TimeSpan _debounce;
    private readonly Func<bool> _ignoreEvents;
    private readonly Action _onSettled;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <param name="path">The config file. Its directory must exist for
    /// <see cref="Start"/> to arm anything.</param>
    /// <param name="timer">Debounce timer. Owned: disposed with this.</param>
    /// <param name="debounce">Quiet period after the last event.</param>
    /// <param name="ignoreEvents">Consulted when each event arrives, not when
    /// the debounce fires, so a write the host brackets with a suppression
    /// flag stays ignored even though the settle would land after the flag
    /// is lowered.</param>
    /// <param name="onSettled">Called on the timer's thread once per settled
    /// edit, only while the file exists. Marshal onto the UI thread there.</param>
    public ConfigFileWatcher(
        string path,
        ISchedulerTimer timer,
        TimeSpan debounce,
        Func<bool> ignoreEvents,
        Action onSettled,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(ignoreEvents);
        ArgumentNullException.ThrowIfNull(onSettled);
        ArgumentNullException.ThrowIfNull(logger);

        _path = path;
        _timer = timer;
        _debounce = debounce;
        _ignoreEvents = ignoreEvents;
        _onSettled = onSettled;
        _logger = logger;
        _timer.Callback = OnTimerFired;
    }

    /// <summary>
    /// Begin watching. Returns false, arming nothing, when the directory is
    /// missing or the path has no directory or file name part. Idempotent.
    /// </summary>
    public bool Start()
    {
        var dir = Path.GetDirectoryName(_path);
        var file = Path.GetFileName(_path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return false;
        if (!Directory.Exists(dir)) return false;

        lock (_lock)
        {
            if (_disposed) return false;
            if (_watcher is not null) return true;

            var watcher = new FileSystemWatcher(dir, file)
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
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            return true;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Rearm();

    private void OnFileRenamed(object sender, RenamedEventArgs e) => Rearm();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        LogWatcherError(e.GetException());
        lock (_lock)
        {
            if (_disposed || _watcher is null) return;
            if (!_watcher.EnableRaisingEvents)
            {
                try { _watcher.EnableRaisingEvents = true; }
                catch (Exception ex) { LogWatcherError(ex); }
            }
        }
        Rearm();
    }

    private void Rearm()
    {
        if (_ignoreEvents()) return;
        lock (_lock)
        {
            if (_disposed) return;
            _timer.Schedule(_debounce);
        }
    }

    private void OnTimerFired()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }

        if (!File.Exists(_path))
        {
            LogSettledWithoutFile(_path);
            return;
        }

        _onSettled();
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

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _timer.Dispose();
    }

    [LoggerMessage(EventId = LogEvents.Config.WatcherError,
                   Level = LogLevel.Warning,
                   Message = "[ConfigFileWatcher] watcher reported an error; re-arming")]
    private partial void LogWatcherError(Exception ex);

    [LoggerMessage(EventId = LogEvents.Config.WatcherFileMissing,
                   Level = LogLevel.Information,
                   Message = "[ConfigFileWatcher] config file absent when the edit settled, waiting for it to come back: {Path}")]
    private partial void LogSettledWithoutFile(string path);
}
