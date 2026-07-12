using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace PullWatch;

internal interface IApplicationUpdate
{
    string Version { get; }
    long SizeBytes { get; }
    string? ReleaseNotesMarkdown { get; }
}

internal interface IApplicationUpdater
{
    bool CanCheckForUpdates { get; }
    IApplicationUpdate? PendingUpdate { get; }

    Task<IApplicationUpdate?> CheckForUpdatesAsync(CancellationToken cancellationToken);

    Task DownloadUpdateAsync(
        IApplicationUpdate update,
        IProgress<int> progress,
        CancellationToken cancellationToken
    );

    void WaitForExitThenApplyUpdateAndRestart(IApplicationUpdate update);
}

internal sealed class ApplicationUpdaterUnavailableException(
    string message,
    Exception? innerException = null
) : Exception(message, innerException);

internal sealed class ApplicationUpdateException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class VelopackApplicationUpdater : IApplicationUpdater
{
    public const string UpdateSourceEnvironmentVariable = "PULLWATCH_UPDATE_SOURCE";
    public const string IncludePrereleaseUpdatesEnvironmentVariable =
        "PULLWATCH_INCLUDE_PRERELEASE_UPDATES";

    private const string RepositoryUrl = "https://github.com/afotescu/PullWatch";

    private readonly UpdateManager _manager;

    public VelopackApplicationUpdater()
        : this(
            CreateUpdateManager(
                Environment.GetEnvironmentVariable(UpdateSourceEnvironmentVariable),
                ShouldIncludePrereleaseUpdates(
                    Environment.GetEnvironmentVariable(IncludePrereleaseUpdatesEnvironmentVariable)
                )
            )
        ) { }

    private VelopackApplicationUpdater(UpdateManager manager)
    {
        _manager = manager;
    }

    public bool CanCheckForUpdates => _manager.IsInstalled;

    public IApplicationUpdate? PendingUpdate =>
        _manager.UpdatePendingRestart is { } update ? new PendingVelopackUpdate(update) : null;

    public IApplicationUpdate? GetCurrentRelease(SemanticVersion restartedVersion)
    {
        var currentVersion = _manager.CurrentVersion;
        var localRelease = VelopackLocator.Current.GetLatestLocalFullPackage();

        return IsCurrentRestartedRelease(restartedVersion, currentVersion, localRelease?.Version)
            ? new CurrentVelopackRelease(localRelease!)
            : null;
    }

    public async Task<IApplicationUpdate?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            cancellationToken.ThrowIfCancellationRequested();

            return update is null ? null : new AvailableVelopackUpdate(update);
        }
        catch (NotInstalledException exception)
        {
            throw new ApplicationUpdaterUnavailableException(
                "Updates are available only from installed PullWatch releases.",
                exception
            );
        }
    }

    public async Task DownloadUpdateAsync(
        IApplicationUpdate update,
        IProgress<int> progress,
        CancellationToken cancellationToken
    )
    {
        var velopackUpdate =
            update as AvailableVelopackUpdate
            ?? throw new ArgumentException(
                "The update must come from the current update check.",
                nameof(update)
            );

        try
        {
            await _manager.DownloadUpdatesAsync(
                velopackUpdate.UpdateInfo,
                progress.Report,
                cancellationToken
            );
        }
        catch (NotInstalledException exception)
        {
            throw new ApplicationUpdaterUnavailableException(
                "Updates are available only from installed PullWatch releases.",
                exception
            );
        }
        catch (AcquireLockFailedException exception)
        {
            throw new ApplicationUpdateException(
                "Another update operation is already running.",
                exception
            );
        }
        catch (ChecksumFailedException exception)
        {
            throw new ApplicationUpdateException(
                "The downloaded update did not pass verification. Try again.",
                exception
            );
        }
    }

    public void WaitForExitThenApplyUpdateAndRestart(IApplicationUpdate update)
    {
        var asset = update switch
        {
            AvailableVelopackUpdate availableUpdate => availableUpdate.TargetRelease,
            PendingVelopackUpdate pendingUpdate => pendingUpdate.TargetRelease,
            _ => throw new ArgumentException("The update must come from Velopack.", nameof(update)),
        };

        try
        {
            _manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true, restartArgs: []);
        }
        catch (NotInstalledException exception)
        {
            throw new ApplicationUpdaterUnavailableException(
                "Updates are available only from installed PullWatch releases.",
                exception
            );
        }
    }

    private sealed class AvailableVelopackUpdate(UpdateInfo updateInfo) : IApplicationUpdate
    {
        public UpdateInfo UpdateInfo { get; } = updateInfo;

        public VelopackAsset TargetRelease => UpdateInfo.TargetFullRelease;

        public string Version => TargetRelease.Version.ToString();

        public long SizeBytes => EstimateDownloadSize(UpdateInfo);

        public string? ReleaseNotesMarkdown => TargetRelease.NotesMarkdown;
    }

    private sealed class PendingVelopackUpdate(VelopackAsset targetRelease) : IApplicationUpdate
    {
        public VelopackAsset TargetRelease { get; } = targetRelease;

        public string Version => TargetRelease.Version.ToString();

        public long SizeBytes => TargetRelease.Size;

        public string? ReleaseNotesMarkdown => TargetRelease.NotesMarkdown;
    }

    private sealed class CurrentVelopackRelease(VelopackAsset release) : IApplicationUpdate
    {
        public string Version => release.Version.ToString();

        public long SizeBytes => release.Size;

        public string? ReleaseNotesMarkdown => release.NotesMarkdown;
    }

    private static long EstimateDownloadSize(UpdateInfo updateInfo)
    {
        return updateInfo.DeltasToTarget.Length > 0
            ? updateInfo.DeltasToTarget.Sum(delta => delta.Size)
            : updateInfo.TargetFullRelease.Size;
    }

    internal static bool ShouldIncludePrereleaseUpdates(string? value)
    {
        return value?.Trim() == "1";
    }

    internal static bool IsCurrentRestartedRelease(
        SemanticVersion? restartedVersion,
        SemanticVersion? currentVersion,
        SemanticVersion? localReleaseVersion
    )
    {
        return restartedVersion is not null
            && restartedVersion == currentVersion
            && restartedVersion == localReleaseVersion;
    }

    private static UpdateManager CreateUpdateManager(
        string? updateSourceOverride,
        bool includePrereleaseUpdates
    )
    {
        return string.IsNullOrWhiteSpace(updateSourceOverride)
            ? new UpdateManager(
                new GithubSource(RepositoryUrl, null, prerelease: includePrereleaseUpdates)
            )
            : new UpdateManager(updateSourceOverride);
    }
}
