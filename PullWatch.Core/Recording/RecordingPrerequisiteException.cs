namespace PullWatch;

public sealed class RecordingPrerequisiteException : InvalidOperationException
{
    public RecordingPrerequisiteException(string message)
        : base(message)
    {
    }

    public RecordingPrerequisiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal static bool TryCreateForRecorderStartup(
        Exception exception,
        out RecordingPrerequisiteException prerequisiteException)
    {
        prerequisiteException = null!;

        if (exception is DllNotFoundException)
        {
            prerequisiteException = new RecordingPrerequisiteException(
                "Screen recording cannot start because a native recording dependency is missing. " +
                "Install Microsoft Visual C++ Redistributable 2015-2022 x64, then restart PullWatch.",
                exception);
            return true;
        }

        if (exception is BadImageFormatException)
        {
            prerequisiteException = new RecordingPrerequisiteException(
                "Screen recording cannot start because a native recording dependency has the wrong architecture. " +
                "Use the win-x64 PullWatch build and the x64 Visual C++ Redistributable.",
                exception);
            return true;
        }

        return false;
    }
}
