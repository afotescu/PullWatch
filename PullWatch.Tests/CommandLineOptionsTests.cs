namespace PullWatch.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void AppliesRuntimeOverrides()
    {
        var parsed = CommandLineOptions.TryParse(
            [
                "--record-now",
                "--wow-logs-directory", @"D:\Wow\Logs",
                "--recordings-directory", @"D:\Videos",
                "--record-mythic-plus", "false",
                "--record-raid-encounters", "true"
            ],
            out var options,
            out var error);

        var settings = options.ApplyTo(new PullWatchSettings());

        Assert.True(parsed, error);
        Assert.True(options.RecordNow);
        Assert.Equal(@"D:\Wow\Logs", settings.WowLogsDirectory);
        Assert.Equal(@"D:\Videos", settings.RecordingsDirectory);
        Assert.False(settings.RecordMythicPlus);
        Assert.True(settings.RecordRaidEncounters);
    }

    [Theory]
    [InlineData("--unknown", null)]
    [InlineData("--wow-logs-directory", null)]
    [InlineData("--wow-logs-directory", "--record-now")]
    [InlineData("--record-mythic-plus", "sometimes")]
    public void RejectsInvalidOptions(string option, string? value)
    {
        var arguments = value is null ? [option] : new[] { option, value };

        var parsed = CommandLineOptions.TryParse(arguments, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
    }
}
