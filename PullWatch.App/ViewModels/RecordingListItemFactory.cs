using System.IO;

namespace PullWatch;

internal static class RecordingListItemFactory
{
    private const string MissingMetadataValue = "-";

    public static IReadOnlyList<RecordingListItem> Create(
        IReadOnlyList<RecordingCatalogFile> recordings
    )
    {
        return recordings.Select(Create).ToList();
    }

    private static RecordingListItem Create(RecordingCatalogFile file)
    {
        var displayName = Path.GetFileNameWithoutExtension(file.FilePath);

        return new RecordingListItem(
            file.Id,
            GetCategory(file),
            file.FilePath,
            displayName,
            FormatStartedAt(file),
            FormatPullNumber(file),
            GetActivity(file, displayName),
            GetActivityDetail(file),
            FormatContext(file),
            FormatResult(file),
            FormatActivityDuration(file),
            file.ModifiedAtUtc.ToLocalTime(),
            file.SizeBytes,
            file.IsFavorite
        );
    }

    private static string FormatStartedAt(RecordingCatalogFile file)
    {
        var startedAtUtc =
            file.RaidEncounter?.EncounterStartedAtUtc
            ?? file.ChallengeMode?.ChallengeStartedAtUtc
            ?? file.StartedAtUtc;

        return startedAtUtc is null
            ? MissingMetadataValue
            : $"{startedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    private static string FormatPullNumber(RecordingCatalogFile file)
    {
        return file.RaidEncounter?.PullNumber is { } pullNumber
            ? pullNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : MissingMetadataValue;
    }

    private static RecordingListCategory GetCategory(RecordingCatalogFile file)
    {
        return file.Kind switch
        {
            RecordingCatalogKind.ChallengeMode => RecordingListCategory.ChallengeMode,
            RecordingCatalogKind.Encounter => RecordingListCategory.RaidEncounter,
            _ => RecordingListCategory.Manual,
        };
    }

    private static string GetActivity(RecordingCatalogFile file, string displayName)
    {
        if (file.RaidEncounter is { } raidEncounter)
        {
            return raidEncounter.EncounterName;
        }

        if (file.ChallengeMode is { } challengeMode)
        {
            return challengeMode.DungeonName;
        }

        return file.Kind switch
        {
            RecordingCatalogKind.Encounter => "Unknown encounter",
            RecordingCatalogKind.ChallengeMode => "Mythic+ recording",
            RecordingCatalogKind.Manual => string.IsNullOrWhiteSpace(displayName)
                ? "Manual recording"
                : displayName,
            _ => string.IsNullOrWhiteSpace(displayName) ? "Recording" : displayName,
        };
    }

    private static string GetActivityDetail(RecordingCatalogFile file)
    {
        return file.ChallengeMode is { AffixIds.Count: > 0 } challengeMode
            ? $"Affix IDs {string.Join(", ", challengeMode.AffixIds)}"
            : string.Empty;
    }

    private static string FormatContext(RecordingCatalogFile file)
    {
        if (file.RaidEncounter is { } raidEncounter)
        {
            return WowRaidDifficultyFormatter.FormatDisplayName(raidEncounter.DifficultyId);
        }

        return file.ChallengeMode is { } challengeMode ? $"+{challengeMode.KeystoneLevel}"
            : file.Kind == RecordingCatalogKind.Manual ? "Manual"
            : MissingMetadataValue;
    }

    private static string FormatResult(RecordingCatalogFile file)
    {
        if (file.RaidEncounter is { } raidEncounter)
        {
            return raidEncounter.Outcome switch
            {
                RaidEncounterOutcome.Kill => "Kill",
                RaidEncounterOutcome.Wipe => "Wipe",
                _ => "Unknown",
            };
        }

        if (file.ChallengeMode is { } challengeMode)
        {
            return challengeMode.Outcome switch
            {
                ChallengeModeOutcome.Timed => "Timed",
                ChallengeModeOutcome.Depleted => "Depleted",
                _ => "Unknown",
            };
        }

        return file.Kind == RecordingCatalogKind.Manual ? "Saved" : MissingMetadataValue;
    }

    private static string FormatActivityDuration(RecordingCatalogFile file)
    {
        if (file.RaidEncounter is { } raidEncounter)
        {
            var duration =
                raidEncounter.DurationMilliseconds is { } durationMilliseconds
                    ? TimeSpan.FromMilliseconds(Math.Max(0, durationMilliseconds))
                : raidEncounter.EncounterEndedAtUtc is { } encounterEndedAt
                    ? encounterEndedAt - raidEncounter.EncounterStartedAtUtc
                : (TimeSpan?)null;

            return FormatNullableDuration(duration);
        }

        var recordingDuration =
            file.StartedAtUtc is { } recordingStartedAt && file.EndedAtUtc is { } recordingEndedAt
                ? recordingEndedAt - recordingStartedAt
                : (TimeSpan?)null;

        return FormatNullableDuration(recordingDuration);
    }

    private static string FormatNullableDuration(TimeSpan? duration)
    {
        return duration is null
            ? MissingMetadataValue
            : RecordingTimeFormatter.FormatPlaybackTime(
                duration.Value < TimeSpan.Zero ? TimeSpan.Zero : duration.Value
            );
    }
}
