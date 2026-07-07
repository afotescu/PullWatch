using System.ComponentModel;
using System.Diagnostics;

namespace PullWatch;

public static class FfmpegToolPaths
{
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);

    private const string BundledToolsDirectoryName = "ffmpeg";
    private const string FfmpegExecutableFileName = "ffmpeg.exe";
    private const string FfprobeExecutableFileName = "ffprobe.exe";
    private const string FfmpegExecutableName = "ffmpeg";
    private const string FfprobeExecutableName = "ffprobe";
    private const string PreferredFfmpegPath = @"C:\ffmpeg\bin\ffmpeg.exe";
    private const string PreferredFfprobePath = @"C:\ffmpeg\bin\ffprobe.exe";

    public static string ResolveFfmpegPath()
    {
        return ResolveFfmpegPath(AppContext.BaseDirectory);
    }

    public static string ResolveFfprobePath()
    {
        return ResolveFfprobePath(AppContext.BaseDirectory);
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

    internal static string ResolveFfprobePath(string baseDirectory)
    {
        return ResolveToolPath(
            baseDirectory,
            FfprobeExecutableFileName,
            PreferredFfprobePath,
            FfprobeExecutableName
        );
    }

    public static async Task<EncoderCalibrationEnvironment> ResolveEnvironmentAsync(
        CancellationToken cancellationToken
    )
    {
        var ffmpegPath = ResolveFfmpegPath();
        var ffprobePath = ResolveFfprobePath();

        return new EncoderCalibrationEnvironment(
            ffmpegPath,
            await TryGetToolVersionAsync(ffmpegPath, cancellationToken),
            ffprobePath,
            await TryGetToolVersionAsync(ffprobePath, cancellationToken)
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
        using var timeoutCancellation = new CancellationTokenSource(VersionProbeTimeout);
        using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(toolPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-version");

        try
        {
            if (!process.Start())
            {
                return null;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(combinedCancellation.Token);

            if (process.ExitCode != 0)
            {
                return null;
            }

            return GetFirstVersionLine(await standardOutput)
                ?? GetFirstVersionLine(await standardError);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            TryKill(process);
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process exited while cleanup was running.
        }
    }
}
