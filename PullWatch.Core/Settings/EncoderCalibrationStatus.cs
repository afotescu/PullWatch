namespace PullWatch;

public enum EncoderCalibrationStatusKind
{
    Valid,
    Missing,
    Stale,
}

public sealed record EncoderCalibrationEnvironment(
    string FfmpegPath,
    string? FfmpegVersion,
    string? FfmpegSha256 = null
);

public sealed record EncoderCalibrationStatus(EncoderCalibrationStatusKind Kind, string Message)
{
    public bool IsValid => Kind == EncoderCalibrationStatusKind.Valid;
}

public static class EncoderCalibrationStatusEvaluator
{
    public static EncoderCalibrationStatus Evaluate(
        PullWatchSettings settings,
        EncoderCalibrationEnvironment environment
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);

        var calibration = settings.EncoderCalibration;
        var selectedProfile = settings.Video.SelectedProfile;

        if (calibration.Results.Count == 0)
        {
            return Missing("Video encoding needs to be tested before recording.");
        }

        if (selectedProfile is null)
        {
            return Missing("No tested video encoder profile has been selected.");
        }

        if (calibration.Version != EncoderCalibrationSettings.CurrentVersion)
        {
            return Stale("Video encoding needs to be retested after an app update.");
        }

        if (HasExecutableFingerprintChanged(calibration, environment))
        {
            return Stale(
                "Video encoding needs to be retested because the FFmpeg executable changed."
            );
        }

        if (
            !HasMatchingExecutableFingerprint(calibration, environment)
            && !PathsEqual(calibration.FfmpegPath, environment.FfmpegPath)
        )
        {
            return Stale("Video encoding needs to be retested because the FFmpeg path changed.");
        }

        if (!StringComparer.Ordinal.Equals(calibration.FfmpegVersion, environment.FfmpegVersion))
        {
            return Stale("Video encoding needs to be retested because the FFmpeg version changed.");
        }

        var selectedResult = calibration.Results.FirstOrDefault(result =>
            result.Codec == selectedProfile.Codec && result.Provider == selectedProfile.Provider
        );

        if (selectedResult is null)
        {
            return Stale("The selected video encoder profile has not been tested.");
        }

        if (!selectedResult.Passed)
        {
            return Stale("The selected video encoder profile did not pass testing.");
        }

        return new EncoderCalibrationStatus(
            EncoderCalibrationStatusKind.Valid,
            "Video encoding is ready."
        );
    }

    private static EncoderCalibrationStatus Missing(string message)
    {
        return new EncoderCalibrationStatus(EncoderCalibrationStatusKind.Missing, message);
    }

    private static EncoderCalibrationStatus Stale(string message)
    {
        return new EncoderCalibrationStatus(EncoderCalibrationStatusKind.Stale, message);
    }

    private static bool PathsEqual(string? left, string right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }

    private static bool HasMatchingExecutableFingerprint(
        EncoderCalibrationSettings calibration,
        EncoderCalibrationEnvironment environment
    )
    {
        return !string.IsNullOrWhiteSpace(calibration.FfmpegSha256)
            && !string.IsNullOrWhiteSpace(environment.FfmpegSha256)
            && StringComparer.OrdinalIgnoreCase.Equals(
                calibration.FfmpegSha256,
                environment.FfmpegSha256
            );
    }

    private static bool HasExecutableFingerprintChanged(
        EncoderCalibrationSettings calibration,
        EncoderCalibrationEnvironment environment
    )
    {
        return !string.IsNullOrWhiteSpace(calibration.FfmpegSha256)
            && !string.IsNullOrWhiteSpace(environment.FfmpegSha256)
            && !StringComparer.OrdinalIgnoreCase.Equals(
                calibration.FfmpegSha256,
                environment.FfmpegSha256
            );
    }
}
