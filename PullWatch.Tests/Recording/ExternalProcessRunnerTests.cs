using System.ComponentModel;
using System.Diagnostics;

namespace PullWatch.Tests;

public sealed class ExternalProcessRunnerTests
{
    [Fact]
    public async Task RunAsyncCapturesOutputAndExitCode()
    {
        var result = await ExternalProcessRunner.RunAsync(
            CreateCommand("echo standard-output & echo standard-error 1>&2 & exit /b 7"),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("standard-output", result.StandardOutput);
        Assert.Contains("standard-error", result.StandardError);
    }

    [Fact]
    public async Task RunAsyncDrainsRedirectedOutputWhileProcessRuns()
    {
        var result = await ExternalProcessRunner.RunAsync(
            CreateCommand("for /L %i in (1,1,20000) do @echo line-%i"),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("line-20000", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsyncThrowsTimeoutExceptionWhenTimeoutExpires()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ExternalProcessRunner.RunAsync(
                CreateLongRunningCommand(),
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken,
                "Test process"
            )
        );

        Assert.StartsWith("Test process did not finish within", exception.Message);
    }

    [Fact]
    public async Task RunAsyncPropagatesCallerCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExternalProcessRunner.RunAsync(
                CreateLongRunningCommand(),
                TimeSpan.FromSeconds(10),
                cancellation.Token
            )
        );
    }

    [Fact]
    public async Task RunAsyncPrefersPreexistingCallerCancellationOverTimeout()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExternalProcessRunner.RunAsync(
                CreateLongRunningCommand(),
                TimeSpan.Zero,
                cancellation.Token
            )
        );
    }

    [Fact]
    public async Task RunAsyncPropagatesProcessStartFailure()
    {
        var startInfo = new ProcessStartInfo($"missing-process-{Guid.NewGuid():N}.exe");

        await Assert.ThrowsAsync<Win32Exception>(() =>
            ExternalProcessRunner.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            )
        );
    }

    private static ProcessStartInfo CreateCommand(string command)
    {
        var commandPath =
            Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo(commandPath);
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static ProcessStartInfo CreateLongRunningCommand()
    {
        return CreateCommand("ping 127.0.0.1 -n 30 > nul");
    }
}
