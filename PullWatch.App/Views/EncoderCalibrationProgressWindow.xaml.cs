using System.ComponentModel;
using System.Windows;

namespace PullWatch;

public partial class EncoderCalibrationProgressWindow : Window
{
    private bool _canClose;

    public EncoderCalibrationProgressWindow()
    {
        InitializeComponent();
    }

    public async Task<T> RunAsync<T>(
        Window owner,
        Func<IProgress<VideoEncoderTestProgress>, Task<T>> operation
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);

        if (owner.IsVisible)
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Task<T>? operationTask = null;
        ContentRendered += OnContentRendered;

        void OnContentRendered(object? sender, EventArgs eventArgs)
        {
            ContentRendered -= OnContentRendered;
            operationTask = operation(new Progress<VideoEncoderTestProgress>(Report));
            _ = CloseWhenOperationCompletesAsync(operationTask);
        }

        ShowDialog();

        return await (
            operationTask
            ?? Task.FromException<T>(
                new InvalidOperationException("The video encoding test did not start.")
            )
        );
    }

    private async Task CloseWhenOperationCompletesAsync(Task operationTask)
    {
        try
        {
            await operationTask;
        }
        catch
        {
            // The caller awaits the operation task and owns error reporting.
        }
        finally
        {
            _canClose = true;
            Close();
        }
    }

    private void Report(VideoEncoderTestProgress progress)
    {
        var totalProfiles = Math.Max(progress.TotalProfiles, 1);
        var completedProfiles = Math.Clamp(progress.CompletedProfiles, 0, totalProfiles);

        ProfileProgressBar.Maximum = totalProfiles;
        ProfileProgressBar.Value = completedProfiles;
        StepTextBlock.Text = $"{completedProfiles} of {totalProfiles}";
        StatusTextBlock.Text =
            completedProfiles >= totalProfiles
                ? "Finishing video encoding test..."
                : $"Testing {VideoProfileFormatter.FormatDisplayName(progress.CurrentProfile)}";
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!_canClose)
        {
            eventArgs.Cancel = true;
            return;
        }

        base.OnClosing(eventArgs);
    }
}
