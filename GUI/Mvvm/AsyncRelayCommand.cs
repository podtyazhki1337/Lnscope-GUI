using System.Windows.Input;

namespace GUI.Mvvm;

/// <summary>Asynchronous button command. Disable the button while running to prevent duplicate controller requests.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>Receives driver exceptions so they can be reported without terminating the application.</summary>
    public Action<Exception>? ErrorHandler { get; set; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _isRunning = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute();
        }
        catch (Exception exception)
        {
            ErrorHandler?.Invoke(exception);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
