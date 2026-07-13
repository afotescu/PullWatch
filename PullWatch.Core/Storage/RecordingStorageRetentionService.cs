using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class RecordingStorageRetentionService(
    RecordingCatalog catalog,
    ILogger<RecordingStorageRetentionService> logger
)
{
    private readonly RecordingCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ILogger<RecordingStorageRetentionService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<RecordingStorageUsage> GetUsageAsync(
        PullWatchSettings settings,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(settings);

        var recordings = await ListManagedRecordingsAsync(settings, cancellationToken);
        return CreateUsage(recordings);
    }

    public async Task<RecordingStorageCleanupResult> EnforceLimitAsync(
        PullWatchSettings settings,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(settings);

        var recordings = await ListManagedRecordingsAsync(settings, cancellationToken);
        var usage = CreateUsage(recordings);
        var usageBytes = usage.UsageBytes;
        var favoriteUsageBytes = usage.FavoriteUsageBytes;
        var favoriteRecordingCount = usage.FavoriteRecordingCount;
        var maxUsageBytes = settings.Storage.MaxUsageBytes;

        if (maxUsageBytes <= RecordingStorageSettings.UnlimitedBytes || usageBytes <= maxUsageBytes)
        {
            return new RecordingStorageCleanupResult(usage, 0, []);
        }

        var targetBytes = RecordingStorageRetentionPolicy.GetCleanupTargetBytes(maxUsageBytes);
        // Keep the newest recording even when it alone exceeds the storage limit.
        var cleanupCandidates = recordings
            .OrderByDescending(GetRetentionSortKey)
            .ThenByDescending(recording => recording.ModifiedAtUtc)
            .ThenBy(recording => recording.FilePath, StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .OrderBy(recording => recording.IsFavorite)
            .ThenBy(GetRetentionSortKey)
            .ThenBy(recording => recording.FilePath, StringComparer.OrdinalIgnoreCase);
        var deletedCount = 0;
        var errors = new List<Exception>();

        foreach (var recording in cleanupCandidates)
        {
            if (usageBytes <= targetBytes)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _catalog.DeleteAvailableRecordingAsync(
                    recording.Id,
                    settings.RecordingsDirectory!,
                    cancellationToken
                );
                var sizeBytes = Math.Max(0, recording.SizeBytes);
                usageBytes = Math.Max(0, usageBytes - sizeBytes);

                if (recording.IsFavorite)
                {
                    favoriteUsageBytes = Math.Max(0, favoriteUsageBytes - sizeBytes);
                    favoriteRecordingCount = Math.Max(0, favoriteRecordingCount - 1);
                }

                deletedCount++;
                _logger.LogDebug(
                    "Deleted old managed recording {RecordingId} at {FilePath} ({SizeBytes} bytes) during storage retention cleanup",
                    recording.Id,
                    recording.FilePath,
                    recording.SizeBytes
                );
            }
            catch (Exception exception)
                when (exception
                        is IOException
                            or UnauthorizedAccessException
                            or InvalidOperationException
                )
            {
                errors.Add(exception);
                _logger.LogWarning(
                    exception,
                    "Could not delete old recording {RecordingId} during storage retention cleanup",
                    recording.Id
                );
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "Deleted {DeletedCount} old managed recordings during storage retention cleanup; usage is now {UsageBytes} of {MaxUsageBytes} bytes",
                deletedCount,
                usageBytes,
                maxUsageBytes
            );
        }

        return new RecordingStorageCleanupResult(
            new RecordingStorageUsage(
                usageBytes,
                Math.Max(0, recordings.Count - deletedCount),
                favoriteUsageBytes,
                favoriteRecordingCount
            ),
            deletedCount,
            errors
        );
    }

    private async Task<IReadOnlyList<RecordingCatalogFile>> ListManagedRecordingsAsync(
        PullWatchSettings settings,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(settings.RecordingsDirectory))
        {
            return [];
        }

        return await _catalog.ListAvailableFilesAsync(
            settings.RecordingsDirectory,
            cancellationToken
        );
    }

    private static RecordingStorageUsage CreateUsage(IReadOnlyList<RecordingCatalogFile> recordings)
    {
        var favoriteRecordings = recordings.Where(recording => recording.IsFavorite).ToArray();

        return new RecordingStorageUsage(
            SumBytes(recordings),
            recordings.Count,
            SumBytes(favoriteRecordings),
            favoriteRecordings.Length
        );
    }

    private static long SumBytes(IEnumerable<RecordingCatalogFile> recordings)
    {
        var total = 0L;

        foreach (var recording in recordings)
        {
            var sizeBytes = Math.Max(0, recording.SizeBytes);

            if (long.MaxValue - total < sizeBytes)
            {
                return long.MaxValue;
            }

            total += sizeBytes;
        }

        return total;
    }

    private static DateTimeOffset GetRetentionSortKey(RecordingCatalogFile recording)
    {
        return recording.StartedAtUtc ?? recording.EndedAtUtc ?? recording.ModifiedAtUtc;
    }
}

public sealed record RecordingStorageUsage(
    long UsageBytes,
    int RecordingCount,
    long FavoriteUsageBytes = 0,
    int FavoriteRecordingCount = 0
);

public sealed record RecordingStorageCleanupResult(
    RecordingStorageUsage Usage,
    int DeletedCount,
    IReadOnlyList<Exception> Errors
);
