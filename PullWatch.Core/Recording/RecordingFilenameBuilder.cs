using System.Globalization;
using System.Text;

namespace PullWatch;

internal static class RecordingFilenameBuilder
{
    public static string CreateAvailablePath(string recordingsDirectory, RecordingContext context)
    {
        var baseName = BuildBaseName(context);
        var path = Path.Combine(recordingsDirectory, $"{baseName}.mp4");
        var suffix = 2;

        while (File.Exists(path))
        {
            path = Path.Combine(recordingsDirectory, $"{baseName}_{suffix}.mp4");
            suffix++;
        }

        return path;
    }

    internal static string BuildBaseName(RecordingContext context)
    {
        var timestamp = context.StartedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        return context switch
        {
            ManualRecordingContext => $"{timestamp}_manual",
            ChallengeRecordingContext challenge =>
                $"{timestamp}_mythic-plus_{Sanitize(challenge.DungeonName)}_{challenge.Level}",
            EncounterRecordingContext encounter =>
                $"{timestamp}_raid_{Sanitize(encounter.EncounterName)}_{GetDifficultyName(encounter.DifficultyId)}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(context),
                context,
                "Unknown recording context."
            ),
        };
    }

    internal static string Sanitize(string value)
    {
        var result = new StringBuilder(value.Length);
        var separatorPending = false;

        foreach (var character in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && result.Length > 0)
                {
                    result.Append('-');
                }

                result.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else if (character != '\'' && character != '\u2019')
            {
                separatorPending = true;
            }
        }

        return result.Length > 0 ? result.ToString() : "unknown";
    }

    private static string GetDifficultyName(int difficultyId)
    {
        // Blizzard Difficulty DB2 IDs: https://wago.tools/db2/Difficulty
        return difficultyId switch
        {
            14 => "normal",
            15 => "heroic",
            16 => "mythic",
            17 => "raid-finder",
            _ => $"difficulty-{difficultyId}",
        };
    }
}
