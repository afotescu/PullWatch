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

    private void OnNonNegativeNumberPreviewTextInput(
        object sender,
        Input.TextCompositionEventArgs eventArgs
    )
    {
        eventArgs.Handled = !ContainsOnlyDigits(eventArgs.Text);
    }

    private void OnNonNegativeNumberPaste(object sender, DataObjectPastingEventArgs eventArgs)
    {
        if (!eventArgs.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            eventArgs.CancelCommand();
            return;
        }

        if (
            eventArgs.DataObject.GetData(System.Windows.DataFormats.Text) is not string text
            || !ContainsOnlyDigits(text)
        )
        {
            eventArgs.CancelCommand();
        }
    }

    private void OnMinimumMythicPlusKeystoneLevelLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (
            sender is System.Windows.Controls.TextBox textBox
            && string.IsNullOrWhiteSpace(textBox.Text)
        )
        {
            textBox.Text = "0";
        }
    }

    private void OnRecordingStorageLimitLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (
            sender is System.Windows.Controls.TextBox textBox
            && string.IsNullOrWhiteSpace(textBox.Text)
        )
        {
            textBox.Text = "1";
        }
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

    private static bool ContainsOnlyDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
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
