namespace PullWatch;

public static class VideoOutputSizeCalculator
{
    private const int Target1440pDimension = 1440;
    private const int Target1080pDimension = 1080;
    private const int Target720pDimension = 720;

    public static VideoCaptureSize CalculateOutputSize(
        VideoCaptureSize captureSize,
        VideoScaling scaling
    )
    {
        if (captureSize.Width <= 0 || captureSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureSize),
                "Capture dimensions must be positive."
            );
        }

        return scaling switch
        {
            VideoScaling.Original => captureSize,
            VideoScaling.Target1440p => CalculateTargetOutputSize(
                captureSize,
                Target1440pDimension
            ),
            VideoScaling.Optimized => CalculateTargetOutputSize(captureSize, Target1080pDimension),
            VideoScaling.Target720p => CalculateTargetOutputSize(captureSize, Target720pDimension),
            _ => throw new ArgumentOutOfRangeException(nameof(scaling), scaling, null),
        };
    }

    private static VideoCaptureSize CalculateTargetOutputSize(
        VideoCaptureSize captureSize,
        int targetDimension
    )
    {
        if (captureSize.Width >= captureSize.Height)
        {
            var outputHeight = Math.Min(captureSize.Height, targetDimension);
            return ScaleToHeight(captureSize, outputHeight);
        }

        var outputWidth = Math.Min(captureSize.Width, targetDimension);
        return ScaleToWidth(captureSize, outputWidth);
    }

    private static VideoCaptureSize ScaleToHeight(VideoCaptureSize captureSize, int outputHeight)
    {
        if (outputHeight == captureSize.Height)
        {
            return captureSize;
        }

        return new VideoCaptureSize(
            RoundToEven(captureSize.Width * (double)outputHeight / captureSize.Height),
            outputHeight
        );
    }

    private static VideoCaptureSize ScaleToWidth(VideoCaptureSize captureSize, int outputWidth)
    {
        if (outputWidth == captureSize.Width)
        {
            return captureSize;
        }

        return new VideoCaptureSize(
            outputWidth,
            RoundToEven(captureSize.Height * (double)outputWidth / captureSize.Width)
        );
    }

    private static int RoundToEven(double value)
    {
        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded % 2 == 0 ? rounded : Math.Max(2, rounded - 1);
    }
}
