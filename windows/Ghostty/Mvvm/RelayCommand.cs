using System;
using System.Windows.Input;

namespace Ghostty.Mvvm;

/// <summary>
/// Lightweight synchronous <see cref="ICommand"/>. Delegate-backed.
/// Callers invoke <see cref="RaiseCanExecuteChanged"/> when any
/// predicate input changes so code-behind-wired buttons can re-query
/// via <c>CanExecuteChanged</c>.
/// </summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
