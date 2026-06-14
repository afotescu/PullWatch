namespace PullWatch;

public sealed class CombatLogEventHandler
{
    public Task HandleAsync(string eventName, CancellationToken cancellationToken)
    {
        switch (eventName)
        {
            case WowEvents.ChallengeModeStart:
                Console.WriteLine("Mythic+ challenge started.");
                break;
            case WowEvents.ChallengeModeEnd:
                Console.WriteLine("Mythic+ challenge ended.");
                break;
        }

        return Task.CompletedTask;
    }
}
