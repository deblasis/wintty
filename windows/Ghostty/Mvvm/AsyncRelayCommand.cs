using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Ghostty.Mvvm;

/// <summary>
/// Async <see cref="ICommand"/>. Disables itself while a previous
/// invocation is in flight (prevents double-invoke from accidental
/// button spam) and re-raises CanExecuteChanged when the task
/// completes. Exceptions surface on the awaiting dispatcher.
/// </summary>
internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    // Read/written across the UI thread (Execute entry, CanExecute queries)
    // and any thread the inner Task's continuation happens to run on.
    // Interlocked CAS provides the "already running" guard atomically.
    // 0 = idle, 1 = running.
    private int _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _isRunning) == 0 && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0) return;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
