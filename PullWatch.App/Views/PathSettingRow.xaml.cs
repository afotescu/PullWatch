using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PullWatch;

public partial class PathSettingRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(PathSettingRow),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(PathSettingRow),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault
        )
    );

    public static readonly DependencyProperty ErrorProperty = DependencyProperty.Register(
        nameof(Error),
        typeof(string),
        typeof(PathSettingRow),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty BrowseCommandProperty = DependencyProperty.Register(
        nameof(BrowseCommand),
        typeof(ICommand),
        typeof(PathSettingRow),
        new PropertyMetadata(null)
    );

    public static readonly DependencyProperty CommitCommandProperty = DependencyProperty.Register(
        nameof(CommitCommand),
        typeof(IAsyncRelayCommand),
        typeof(PathSettingRow),
        new PropertyMetadata(null)
    );

    public static readonly DependencyProperty BrowseButtonTextProperty =
        DependencyProperty.Register(
            nameof(BrowseButtonText),
            typeof(string),
            typeof(PathSettingRow),
            new PropertyMetadata("Browse")
        );

    public PathSettingRow()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Error
    {
        get => (string)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public ICommand? BrowseCommand
    {
        get => (ICommand?)GetValue(BrowseCommandProperty);
        set => SetValue(BrowseCommandProperty, value);
    }

    public IAsyncRelayCommand? CommitCommand
    {
        get => (IAsyncRelayCommand?)GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    public string BrowseButtonText
    {
        get => (string)GetValue(BrowseButtonTextProperty);
        set => SetValue(BrowseButtonTextProperty, value);
    }

    private async void OnPathTextBoxLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (ReferenceEquals(Keyboard.FocusedElement, BrowseButton))
        {
            return;
        }

        await CommitAsync();
    }

    private async void OnPathTextBoxKeyDown(object sender, WpfKeyEventArgs eventArgs)
    {
        if (eventArgs.Key != WpfKey.Enter)
        {
            return;
        }

        eventArgs.Handled = true;
        await CommitAsync();
    }

    private Task CommitAsync()
    {
        return CommitCommand?.ExecuteAsync(null) ?? Task.CompletedTask;
    }
}
