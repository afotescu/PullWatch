namespace PullWatch.Tests;

public sealed class FileSizeFormatterTests
{
    [Theory]
    [InlineData(-1, "0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void UsesBinaryUnitsAndConsistentRounding(long bytes, string expected)
    {
        Assert.Equal(expected, FileSizeFormatter.Format(bytes));
    }
}
