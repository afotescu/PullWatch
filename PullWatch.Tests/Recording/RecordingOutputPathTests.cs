namespace PullWatch.Tests;

public sealed class RecordingOutputPathTests
{
    [Fact]
    public void CreateReportsUnavailableRecordingsDirectory()
    {
        using var directory = new TemporaryDirectory();
        var blockedPath = Path.Combine(directory.Path, "blocked");
        File.WriteAllText(blockedPath, "not a directory");
        var settings = new PullWatchSettings { RecordingsDirectory = blockedPath };

        var exception = Assert.Throws<RecordingOutputUnavailableException>(() =>
            RecordingOutputPath.Create(new ManualRecordingContext(DateTimeOffset.Now), settings)
        );

        Assert.Equal(Path.GetFullPath(blockedPath), exception.RecordingsDirectory);
        Assert.Contains("recordings folder is unavailable", exception.Message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchRecordingOutputPathTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
