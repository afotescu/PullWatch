using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace PullWatch;

public static class FfmpegToolPaths
{
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);

    private const string BundledToolsDirectoryName = "ffmpeg";
    private const string FfmpegExecutableFileName = "ffmpeg.exe";
    private const string FfmpegExecutableName = "ffmpeg";
    private const string PreferredFfmpegPath = @"C:\ffmpeg\bin\ffmpeg.exe";

    public static string ResolveFfmpegPath()
    {
        return ResolveFfmpegPath(AppContext.BaseDirectory);
    }

    internal static string ResolveFfmpegPath(string baseDirectory)
    {
        return ResolveToolPath(
            baseDirectory,
            FfmpegExecutableFileName,
            PreferredFfmpegPath,
            FfmpegExecutableName
        );
    }

    public static async Task<EncoderCalibrationEnvironment> ResolveEnvironmentAsync(
        CancellationToken cancellationToken
    )
    {
        var ffmpegPath = ResolveFfmpegPath();

        return new EncoderCalibrationEnvironment(
            ffmpegPath,
            await TryGetToolVersionAsync(ffmpegPath, cancellationToken),
            await TryGetToolSha256Async(ffmpegPath, cancellationToken)
        );
    }

    internal static string ResolveToolPath(
        string baseDirectory,
        string bundledExecutableFileName,
        string preferredPath,
        string pathExecutableName
    )
    {
        var bundledPath = Path.Combine(
            baseDirectory,
            BundledToolsDirectoryName,
            bundledExecutableFileName
        );

        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        return File.Exists(preferredPath) ? preferredPath : pathExecutableName;
    }

    private static async Task<string?> TryGetToolVersionAsync(
        string toolPath,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo(toolPath);
        startInfo.ArgumentList.Add("-version");

        try
        {
            var result = await ExternalProcessRunner.RunAsync(
                startInfo,
                VersionProbeTimeout,
                cancellationToken
            );

            if (result.ExitCode != 0)
            {
                return null;
            }

            return GetFirstVersionLine(result.StandardOutput)
                ?? GetFirstVersionLine(result.StandardError);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static string? GetFirstVersionLine(string output)
    {
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return null;
    }

    private static async Task<string?> TryGetToolSha256Async(
        string toolPath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (!File.Exists(toolPath))
            {
                return null;
            }

            await using var stream = new FileStream(
                toolPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
