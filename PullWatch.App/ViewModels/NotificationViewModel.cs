using System.Windows.Input;

namespace PullWatch;

public sealed class NotificationViewModel : ObservableObject
{
    private readonly Action<NotificationViewModel> _dismiss;

    private NotificationSeverity _severity;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string? _actionText;
    private ICommand? _actionCommand;
    private bool _isDismissible;
    private Action? _dismissed;

    internal NotificationViewModel(string id, Action<NotificationViewModel> dismiss)
    {
        Id = id;
        _dismiss = dismiss;
        DismissCommand = new RelayCommand(Dismiss, () => IsDismissVisible);
    }

    public string Id { get; }

    public NotificationSeverity Severity
    {
        get => _severity;
        private set => SetProperty(ref _severity, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string? ActionText
    {
        get => _actionText;
        private set
        {
            if (SetProperty(ref _actionText, value))
            {
                OnPropertyChanged(nameof(IsActionVisible));
            }
        }
    }

    public ICommand? ActionCommand
    {
        get => _actionCommand;
        private set
        {
            if (SetProperty(ref _actionCommand, value))
            {
                OnPropertyChanged(nameof(IsActionVisible));
            }
        }
    }

    public bool IsActionVisible =>
        ActionCommand is not null && !string.IsNullOrWhiteSpace(ActionText);

    public bool IsDismissible
    {
        get => _isDismissible;
        private set
        {
            if (SetProperty(ref _isDismissible, value))
            {
                OnPropertyChanged(nameof(IsDismissVisible));
                DismissCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsDismissVisible => IsDismissible;

    public IRelayCommand DismissCommand { get; }

    internal void Update(NotificationContent content)
    {
        Severity = content.Severity;
        Title = content.Title;
        Message = content.Message;
        ActionText = content.ActionText;
        ActionCommand = content.ActionCommand;
        IsDismissible = content.IsDismissible;
        _dismissed = content.Dismissed;
    }

    private void Dismiss()
    {
        if (IsDismissVisible)
        {
            _dismissed?.Invoke();
            _dismiss(this);
        }
    }
}
