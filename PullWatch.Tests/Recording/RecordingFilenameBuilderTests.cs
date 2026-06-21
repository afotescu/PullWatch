namespace PullWatch.Tests;

public sealed class RecordingFilenameBuilderTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 6, 15, 0, 15, 10, TimeSpan.Zero);

    [Fact]
    public void BuildsMeaningfulNamesForEveryRecordingType()
    {
        Assert.Equal(
            "20260615_001510_manual",
            RecordingFilenameBuilder.BuildBaseName(new ManualRecordingContext(StartedAt))
        );
        Assert.Equal(
            "20260615_001510_mythic-plus_magisters-terrace_22",
            RecordingFilenameBuilder.BuildBaseName(
                new ChallengeRecordingContext(
                    StartedAt,
                    "Magisters' Terrace",
                    2811,
                    558,
                    22,
                    [9, 10, 147]
                )
            )
        );
        Assert.Equal(
            "20260615_001510_raid_plexus-sentinel_mythic",
            RecordingFilenameBuilder.BuildBaseName(
                new EncounterRecordingContext(
                    StartedAt,
                    3129,
                    "Plexus Sentinel",
                    WowDifficultyIds.MythicRaid
                )
            )
        );
        Assert.Equal(
            "20260615_001510_raid_rotmire_mythic",
            RecordingFilenameBuilder.BuildBaseName(
                new EncounterRecordingContext(
                    StartedAt,
                    3159,
                    "Rotmire",
                    WowDifficultyIds.FlexibleMythicRaid
                )
            )
        );
    }

    [Theory]
    [InlineData("Queen Ansurek", "queen-ansurek")]
    [InlineData("Magisters' Terrace", "magisters-terrace")]
    [InlineData("Ara-Kara, City of Echoes", "ara-kara-city-of-echoes")]
    [InlineData("Cité des Fils", "cité-des-fils")]
    [InlineData("<>:", "unknown")]
    public void SanitizesNames(string value, string expected)
    {
        Assert.Equal(expected, RecordingFilenameBuilder.Sanitize(value));
    }

    [Fact]
    public void UsesNumericDifficultyFallback()
    {
        var context = new EncounterRecordingContext(StartedAt, 1, "Boss", 99);

        Assert.Equal(
            "20260615_001510_raid_boss_difficulty-99",
            RecordingFilenameBuilder.BuildBaseName(context)
        );
    }

    [Fact]
    public void AppendsSuffixWhenDesiredPathExists()
    {
        var directory = Directory.CreateTempSubdirectory("PullWatchTests-");
        var context = new ManualRecordingContext(StartedAt);

        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "20260615_001510_manual.mp4"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "20260615_001510_manual_2.mp4"), "");

            var path = RecordingFilenameBuilder.CreateAvailablePath(directory.FullName, context);

            Assert.Equal(Path.Combine(directory.FullName, "20260615_001510_manual_3.mp4"), path);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
