using System.Windows;
using WpfButton = System.Windows.Controls.Button;

namespace PullWatch;

public partial class ConfirmationDialogWindow : Window
{
    public ConfirmationDialogWindow(ConfirmationDialogRequest request)
    {
        InitializeComponent();

        Result = request.DefaultResult;
        Title = request.Title;
        TitleTextBlock.Text = request.Heading ?? request.Title;
        MessageItemsControl.ItemsSource = request.MessageLines;

        foreach (var button in request.Buttons)
        {
            ButtonsPanel.Children.Add(CreateButton(button));
        }
    }

    public ConfirmationDialogResult Result { get; private set; }

    private WpfButton CreateButton(ConfirmationDialogButton dialogButton)
    {
        var button = new WpfButton
        {
            Content = dialogButton.Text,
            IsCancel = dialogButton.IsCancel,
            IsDefault = dialogButton.IsDefault,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 92,
            Style = GetButtonStyle(dialogButton.Kind),
        };
        button.Click += (_, _) =>
        {
            Result = dialogButton.Result;
            DialogResult = dialogButton.Result != ConfirmationDialogResult.Cancel;
        };

        return button;
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private Style? GetButtonStyle(ConfirmationDialogButtonKind kind)
    {
        return kind switch
        {
            ConfirmationDialogButtonKind.Accent => TryFindResource("AccentButtonStyle") as Style,
            ConfirmationDialogButtonKind.Destructive => TryFindResource("StopButtonStyle") as Style,
            _ => TryFindResource("BaseButtonStyle") as Style,
        };
    }
}
