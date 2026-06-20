using System.Globalization;

namespace PullWatch;

internal static class StorageTimestampFormatter
{
    public static string FormatUtc(DateTimeOffset timestamp)
    {
        return timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset ParseUtc(string timestamp)
    {
        var value = DateTimeOffset.Parse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
        );

        return value;
    }

    public static string? FormatNullableUtc(DateTimeOffset? timestamp)
    {
        return timestamp is null ? null : FormatUtc(timestamp.Value);
    }

    public static DateTimeOffset? ParseNullableUtc(string? timestamp)
    {
        return timestamp is null ? null : ParseUtc(timestamp);
    }
}
