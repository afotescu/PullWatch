namespace PullWatch.Tests;

public sealed class RecordingFilenameBuilderTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 6, 15, 0, 15, 10, TimeSpan.Zero);

    [Fact]
    public void BuildsMeaningfulNamesForEveryRecordingType()
    {
        Assert.Equal(
            "manual_20260615_001510",
            RecordingFilenameBuilder.BuildBaseName(new ManualRecordingContext(StartedAt)));
        Assert.Equal(
            "mythic-plus_magisters-terrace_22_20260615_001510",
            RecordingFilenameBuilder.BuildBaseName(new ChallengeRecordingContext(
                StartedAt,
                "Magisters' Terrace",
                22)));
        Assert.Equal(
            "raid_plexus-sentinel_mythic_20260615_001510",
            RecordingFilenameBuilder.BuildBaseName(new EncounterRecordingContext(
                StartedAt,
                3129,
                "Plexus Sentinel",
                16)));
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
            "raid_boss_difficulty-99_20260615_001510",
            RecordingFilenameBuilder.BuildBaseName(context));
    }

    [Fact]
    public void AppendsSuffixWhenDesiredPathExists()
    {
        var directory = Directory.CreateTempSubdirectory("PullWatchTests-");
        var context = new ManualRecordingContext(StartedAt);

        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "manual_20260615_001510.mp4"), "");
            File.WriteAllText(Path.Combine(directory.FullName, "manual_20260615_001510_2.mp4"), "");

            var path = RecordingFilenameBuilder.CreateAvailablePath(directory.FullName, context);

            Assert.Equal(
                Path.Combine(directory.FullName, "manual_20260615_001510_3.mp4"),
                path);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
