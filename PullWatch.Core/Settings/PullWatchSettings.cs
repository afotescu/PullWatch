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
    public UiSettings Ui { get; init; } = new();
}

public sealed record VideoSettings
{
    public int Bitrate { get; init; } = 12_000_000;
    public int FrameRate { get; init; } = 60;
    public bool CaptureCursor { get; init; } = true;
    public bool ShowCaptureBorder { get; init; }
    public bool CaptureMainDisplayForManualRecordings { get; init; }
}

public sealed record AudioSettings
{
    public bool CaptureSystemAudio { get; init; } = true;
    public bool CaptureMicrophone { get; init; }
}

public sealed record UiSettings
{
    public WindowPlacementSettings WindowPlacement { get; init; } = new();
}

public sealed record WindowPlacementSettings
{
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public bool IsMaximized { get; init; }
}
