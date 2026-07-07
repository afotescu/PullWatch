namespace PullWatch;

internal static class FfmpegVideoOutputSizeCalculator
{
    public static VideoCaptureSize CalculateOutputSize(
        VideoCaptureSize captureSize,
        VideoScaling scaling
    )
    {
        return EnsureEven(VideoOutputSizeCalculator.CalculateOutputSize(captureSize, scaling));
    }

    public static VideoCaptureSize EnsureEven(VideoCaptureSize outputSize)
    {
        if (outputSize.Width <= 0 || outputSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputSize),
                "Output dimensions must be positive."
            );
        }

        return new VideoCaptureSize(
            ToEncoderSafeDimension(outputSize.Width),
            ToEncoderSafeDimension(outputSize.Height)
        );
    }

    private static int ToEncoderSafeDimension(int dimension)
    {
        return dimension % 2 == 0 ? dimension : Math.Max(2, dimension - 1);
    }
}
