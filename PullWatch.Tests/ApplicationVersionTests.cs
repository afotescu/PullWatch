using System.Reflection;
using System.Reflection.Emit;

namespace PullWatch.Tests;

public sealed class ApplicationVersionTests
{
    [Fact]
    public void GetCurrentUsesInformationalVersionWhenAvailable()
    {
        var assembly = CreateAssembly(
            new Version(1, 2, 3, 4),
            informationalVersion: "2.0.0-beta+build"
        );

        Assert.Equal("2.0.0-beta+build", ApplicationVersion.GetCurrent(assembly));
    }

    [Fact]
    public void GetCurrentFallsBackToAssemblyVersion()
    {
        var assembly = CreateAssembly(new Version(1, 2, 3, 4));

        Assert.Equal("1.2.3.4", ApplicationVersion.GetCurrent(assembly));
    }

    private static Assembly CreateAssembly(Version version, string? informationalVersion = null)
    {
        var name = new AssemblyName($"PullWatch.VersionTests.{Guid.NewGuid():N}")
        {
            Version = version,
        };
        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);

        if (informationalVersion is not null)
        {
            var constructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([
                typeof(string),
            ])!;
            assembly.SetCustomAttribute(
                new CustomAttributeBuilder(constructor, [informationalVersion])
            );
        }

        return assembly;
    }
}
