using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class CombatLogEventHandler(
    IRecordingService recordingService,
    ILogger<CombatLogEventHandler> logger)
{
    private long _previousChallengeEventTimestamp;

    public async Task HandleAsync(string eventName, CancellationToken cancellationToken)
    {
        var eventTimestamp = Stopwatch.GetTimestamp();

        switch (eventName)
        {
            case WowEvents.ChallengeModeStart:
                LogChallengeEventReceived(eventName, eventTimestamp);
                await recordingService.StartAsync(cancellationToken);
                logger.LogInformation(
                    "Handled {EventName} in {ElapsedMilliseconds:F1} ms",
                    eventName,
                    Stopwatch.GetElapsedTime(eventTimestamp).TotalMilliseconds);
                break;
            case WowEvents.ChallengeModeEnd:
                LogChallengeEventReceived(eventName, eventTimestamp);
                await recordingService.StopAsync(cancellationToken);
                logger.LogInformation(
                    "Handled {EventName} in {ElapsedMilliseconds:F1} ms",
                    eventName,
                    Stopwatch.GetElapsedTime(eventTimestamp).TotalMilliseconds);
                break;
        }
    }

    private void LogChallengeEventReceived(string eventName, long eventTimestamp)
    {
        if (_previousChallengeEventTimestamp == 0)
        {
            logger.LogInformation("Received {EventName}", eventName);
        }
        else
        {
            logger.LogInformation(
                "Received {EventName}; previous challenge event was {ElapsedSincePreviousEvent}",
                eventName,
                Stopwatch.GetElapsedTime(_previousChallengeEventTimestamp, eventTimestamp));
        }

        _previousChallengeEventTimestamp = eventTimestamp;
    }
}
