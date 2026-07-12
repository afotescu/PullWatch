using System.Windows;

namespace PullWatch;

public partial class WhatsNewWindow : Window
{
    internal WhatsNewWindow(WhatsNewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }
}
