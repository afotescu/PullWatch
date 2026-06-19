namespace PullWatch;

public static class DiagnosticsValueFormatter
{
    public static string Format(object? value)
    {
        return value?.ToString() ?? "(none)";
    }
}
