using System.Windows;
using System.Windows.Controls;
using Input = System.Windows.Input;

namespace PullWatch;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnWowLogsDirectoryLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (ReferenceEquals(Input.Keyboard.FocusedElement, WowLogsDirectoryBrowseButton))
        {
            return;
        }

        await CommitWowLogsDirectoryAsync();
    }

    private async void OnRecordingsDirectoryLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (ReferenceEquals(Input.Keyboard.FocusedElement, RecordingsDirectoryBrowseButton))
        {
            return;
        }

        await CommitRecordingsDirectoryAsync();
    }

    private async void OnWowLogsDirectoryKeyDown(object sender, Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Input.Key.Enter)
        {
            return;
        }

        eventArgs.Handled = true;
        await CommitWowLogsDirectoryAsync();
    }

    private async void OnRecordingsDirectoryKeyDown(object sender, Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Input.Key.Enter)
        {
            return;
        }

        eventArgs.Handled = true;
        await CommitRecordingsDirectoryAsync();
    }

    private Task CommitWowLogsDirectoryAsync()
    {
        return DataContext is SettingsViewModel viewModel
            ? viewModel.CommitWowLogsDirectoryCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }

    private Task CommitRecordingsDirectoryAsync()
    {
        return DataContext is SettingsViewModel viewModel
            ? viewModel.CommitRecordingsDirectoryCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }
}
