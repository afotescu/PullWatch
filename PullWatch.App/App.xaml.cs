using System.Windows;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private ApplicationController? _controller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _loggerFactory = LoggerFactory.Create(_ => { });
        _controller = new ApplicationController(_loggerFactory);

        try
        {
            await _controller.StartAsync(CancellationToken.None);
            new MainWindow(_controller).Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "PullWatch could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_controller is not null)
        {
            Task.Run(() => _controller.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }

        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
