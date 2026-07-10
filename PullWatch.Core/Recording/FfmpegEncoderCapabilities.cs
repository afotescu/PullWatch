using System.Diagnostics;
using System.Text;

namespace PullWatch;

internal sealed class FfmpegEncoderCapabilities
{
    private readonly HashSet<string> _encoderNames;

    public FfmpegEncoderCapabilities(IEnumerable<string> encoderNames)
    {
        _encoderNames = new HashSet<string>(
            encoderNames.Where(encoderName => !string.IsNullOrWhiteSpace(encoderName)),
            StringComparer.OrdinalIgnoreCase
        );
    }

    public int Count => _encoderNames.Count;

    public IReadOnlyList<string> EncoderNames =>
        _encoderNames.Order(StringComparer.Ordinal).ToArray();

    public bool Contains(string encoderName)
    {
        return _encoderNames.Contains(encoderName);
    }

    public static FfmpegEncoderCapabilities Parse(string encoderListOutput)
    {
        ArgumentNullException.ThrowIfNull(encoderListOutput);

        var encoderNames = new List<string>();
        using var reader = new StringReader(encoderListOutput);

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('V'))
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                encoderNames.Add(parts[1]);
            }
        }

        return new FfmpegEncoderCapabilities(encoderNames);
    }
}

internal static class FfmpegEncoderCapabilityDetector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<FfmpegEncoderCapabilities> DetectUsableAsync(
        string ffmpegPath,
        IReadOnlyCollection<string> candidateEncoderNames,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(candidateEncoderNames);

        var compiledCapabilities = await DetectCompiledAsync(ffmpegPath, cancellationToken);
        var usableEncoderNames = new List<string>();

        foreach (var encoderName in candidateEncoderNames)
        {
            if (!compiledCapabilities.Contains(encoderName))
            {
                continue;
            }

            if (
                !RequiresHardwareProbe(encoderName)
                || await ProbeEncoderAsync(ffmpegPath, encoderName, cancellationToken)
            )
            {
                usableEncoderNames.Add(encoderName);
            }
        }

        return new FfmpegEncoderCapabilities(usableEncoderNames);
    }

    private static async Task<FfmpegEncoderCapabilities> DetectCompiledAsync(
        string ffmpegPath,
        CancellationToken cancellationToken
    )
    {
        var result = await RunFfmpegAsync(
            ffmpegPath,
            ["-hide_banner", "-encoders"],
            cancellationToken
        );
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg encoder capability detection failed with exit code {result.ExitCode}.{FormatProcessOutput(result)}"
            );
        }

        return FfmpegEncoderCapabilities.Parse(
            $"{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
        );
    }

    private static async Task<bool> ProbeEncoderAsync(
        string ffmpegPath,
        string encoderName,
        CancellationToken cancellationToken
    )
    {
        // Listing encoders only proves that FFmpeg was compiled with the wrapper.
        // Hardware wrappers still need a small initialization probe to catch missing GPUs/drivers.
        ExternalProcessResult result;
        try
        {
            result = await RunFfmpegAsync(
                ffmpegPath,
                [
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    "color=c=black:s=256x256:r=1",
                    "-frames:v",
                    "1",
                    "-an",
                    "-c:v",
                    encoderName,
                    "-f",
                    "null",
                    "-",
                ],
                cancellationToken
            );
        }
        catch (TimeoutException)
        {
            return false;
        }

        return result.ExitCode == 0;
    }

    private static bool RequiresHardwareProbe(string encoderName)
    {
        return encoderName.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)
            || encoderName.EndsWith("_amf", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ExternalProcessResult> RunFfmpegAsync(
        string ffmpegPath,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken
    )
    {
        return await ExternalProcessRunner.RunAsync(
            CreateProbeStartInfo(ffmpegPath, arguments),
            ProbeTimeout,
            cancellationToken,
            "FFmpeg encoder capability check"
        );
    }

    private static ProcessStartInfo CreateProbeStartInfo(
        string ffmpegPath,
        IEnumerable<string> arguments
    )
    {
        var startInfo = new ProcessStartInfo(ffmpegPath);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string FormatProcessOutput(ExternalProcessResult result)
    {
        var output = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            output.AppendLine();
            output.Append(result.StandardError.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            output.AppendLine();
            output.Append(result.StandardOutput.Trim());
        }

        return output.ToString();
    }
}
