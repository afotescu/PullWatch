namespace PullWatch.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecutionFailureIsRetainedAndReportedWithoutEscaping()
    {
        var failure = new InvalidOperationException("command failed");
        Exception? reportedFailure = null;
        var command = new AsyncRelayCommand(
            () => Task.FromException(failure),
            onException: exception => reportedFailure = exception
        );

        await command.ExecuteAsync();

        Assert.Same(failure, command.LastException);
        Assert.Same(failure, reportedFailure);
        Assert.False(command.IsExecuting);
    }

    [Fact]
    public async Task SuccessfulExecutionClearsPreviousFailure()
    {
        var shouldFail = true;
        var command = new AsyncRelayCommand(() =>
        {
            if (shouldFail)
            {
                throw new InvalidOperationException("command failed");
            }

            return Task.CompletedTask;
        });

        await command.ExecuteAsync();
        shouldFail = false;
        await command.ExecuteAsync();

        Assert.Null(command.LastException);
    }

    [Fact]
    public async Task ExceptionReporterFailureDoesNotEscape()
    {
        var command = new AsyncRelayCommand(
            () => throw new InvalidOperationException("command failed"),
            onException: _ => throw new InvalidOperationException("reporting failed")
        );

        await command.ExecuteAsync();

        var failure = Assert.IsType<AggregateException>(command.LastException);
        Assert.Equal(2, failure.InnerExceptions.Count);
    }
}
