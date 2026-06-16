namespace PullWatch;

internal static class RecordingFailureClassifier
{
    private const string VisualCRuntimeMessage =
        "Screen recording cannot start because a native recording dependency could not be loaded. " +
        "Install Microsoft Visual C++ Redistributable 2015-2022 x64, then restart PullWatch.";

    private const string NativeArchitectureMessage =
        "Screen recording cannot start because a native recording dependency has the wrong architecture. " +
        "Use the win-x64 PullWatch build and the x64 Visual C++ Redistributable.";

    private const string MediaFoundationMessage =
        "Screen recording cannot start because Windows Media Foundation is unavailable or failed to initialize. " +
        "Install the Media Feature Pack for your Windows edition, then restart PullWatch.";

    public static Exception Classify(Exception exception)
    {
        if (ContainsBadImageFormat(exception))
        {
            return new InvalidOperationException(NativeArchitectureMessage, exception);
        }

        if (ContainsMediaFoundationFailure(exception))
        {
            return new InvalidOperationException(MediaFoundationMessage, exception);
        }

        if (ContainsNativeLoadFailure(exception))
        {
            return new InvalidOperationException(VisualCRuntimeMessage, exception);
        }

        return exception;
    }

    private static bool ContainsBadImageFormat(Exception exception)
    {
        return ContainsException<BadImageFormatException>(exception);
    }

    private static bool ContainsNativeLoadFailure(Exception exception)
    {
        if (ContainsException<DllNotFoundException>(exception))
        {
            return true;
        }

        var text = exception.ToString();
        return text.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ScreenRecorderLib.dll", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("vcruntime", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("msvcp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsMediaFoundationFailure(Exception exception)
    {
        var text = exception.ToString();
        return text.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("mfplat", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Media Feature Pack", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        if (exception is TException)
        {
            return true;
        }

        return exception.InnerException is not null &&
               ContainsException<TException>(exception.InnerException);
    }
}
