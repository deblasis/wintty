using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Dialogs;

/// <summary>
/// Registers every in-flight <see cref="ContentDialog"/> so
/// <c>MainWindow.Closed</c> can wait for them to dismiss before
/// tearing down the libghostty host. Shutting down while a dialog is
/// still on-screen races the XAML data-binding teardown and throws
/// <c>COMException</c> out of the runtime.
///
/// Usage:
///   using (tracker.Track(dialog))
///   {
///       var result = await dialog.ShowAsync();
///   }
///
/// The token disposes the tracker entry when the dialog's own
/// <see cref="ContentDialog.ShowAsync"/> completes, whether the user
/// confirmed, cancelled, or the runtime forced it to close.
/// </summary>
internal sealed class DialogTracker
{
    private readonly HashSet<TaskCompletionSource> _pending = new();
    private readonly object _lock = new();

    public int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }

    /// <summary>
    /// Register a dialog as open. Returns a disposable token whose
    /// Dispose() marks the entry as complete. Callers wrap their
    /// <c>await dialog.ShowAsync()</c> in a <c>using</c>.
    /// </summary>
    public System.IDisposable Track(ContentDialog dialog)
    {
        var tcs = new TaskCompletionSource();
        lock (_lock) _pending.Add(tcs);
        return new Token(this, tcs, dialog);
    }

    /// <summary>
    /// Await every dialog currently tracked. Safe to call multiple
    /// times; if nothing is pending, completes immediately.
    /// </summary>
    public Task WhenAllClosedAsync()
    {
        Task[] tasks;
        lock (_lock)
        {
            if (_pending.Count == 0) return Task.CompletedTask;
            tasks = new Task[_pending.Count];
            int i = 0;
            foreach (var t in _pending) tasks[i++] = t.Task;
        }
        return Task.WhenAll(tasks);
    }

    private void Release(TaskCompletionSource tcs)
    {
        lock (_lock) _pending.Remove(tcs);
        tcs.TrySetResult();
    }

    private sealed class Token : System.IDisposable
    {
        private readonly DialogTracker _tracker;
        private readonly TaskCompletionSource _tcs;
        private readonly ContentDialog _dialog;
        private bool _disposed;

        public Token(DialogTracker tracker, TaskCompletionSource tcs, ContentDialog dialog)
        {
            _tracker = tracker;
            _tcs = tcs;
            _dialog = dialog;
            // If the window is closing while the dialog is showing,
            // MainWindow hides the dialog explicitly; we still need
            // the Closed handler to release the TCS even when the
            // caller's await is abandoned.
            _dialog.Closed += OnClosed;
        }

        private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _dialog.Closed -= OnClosed;
            _tracker.Release(_tcs);
        }
    }
}
