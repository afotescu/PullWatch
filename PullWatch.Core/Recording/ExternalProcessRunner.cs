using System.ComponentModel;
using System.Diagnostics;

namespace PullWatch;

internal static class ExternalProcessRunner
{
    private static readonly TimeSpan KillExitTimeout = TimeSpan.FromSeconds(3);

    public static async Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? operationDescription = null
    )
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );
        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(startInfo.FileName)} process did not start."
            );
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(combinedCancellation.Token);
        }
        catch (OperationCanceledException exception)
        {
            await TryKillAndWaitForExitAsync(process);
            cancellationToken.ThrowIfCancellationRequested();

            var description = operationDescription ?? Path.GetFileName(startInfo.FileName);
            throw new TimeoutException(
                $"{description} did not finish within {timeout}.",
                exception
            );
        }

        return new ExternalProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError
        );
    }

    private static async Task TryKillAndWaitForExitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (!process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(KillExitTimeout);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception or TimeoutException)
        {
            // The caller still receives its timeout or cancellation after best-effort cleanup.
        }
    }
}

internal sealed record ExternalProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError
);
