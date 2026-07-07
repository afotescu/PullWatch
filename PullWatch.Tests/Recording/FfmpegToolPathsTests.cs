namespace PullWatch.Tests;

public sealed class FfmpegToolPathsTests
{
    [Fact]
    public void ResolveFfmpegPathPrefersBundledTool()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var bundledDirectory = Path.Combine(baseDirectory, "ffmpeg");
            var bundledPath = Path.Combine(bundledDirectory, "ffmpeg.exe");
            Directory.CreateDirectory(bundledDirectory);
            File.WriteAllText(bundledPath, string.Empty);

            Assert.Equal(bundledPath, FfmpegToolPaths.ResolveFfmpegPath(baseDirectory));
        }
        finally
        {
            DeleteDirectory(baseDirectory);
        }
    }

    [Fact]
    public void ResolveFfprobePathPrefersBundledTool()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var bundledDirectory = Path.Combine(baseDirectory, "ffmpeg");
            var bundledPath = Path.Combine(bundledDirectory, "ffprobe.exe");
            Directory.CreateDirectory(bundledDirectory);
            File.WriteAllText(bundledPath, string.Empty);

            Assert.Equal(bundledPath, FfmpegToolPaths.ResolveFfprobePath(baseDirectory));
        }
        finally
        {
            DeleteDirectory(baseDirectory);
        }
    }

    [Fact]
    public void ResolveToolPathFallsBackToPreferredPath()
    {
        var baseDirectory = CreateTempDirectory();
        var preferredDirectory = CreateTempDirectory();

        try
        {
            var preferredPath = Path.Combine(preferredDirectory, "ffmpeg.exe");
            File.WriteAllText(preferredPath, string.Empty);

            var resolvedPath = FfmpegToolPaths.ResolveToolPath(
                baseDirectory,
                "ffmpeg.exe",
                preferredPath,
                "ffmpeg"
            );

            Assert.Equal(preferredPath, resolvedPath);
        }
        finally
        {
            DeleteDirectory(baseDirectory);
            DeleteDirectory(preferredDirectory);
        }
    }

    [Fact]
    public void ResolveToolPathFallsBackToPathExecutableName()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var missingPreferredPath = Path.Combine(baseDirectory, "missing", "ffmpeg.exe");

            var resolvedPath = FfmpegToolPaths.ResolveToolPath(
                baseDirectory,
                "ffmpeg.exe",
                missingPreferredPath,
                "ffmpeg"
            );

            Assert.Equal("ffmpeg", resolvedPath);
        }
        finally
        {
            DeleteDirectory(baseDirectory);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PullWatch-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
