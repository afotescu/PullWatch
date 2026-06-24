namespace PullWatch.Tests;

public sealed class StartupShortcutOwnershipTests
{
    private const string CurrentPath = @"C:\Apps\PullWatch-new\PullWatch.exe";
    private const string ExistingPath = @"C:\Apps\PullWatch-old\PullWatch.exe";

    [Fact]
    public void DisabledStartupDeletesShortcut()
    {
        var action = StartupShortcutOwnership.Decide(
            false,
            Existing(version: "2.0.0"),
            CurrentPath,
            "1.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.DeleteShortcut, action);
    }

    [Fact]
    public void MissingShortcutWritesCurrentShortcut()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            StartupShortcutInspection.Missing,
            CurrentPath,
            "1.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void DevelopmentVersionDoesNotCreateMissingShortcut()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            StartupShortcutInspection.Missing,
            CurrentPath,
            "0.0.0-dev"
        );

        Assert.Equal(StartupShortcutOwnershipAction.KeepExistingShortcut, action);
    }

    [Fact]
    public void ExistingShortcutForCurrentExecutableIsRefreshed()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(path: CurrentPath, version: "1.0.0"),
            CurrentPath,
            "1.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void DevelopmentVersionRefreshesShortcutForCurrentExecutable()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(path: CurrentPath, version: "0.0.0-dev"),
            CurrentPath,
            "0.0.0-dev"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void CurrentVersionReplacesOlderShortcutTarget()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "1.9.0"),
            CurrentPath,
            "2.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void DevelopmentVersionDoesNotReplaceOlderShortcutTarget()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "0.0.0-alpha"),
            CurrentPath,
            "0.0.0-dev"
        );

        Assert.Equal(StartupShortcutOwnershipAction.KeepExistingShortcut, action);
    }

    [Fact]
    public void CurrentVersionReplacesEqualShortcutTarget()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "2.0.0"),
            CurrentPath,
            "2.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void CurrentVersionDoesNotReplaceNewerShortcutTarget()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "2.1.0"),
            CurrentPath,
            "2.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.KeepExistingShortcut, action);
    }

    [Fact]
    public void StableVersionReplacesMatchingPrereleaseShortcutTarget()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "2.0.0-dev"),
            CurrentPath,
            "2.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void PrereleaseVersionDoesNotReplaceMatchingStableShortcutTarget()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "2.0.0"),
            CurrentPath,
            "2.0.0-dev"
        );

        Assert.Equal(StartupShortcutOwnershipAction.KeepExistingShortcut, action);
    }

    [Fact]
    public void MissingTargetWritesCurrentShortcut()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(targetExists: false, version: null),
            CurrentPath,
            "2.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void UnknownTargetVersionWritesCurrentShortcut()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "not-a-version"),
            CurrentPath,
            "2.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void NonPullWatchTargetWritesCurrentShortcut()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "999.0.0", isPullWatchTarget: false),
            CurrentPath,
            "2.0.0"
        );

        Assert.Equal(StartupShortcutOwnershipAction.WriteCurrentShortcut, action);
    }

    [Fact]
    public void UnknownCurrentVersionKeepsKnownTargetVersion()
    {
        var action = StartupShortcutOwnership.Decide(
            true,
            Existing(version: "2.0.0"),
            CurrentPath,
            "not-a-version"
        );

        Assert.Equal(StartupShortcutOwnershipAction.KeepExistingShortcut, action);
    }

    private static StartupShortcutInspection Existing(
        string path = ExistingPath,
        bool targetExists = true,
        string? version = "1.0.0",
        bool isPullWatchTarget = true
    )
    {
        return new StartupShortcutInspection(true, path, targetExists, version, isPullWatchTarget);
    }
}
