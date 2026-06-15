namespace PullWatch.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task DisplaysMbpsAndSavesBitsPerSecond()
    {
        PullWatchSettings? saved = null;
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saved = settings;
                return Saved(settings);
            });

        Assert.Equal("12", viewModel.BitrateMegabits);

        viewModel.BitrateMegabits = "18.5";
        await viewModel.SaveChangesAsync();

        Assert.Equal(18_500_000, saved!.Video.Bitrate);
        Assert.False(viewModel.IsDirty);
        Assert.Equal("Settings saved and active.", viewModel.SaveMessage);
    }

    [Theory]
    [InlineData(RecordingCoordinatorState.Starting)]
    [InlineData(RecordingCoordinatorState.Recording)]
    [InlineData(RecordingCoordinatorState.Stopping)]
    public void DisablesEditingOutsideIdle(RecordingCoordinatorState state)
    {
        var viewModel = CreateViewModel(Status(state));

        Assert.False(viewModel.IsEditingEnabled);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.PickWowLogsDirectoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task InvalidInputKeepsEditsAndShowsInlineError()
    {
        var saveCalled = false;
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saveCalled = true;
                return Saved(settings);
            });
        viewModel.FrameRate = "not a number";

        var saved = await viewModel.SaveChangesAsync();

        Assert.False(saved);
        Assert.False(saveCalled);
        Assert.True(viewModel.IsDirty);
        Assert.NotNull(viewModel.FrameRateError);
    }

    [Fact]
    public void DiscardRestoresLastSavedSettings()
    {
        var viewModel = CreateViewModel(Status(RecordingCoordinatorState.Idle));
        viewModel.RecordMythicPlus = false;

        viewModel.DiscardChanges();

        Assert.True(viewModel.RecordMythicPlus);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task UnexpectedSaveFailureIsDisplayed()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ => Task.FromException<SettingsSaveResult>(
                new InvalidOperationException("settings service unavailable")));
        viewModel.RecordMythicPlus = false;

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.True(viewModel.IsSaveError);
        Assert.Equal(
            "Could not save settings: settings service unavailable",
            viewModel.SaveMessage);
    }

    private static SettingsViewModel CreateViewModel(
        ApplicationStatus status,
        Func<PullWatchSettings, Task<SettingsSaveResult>>? save = null)
    {
        return new SettingsViewModel(
            status,
            (settings, _) => save?.Invoke(settings) ?? Saved(settings),
            new FakeSettingsDialogs());
    }

    private static Task<SettingsSaveResult> Saved(PullWatchSettings settings)
    {
        return Task.FromResult(new SettingsSaveResult(SettingsSaveStatus.Saved, settings, []));
    }

    private static ApplicationStatus Status(RecordingCoordinatorState state)
    {
        return new ApplicationStatus(
            new PullWatchSettings
            {
                RecordingsDirectory = Path.Combine(Path.GetTempPath(), "PullWatchViewModelTests")
            },
            new RecordingCoordinatorStatus(
                state,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            new CombatLogReaderStatus(
                CombatLogReaderState.WaitingForCombatLog,
                null,
                null,
                null));
    }

    private sealed class FakeSettingsDialogs : ISettingsDialogs
    {
        public string? PickFolder(string title, string? initialDirectory)
        {
            return null;
        }

        public bool SaveBeforeLeavingSettings()
        {
            return false;
        }
    }
}
