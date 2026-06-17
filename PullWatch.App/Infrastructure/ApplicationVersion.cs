using System.Reflection;

namespace PullWatch;

public static class ApplicationVersion
{
    public static string Current => GetCurrent(typeof(ApplicationVersion).Assembly);

    internal static string GetCurrent(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
