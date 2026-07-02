using System;
using System.Windows.Input;

namespace Wisp;

/// <summary>Minimal ICommand for wiring keyboard shortcuts to actions.</summary>
public class RelayCommand : ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;

    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
}
