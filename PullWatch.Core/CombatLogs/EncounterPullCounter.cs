namespace PullWatch;

internal sealed class EncounterPullCounter
{
    private readonly Dictionary<EncounterPullKey, int> _pullCounts = new();

    public EncounterRecordingContext AssignNextPullNumber(EncounterRecordingContext context)
    {
        var key = EncounterPullKey.From(context);
        var nextPullNumber = _pullCounts.GetValueOrDefault(key) + 1;
        return context with { PullNumber = nextPullNumber };
    }

    public void Commit(EncounterRecordingContext context)
    {
        if (context.PullNumber is not { } pullNumber)
        {
            return;
        }

        _pullCounts[EncounterPullKey.From(context)] = pullNumber;
    }

    private sealed record EncounterPullKey(int EncounterId, int DifficultyId)
    {
        public static EncounterPullKey From(EncounterRecordingContext context)
        {
            return new EncounterPullKey(context.EncounterId, context.DifficultyId);
        }
    }
}
