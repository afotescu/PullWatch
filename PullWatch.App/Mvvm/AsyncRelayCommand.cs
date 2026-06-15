using System.Windows.Input;

namespace PullWatch;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onException = null) : ObservableObject, ICommand
{
    private bool _isExecuting;
    private Exception? _lastException;

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

    public Exception? LastException
    {
        get => _lastException;
        private set => SetProperty(ref _lastException, value);
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

        LastException = null;
        IsExecuting = true;

        try
        {
            await execute();
        }
        catch (Exception exception)
        {
            LastException = exception;

            try
            {
                onException?.Invoke(exception);
            }
            catch (Exception reportingException)
            {
                LastException = new AggregateException(
                    "Command execution and exception reporting both failed.",
                    exception,
                    reportingException);
            }
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
