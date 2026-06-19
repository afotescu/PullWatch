namespace PullWatch.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task DisplaysVideoOptionsAndSavesSelection()
    {
        PullWatchSettings? saved = null;
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saved = settings;
                return Saved(settings);
            }
        );

        Assert.Equal(VideoQuality.Balanced, viewModel.SelectedVideoQuality);
        Assert.Equal(VideoFrameRates.High, viewModel.SelectedFrameRate);
        Assert.Contains("1920x1080", viewModel.EstimatedRecordingSize);

        viewModel.SelectedVideoQuality = VideoQuality.High;
        viewModel.SelectedFrameRate = VideoFrameRates.Standard;
        await viewModel.SaveChangesAsync();

        Assert.Equal(VideoQuality.High, saved!.Video.Quality);
        Assert.Equal(VideoFrameRates.Standard, saved.Video.FrameRate);
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
    public async Task ValidationFailureKeepsEditsAndShowsSaveError()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ =>
                Task.FromResult(
                    new SettingsSaveResult(
                        SettingsSaveStatus.Invalid,
                        null,
                        ["Video quality must be Compact, Balanced, or High."]
                    )
                )
        );
        viewModel.SelectedVideoQuality = VideoQuality.High;

        var saved = await viewModel.SaveChangesAsync();

        Assert.False(saved);
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.IsSaveError);
        Assert.Equal("Fix the highlighted settings before saving.", viewModel.SaveMessage);
    }

    [Fact]
    public void EstimateUpdatesWhenVideoOptionsChange()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            estimateCaptureSize: new VideoCaptureSize(2560, 1440)
        );

        Assert.Contains("910 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("60 FPS", viewModel.EstimatedRecordingSize);

        viewModel.SelectedFrameRate = VideoFrameRates.Standard;

        Assert.Contains("460 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("30 FPS", viewModel.EstimatedRecordingSize);
    }

    [Fact]
    public void OptionLabelsRemainStable()
    {
        var viewModel = CreateViewModel(Status(RecordingCoordinatorState.Idle));

        Assert.Equal(
            ["Compact", "Balanced", "High"],
            viewModel.VideoQualityOptions.Select(option => option.Label)
        );
        Assert.Equal(
            ["30 FPS", "60 FPS"],
            viewModel.FrameRateOptions.Select(option => option.Label)
        );
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
            _ =>
                Task.FromException<SettingsSaveResult>(
                    new InvalidOperationException("settings service unavailable")
                )
        );
        viewModel.RecordMythicPlus = false;

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.True(viewModel.IsSaveError);
        Assert.Equal(
            "Could not save settings: settings service unavailable",
            viewModel.SaveMessage
        );
    }

    private static SettingsViewModel CreateViewModel(
        ApplicationStatus status,
        Func<PullWatchSettings, Task<SettingsSaveResult>>? save = null,
        VideoCaptureSize? estimateCaptureSize = null
    )
    {
        return new SettingsViewModel(
            status,
            (settings, _) => save?.Invoke(settings) ?? Saved(settings),
            new FakeSettingsDialogs(),
            () => estimateCaptureSize ?? new VideoCaptureSize(1920, 1080)
        );
    }

    private static Task<SettingsSaveResult> Saved(PullWatchSettings settings)
    {
        return Task.FromResult(new SettingsSaveResult(SettingsSaveStatus.Saved, settings, []));
    }

    private static ApplicationStatus Status(
        RecordingCoordinatorState state,
        PullWatchSettings? settings = null
    )
    {
        return new ApplicationStatus(
            settings
                ?? new PullWatchSettings
                {
                    RecordingsDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "PullWatchViewModelTests"
                    ),
                },
            new RecordingCoordinatorStatus(state, null, null, null, null, null, null, null),
            new CombatLogReaderStatus(CombatLogReaderState.WaitingForCombatLog, null, null, null),
            new WowProcessStatus(WowProcessState.WaitingForProcess, null, null, null)
        );
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
