using Microsoft.Extensions.Logging;

namespace PullWatch.Tests;

public sealed class InMemoryLogProviderTests
{
    [Fact]
    public void KeepsOnlyNewestEntriesWithinCapacity()
    {
        using var provider = new InMemoryLogProvider(2);
        var logger = provider.CreateLogger("test");

        logger.LogInformation("first");
        logger.LogWarning("second");
        logger.LogError("third");

        var entries = provider.GetSnapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(["second", "third"], entries.Select(entry => entry.Message));
        Assert.Equal([LogLevel.Warning, LogLevel.Error], entries.Select(entry => entry.Level));
    }
}
