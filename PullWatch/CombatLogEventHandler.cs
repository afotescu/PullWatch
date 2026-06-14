using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class CombatLogEventHandler(ILogger<CombatLogEventHandler> logger)
{
    public Task HandleAsync(string eventName, CancellationToken cancellationToken)
    {
        switch (eventName)
        {
            case WowEvents.ChallengeModeStart:
                logger.LogInformation("Mythic+ challenge started");
                break;
            case WowEvents.ChallengeModeEnd:
                logger.LogInformation("Mythic+ challenge ended");
                break;
        }

        return Task.CompletedTask;
    }
}
