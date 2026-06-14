using Microsoft.Extensions.Logging;
using PullWatch;

var logsDirectory = @"E:\World of Warcraft\_retail_\Logs";

using var cancellation = new CancellationTokenSource();
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
});

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var logger = loggerFactory.CreateLogger("PullWatch");
var reader = new CombatLogReader(logsDirectory, loggerFactory.CreateLogger<CombatLogReader>());
await using var recordingService = new ScreenRecordingService(
    loggerFactory.CreateLogger<ScreenRecordingService>());
var eventHandler = new CombatLogEventHandler(
    recordingService,
    loggerFactory.CreateLogger<CombatLogEventHandler>());

try
{
    await reader.ReadAsync(eventHandler.HandleAsync, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    logger.LogInformation("Stopped");
}
catch (Exception exception)
{
    logger.LogError(exception, "PullWatch stopped unexpectedly");
}
