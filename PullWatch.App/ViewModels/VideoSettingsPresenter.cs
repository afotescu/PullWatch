using System.Runtime.InteropServices;

namespace PullWatch;

internal sealed class VideoSettingsPresenter(Func<VideoCaptureSize> getCaptureSize)
{
    private const int PrimaryScreenWidthMetric = 0;
    private const int PrimaryScreenHeightMetric = 1;
    private static readonly VideoCaptureSize FallbackCaptureSize = new(1920, 1080);
    private static readonly TimeSpan EstimateDuration = TimeSpan.FromMinutes(1);

    public IReadOnlyList<VideoScalingOption> GetScalingOptions(VideoScaling selectedScaling)
    {
        var captureSize = getCaptureSize();
        var candidates = new (VideoScaling Value, string Label, VideoCaptureSize OutputSize)[]
        {
            (VideoScaling.Original, FormatScalingOptionLabel("Original", captureSize), captureSize),
            CreateScalingOption("1440p", VideoScaling.Target1440p, captureSize),
            CreateScalingOption("1080p", VideoScaling.Optimized, captureSize),
            CreateScalingOption("720p", VideoScaling.Target720p, captureSize),
        };

        return candidates
            .Where(option =>
                option.Value == VideoScaling.Original
                || option.Value == selectedScaling
                || option.OutputSize != captureSize
            )
            .Select(option => new VideoScalingOption(option.Value, option.Label))
            .ToArray();
    }

    public string GetEstimatedRecordingSize(
        VideoProfileSelection? selectedProfile,
        VideoQuality quality,
        int frameRate,
        VideoScaling scaling,
        bool captureSystemAudio,
        bool captureMicrophone
    )
    {
        var captureSize = getCaptureSize();
        var outputSize = VideoOutputSizeCalculator.CalculateOutputSize(captureSize, scaling);
        var codec = selectedProfile?.Codec ?? VideoCodec.H264;
        var bitrate = VideoBitrateCalculator.CalculateBitrate(
            outputSize,
            frameRate,
            quality,
            codec
        );
        var megabytes = VideoBitrateCalculator.EstimateFileSizeMegabytes(
            bitrate,
            new AudioSettings
            {
                CaptureSystemAudio = captureSystemAudio,
                CaptureMicrophone = captureMicrophone,
            },
            EstimateDuration
        );

        return string.Join(
            " ",
            $"About {FormatEstimatedFileSize(megabytes)} per minute",
            FormatEstimateSizeText(captureSize, outputSize),
            VideoProfileFormatter.FormatCodecName(codec),
            $"{frameRate} FPS",
            $"({VideoBitrateCalculator.ToMegabitsPerSecond(bitrate)} Mbps target).",
            "Actual recording uses the WoW window size."
        );
    }

    public static VideoCaptureSize GetEstimatedCaptureSize()
    {
        return WowWindowCaptureSizeDetector.TryGetCurrentCaptureSize(out var wowCaptureSize)
            ? wowCaptureSize
            : GetPrimaryDisplayCaptureSize();
    }

    private static string FormatEstimatedFileSize(int megabytes)
    {
        if (megabytes >= 1_000)
        {
            return $"{megabytes / 1_000d:0.#} GB";
        }

        var roundedMegabytes = Math.Max(1, (int)Math.Round(megabytes / 10d) * 10);
        return $"{roundedMegabytes} MB";
    }

    private static string FormatEstimateSizeText(
        VideoCaptureSize captureSize,
        VideoCaptureSize outputSize
    )
    {
        return outputSize == captureSize
            ? $"at estimated {FormatCaptureSize(captureSize)} capture,"
            : $"at estimated {FormatCaptureSize(outputSize)} output from {FormatCaptureSize(captureSize)} capture,";
    }

    private static (
        VideoScaling Value,
        string Label,
        VideoCaptureSize OutputSize
    ) CreateScalingOption(string label, VideoScaling scaling, VideoCaptureSize captureSize)
    {
        var outputSize = VideoOutputSizeCalculator.CalculateOutputSize(captureSize, scaling);
        return (scaling, FormatScalingOptionLabel(label, outputSize), outputSize);
    }

    private static string FormatScalingOptionLabel(string label, VideoCaptureSize size)
    {
        return $"{label} ({FormatCaptureSize(size)} estimated)";
    }

    private static string FormatCaptureSize(VideoCaptureSize size)
    {
        return $"{size.Width}x{size.Height}";
    }

    private static VideoCaptureSize GetPrimaryDisplayCaptureSize()
    {
        var width = GetSystemMetrics(PrimaryScreenWidthMetric);
        var height = GetSystemMetrics(PrimaryScreenHeightMetric);

        return width > 0 && height > 0 ? new VideoCaptureSize(width, height) : FallbackCaptureSize;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
