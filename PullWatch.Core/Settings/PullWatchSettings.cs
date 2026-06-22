namespace PullWatch;

public sealed record PullWatchSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string? WowLogsDirectory { get; init; }
    public string? RecordingsDirectory { get; init; }
    public bool RecordMythicPlus { get; init; } = true;
    public bool RecordRaidEncounters { get; init; } = true;
    public VideoSettings Video { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    public StartupSettings Startup { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
}

public sealed record VideoSettings
{
    public VideoQuality Quality { get; init; } = VideoQuality.Balanced;
    public int FrameRate { get; init; } = VideoFrameRates.High;
    public bool CaptureCursor { get; init; } = true;
    public bool ShowCaptureBorder { get; init; }
}

public enum VideoQuality
{
    Compact,
    Balanced,
    High,
}

public static class VideoFrameRates
{
    public const int Standard = 30;
    public const int High = 60;

    public static readonly IReadOnlyList<int> Supported = [Standard, High];

    public static bool IsSupported(int frameRate)
    {
        return frameRate is Standard or High;
    }
}

public sealed record AudioSettings
{
    public bool CaptureSystemAudio { get; init; } = true;
    public bool CaptureMicrophone { get; init; }
}

public sealed record StartupSettings
{
    public bool StartWithWindows { get; init; }
    public bool StartMinimizedToTray { get; init; }
}

public sealed record UiSettings
{
    public WindowPlacementSettings WindowPlacement { get; init; } = new();
    public bool SidebarCollapsed { get; init; }
    public RecordingListCategory SelectedRecordingCategory { get; init; } =
        RecordingListCategory.ChallengeMode;
}

public sealed record WindowPlacementSettings
{
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public bool IsMaximized { get; init; }
}
