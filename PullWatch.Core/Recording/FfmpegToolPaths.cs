namespace PullWatch;

internal static class FfmpegToolPaths
{
    private const string FfmpegExecutableName = "ffmpeg";
    private const string FfprobeExecutableName = "ffprobe";
    private const string PreferredFfmpegPath = @"C:\ffmpeg\bin\ffmpeg.exe";
    private const string PreferredFfprobePath = @"C:\ffmpeg\bin\ffprobe.exe";

    public static string ResolveFfmpegPath()
    {
        return File.Exists(PreferredFfmpegPath) ? PreferredFfmpegPath : FfmpegExecutableName;
    }

    public static string ResolveFfprobePath()
    {
        return File.Exists(PreferredFfprobePath) ? PreferredFfprobePath : FfprobeExecutableName;
    }
}
