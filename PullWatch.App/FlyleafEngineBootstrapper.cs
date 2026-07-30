using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FlyleafLib;
using Microsoft.Extensions.Logging;
using FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel;
using FlyleafLogLevel = FlyleafLib.LogLevel;

namespace PullWatch;

internal static class FlyleafEngineBootstrapper
{
    private static readonly object Sync = new();

    public static void Start(ILogger logger)
    {
        lock (Sync)
        {
            if (Engine.IsLoaded)
            {
                return;
            }

            var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg");
            if (!Directory.Exists(ffmpegPath))
            {
                throw new DirectoryNotFoundException(
                    $"Flyleaf's FFmpeg libraries were not found at {ffmpegPath}."
                );
            }

            var version = typeof(Engine).Assembly.GetName().Version?.ToString() ?? "unknown";
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation(
                "Starting Flyleaf {FlyleafVersion}; architecture={Architecture}; FFmpegPath={FFmpegPath}",
                version,
                RuntimeInformation.ProcessArchitecture,
                ffmpegPath
            );

            Engine.Start(
                new EngineConfig
                {
                    FFmpegPath = ffmpegPath,
                    LogOutput = ":debug",
                    LogLevel = FlyleafLogLevel.Debug,
                    FFmpegLogLevel = FFmpegLogLevel.Warn,
                    UIRefresh = true,
                    UIRefreshInterval = 100,
                }
            );

            stopwatch.Stop();
            logger.LogInformation(
                "Flyleaf engine started in {ElapsedMilliseconds:F1} ms; loaded={IsLoaded}",
                stopwatch.Elapsed.TotalMilliseconds,
                Engine.IsLoaded
            );
        }
    }
}
