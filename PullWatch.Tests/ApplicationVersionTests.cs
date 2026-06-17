namespace PullWatch.Tests;

public sealed class ApplicationVersionTests
{
    [Fact]
    public void CurrentUsesAssemblyInformationalVersion()
    {
        Assert.Equal(
            typeof(ApplicationVersion).Assembly
                .GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Single()
                .InformationalVersion,
            ApplicationVersion.Current);
    }
}
