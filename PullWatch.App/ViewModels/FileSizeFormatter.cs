namespace PullWatch;

internal static class FileSizeFormatter
{
    private const double BytesPerKilobyte = 1024;
    private const double BytesPerMegabyte = BytesPerKilobyte * 1024;
    private const double BytesPerGigabyte = BytesPerMegabyte * 1024;

    public static string Format(long bytes)
    {
        bytes = Math.Max(0, bytes);

        if (bytes >= BytesPerGigabyte)
        {
            return $"{bytes / BytesPerGigabyte:0.#} GB";
        }

        if (bytes >= BytesPerMegabyte)
        {
            return $"{bytes / BytesPerMegabyte:0.#} MB";
        }

        return $"{bytes / BytesPerKilobyte:0.#} KB";
    }
}
