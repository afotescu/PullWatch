using Microsoft.Extensions.Logging;
using PullWatch;

var recordNow = args.Contains("--record-now", StringComparer.Ordinal);
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
var recordingService = new ScreenRecordingService(
    loggerFactory.CreateLogger<ScreenRecordingService>());
await using var recordingCoordinator = new RecordingCoordinator(
    recordingService,
    loggerFactory.CreateLogger<RecordingCoordinator>());

if (recordNow)
{
    try
    {
        var result = await recordingCoordinator.StartManualAsync(cancellation.Token);
        logger.LogInformation(
            "Manual recording start result: {RecordingCommandResult}; press Ctrl+C to stop",
            result);

        if (result != RecordingCommandResult.Started)
        {
            return;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        logger.LogInformation("Stopping manual recording");
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Manual recording failed");
    }

    await recordingCoordinator.StopManualAsync(CancellationToken.None);
    return;
}

var reader = new CombatLogReader(logsDirectory, loggerFactory.CreateLogger<CombatLogReader>());
var eventHandler = new CombatLogEventHandler(
    recordingCoordinator,
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
