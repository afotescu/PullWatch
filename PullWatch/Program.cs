using Microsoft.Extensions.Logging;
using PullWatch;

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

if (!CommandLineOptions.TryParse(args, out var commandLine, out var commandLineError))
{
    logger.LogError("{CommandLineError}", commandLineError);
    return;
}

var settingsStore = new SettingsStore();
var settingsBootstrapper = new SettingsBootstrapper(
    settingsStore,
    loggerFactory.CreateLogger<SettingsBootstrapper>());
var settings = await settingsBootstrapper.LoadEffectiveAsync(commandLine, cancellation.Token);

if (settings is null)
{
    return;
}

var settingsProvider = new SettingsProvider(settings);
LogEffectiveSettings(logger, settingsStore.SettingsPath, settings);

var recordingService = new ScreenRecordingService(
    settingsProvider,
    loggerFactory.CreateLogger<ScreenRecordingService>());
await using var recordingCoordinator = new RecordingCoordinator(
    recordingService,
    loggerFactory.CreateLogger<RecordingCoordinator>());

if (commandLine.RecordNow)
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

if (settings.WowLogsDirectory is null)
{
    logger.LogError(
        "No WoW logs directory is configured and none was detected. Configure it in {SettingsPath} or with --wow-logs-directory",
        settingsStore.SettingsPath);
    return;
}

var reader = new CombatLogReader(
    settings.WowLogsDirectory,
    loggerFactory.CreateLogger<CombatLogReader>());
var eventHandler = new CombatLogEventHandler(
    recordingCoordinator,
    settingsProvider,
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

static void LogEffectiveSettings(
    ILogger logger,
    string settingsPath,
    PullWatchSettings settings)
{
    logger.LogInformation(
        "Effective settings from {SettingsPath}: WoW logs {WowLogsDirectory}; recordings {RecordingsDirectory}; Mythic+ {RecordMythicPlus}; raids {RecordRaidEncounters}; {FrameRate} FPS; {Bitrate} bps; system audio {CaptureSystemAudio}; microphone {CaptureMicrophone}; cursor {CaptureCursor}; border {ShowCaptureBorder}",
        settingsPath,
        settings.WowLogsDirectory,
        settings.RecordingsDirectory,
        settings.RecordMythicPlus,
        settings.RecordRaidEncounters,
        settings.Video.FrameRate,
        settings.Video.Bitrate,
        settings.Audio.CaptureSystemAudio,
        settings.Audio.CaptureMicrophone,
        settings.Video.CaptureCursor,
        settings.Video.ShowCaptureBorder);
}
