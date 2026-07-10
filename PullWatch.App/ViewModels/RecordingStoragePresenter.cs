namespace PullWatch;

internal sealed class RecordingStoragePresenter(RecordingStorageStatus initialStatus)
{
    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    public const int MaximumLimitGigabytes = 10_000;

    public static int DefaultLimitGigabytes { get; } =
        (int)(RecordingStorageSettings.DefaultMaxUsageBytes / BytesPerGigabyte);

    public RecordingStorageStatus Status { get; private set; } = initialStatus;

    public bool ApplyStatus(RecordingStorageStatus status)
    {
        if (Status == status)
        {
            return false;
        }

        Status = status;
        return true;
    }

    public string GetUsageText(bool isLimitEnabled, long limitBytes)
    {
        var usageText = Status.UsageBytes is { } usageBytes
            ? FileSizeFormatter.Format(usageBytes)
            : "Calculating";
        var limitText = isLimitEnabled ? FileSizeFormatter.Format(limitBytes) : "Unlimited";

        return $"Managed recordings storage: {usageText} / {limitText}";
    }

    public string GetStatusText(bool isLimitEnabled, long limitBytes)
    {
        if (Status.LastError is not null)
        {
            return $"Could not read managed recordings storage: {Status.LastError.Message}";
        }

        if (Status.IsCleaning)
        {
            return "Cleaning up old recordings...";
        }

        if (Status.IsRefreshing)
        {
            return "Calculating managed recordings storage...";
        }

        if (Status.UsageBytes is null)
        {
            return "Managed recordings storage has not been scanned yet.";
        }

        if (!isLimitEnabled)
        {
            return "Storage limit is disabled. PullWatch-owned recordings are still counted.";
        }

        if (IsOverLimit(isLimitEnabled, limitBytes))
        {
            return "Managed recordings are over the configured limit.";
        }

        if (IsNearLimit(isLimitEnabled, limitBytes))
        {
            return "Managed recordings are close to the configured limit.";
        }

        return "Oldest managed recordings are removed first when the limit is reached.";
    }

    public double GetUsagePercent(long limitBytes)
    {
        return limitBytes > 0 && Status.UsageBytes is { } usageBytes
            ? Math.Clamp(usageBytes * 100d / limitBytes, 0, 100)
            : 0;
    }

    public bool IsUsageIndeterminate(bool isLimitEnabled)
    {
        return isLimitEnabled
            && Status.UsageBytes is null
            && (Status.IsRefreshing || Status.IsCleaning);
    }

    public bool IsOverLimit(bool isLimitEnabled, long limitBytes)
    {
        return isLimitEnabled && Status.UsageBytes is { } usageBytes && usageBytes > limitBytes;
    }

    public bool IsNearLimit(bool isLimitEnabled, long limitBytes)
    {
        return isLimitEnabled
            && !IsOverLimit(isLimitEnabled, limitBytes)
            && Status.UsageBytes is { } usageBytes
            && usageBytes >= limitBytes * 0.85d;
    }

    public static long GigabytesToBytes(int gigabytes)
    {
        return Math.Clamp(gigabytes, 1, MaximumLimitGigabytes) * BytesPerGigabyte;
    }

    public static int BytesToGigabytes(long bytes)
    {
        if (bytes <= 0)
        {
            return DefaultLimitGigabytes;
        }

        return Math.Clamp(
            (int)Math.Ceiling(bytes / (double)BytesPerGigabyte),
            1,
            MaximumLimitGigabytes
        );
    }
}
