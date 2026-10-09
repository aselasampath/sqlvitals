using System.Windows.Input;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// The smallest <see cref="ICommand"/>: runs an action, always enabled. For a button bound with
/// Command= where the MVVM rule forbids a new Click= (docs/Architecture.md), until a page has a
/// view model with commands of its own (#170, ahead of the MVVM pilot #184).
/// </summary>
internal sealed class RelayCommand(Action execute) : ICommand
{
    // Always enabled, so nothing ever changes.
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
