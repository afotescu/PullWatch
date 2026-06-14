using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class CombatLogEventHandler(
    IRecordingService recordingService,
    ILogger<CombatLogEventHandler> logger)
{
    public async Task HandleAsync(string eventName, CancellationToken cancellationToken)
    {
        switch (eventName)
        {
            case WowEvents.ChallengeModeStart:
                logger.LogInformation("Mythic+ challenge started");
                await recordingService.StartAsync(cancellationToken);
                break;
            case WowEvents.ChallengeModeEnd:
                logger.LogInformation("Mythic+ challenge ended");
                await recordingService.StopAsync(cancellationToken);
                break;
        }
    }
}
