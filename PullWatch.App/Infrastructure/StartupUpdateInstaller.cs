using Microsoft.Extensions.Logging;

namespace PullWatch;

internal static class StartupUpdateInstaller
{
    public static bool TryApplyPendingUpdateAndRestart(
        IApplicationUpdater updater,
        Action requestShutdown,
        ILogger logger
    )
    {
        var pendingUpdate = updater.PendingUpdate;

        if (pendingUpdate is null)
        {
            return false;
        }

        try
        {
            logger.LogInformation(
                "Applying pending PullWatch update {UpdateVersion} before startup.",
                pendingUpdate.Version
            );
            updater.WaitForExitThenApplyUpdateAndRestart(pendingUpdate);
            requestShutdown();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not automatically apply pending PullWatch update {UpdateVersion}.",
                pendingUpdate.Version
            );
            return false;
        }
    }
}
