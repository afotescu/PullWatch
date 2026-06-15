using System.Windows.Input;

namespace PullWatch;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null) : ObservableObject, ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (SetProperty(ref _isExecuting, value))
            {
                NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanExecute(object? parameter)
    {
        return !IsExecuting && (canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync();
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        IsExecuting = true;

        try
        {
            await execute();
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
