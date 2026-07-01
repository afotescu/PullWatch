using Microsoft.Extensions.Logging.Abstractions;
using PullWatch.Tests.TestDoubles;

namespace PullWatch.Tests;

public sealed class CombatLogEventHandlerTests
{
    [Fact]
    public async Task ChallengeModeOwnsRecordingAcrossNestedEncounterEvents()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleAsync(handler, WowEvents.ChallengeModeStart);
        await HandleAsync(handler, WowEvents.EncounterStart);
        await HandleAsync(handler, WowEvents.EncounterEnd);
        await HandleAsync(handler, WowEvents.ChallengeModeEnd);

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task EncounterRecordsIndependentlyOutsideChallengeMode()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleAsync(handler, WowEvents.EncounterStart);
        await HandleAsync(handler, WowEvents.EncounterEnd);
        await HandleAsync(handler, WowEvents.EncounterStart);
        await HandleAsync(handler, WowEvents.EncounterEnd);

        Assert.Equal(["start", "stop", "start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task EncounterPullNumbersIncrementPerBossAndDifficulty()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);
        var mythicStart = $"3129,\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,2810";
        var mythicEnd = $"3129,\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,0,70964";
        var heroicStart = $"3129,\"Plexus Sentinel\",{WowDifficultyIds.HeroicRaid},20,2810";
        var heroicEnd = $"3129,\"Plexus Sentinel\",{WowDifficultyIds.HeroicRaid},20,0,70964";

        await HandleAsync(handler, WowEvents.ChallengeModeStart);
        await HandleWithArgumentsAsync(handler, WowEvents.EncounterStart, mythicStart);
        await HandleAsync(handler, WowEvents.ChallengeModeEnd);
        await HandleWithArgumentsAsync(handler, WowEvents.EncounterStart, mythicStart);
        await HandleWithArgumentsAsync(handler, WowEvents.EncounterEnd, mythicEnd);
        await HandleWithArgumentsAsync(handler, WowEvents.EncounterStart, mythicStart);
        await HandleWithArgumentsAsync(handler, WowEvents.EncounterEnd, mythicEnd);
        await HandleWithArgumentsAsync(handler, WowEvents.EncounterStart, heroicStart);
        await HandleWithArgumentsAsync(handler, WowEvents.EncounterEnd, heroicEnd);

        var pullNumbers = recorder
            .StartedContexts.OfType<EncounterRecordingContext>()
            .Select(context => context.PullNumber)
            .ToArray();

        Assert.Equal([1, 2, 1], pullNumbers);
    }

    [Fact]
    public async Task EncounterOwnsRecordingAcrossChallengeModeEvents()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleAsync(handler, WowEvents.EncounterStart);
        await HandleAsync(handler, WowEvents.ChallengeModeStart);
        await HandleAsync(handler, WowEvents.ChallengeModeEnd);
        await HandleAsync(handler, WowEvents.EncounterEnd);

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task DuplicateAndUnmatchedEventsAreIgnored()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleAsync(handler, WowEvents.ChallengeModeEnd);
        await HandleAsync(handler, WowEvents.ChallengeModeStart);
        await HandleAsync(handler, WowEvents.ChallengeModeStart);
        await HandleAsync(handler, WowEvents.ChallengeModeEnd);
        await HandleAsync(handler, WowEvents.ChallengeModeEnd);

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task SameChallengeStartAfterSoftSignalKeepsRecordingActive()
    {
        var recorder = new FakeRecordingService();
        await using var handler = CreateHandler(recorder);

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:50.0000  ZONE_CHANGE,1116,\"Spires of Arak\",0"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:59.0000  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(handler, "6/23/2026 16:43:10.0000  SPELL_DAMAGE");

        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task WatchdogExpiresFromLogTimestampsWithoutRecoveryEvidence()
    {
        var recorder = new FakeRecordingService();
        await using var handler = CreateHandler(recorder);

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:50.0000  ZONE_CHANGE,1116,\"Spires of Arak\",0"
        );
        await HandleLineAsync(handler, "6/23/2026 16:42:51.0000  SPELL_DAMAGE");

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task WatchdogTimerExpiryStopsRecordingWithoutAnotherLogLine()
    {
        var recorder = new FakeRecordingService();
        await using var handler = CreateHandler(
            recorder,
            challengeWatchdogTimeout: TimeSpan.FromMilliseconds(20)
        );

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:50.0000  ZONE_CHANGE,1116,\"Spires of Arak\",0"
        );

        await WaitForAsync(() => recorder.Calls.Count == 2);

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task ZoneReturnToMythicPlusCancelsWatchdog()
    {
        var recorder = new FakeRecordingService();
        await using var handler = CreateHandler(recorder);

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:50.0000  ZONE_CHANGE,1116,\"Spires of Arak\",0"
        );
        await HandleLineAsync(handler, "6/23/2026 16:41:59.0000  ZONE_CHANGE,1209,\"Skyreach\",8");
        await HandleLineAsync(handler, "6/23/2026 16:43:10.0000  SPELL_DAMAGE");

        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task MapReturnToDungeonNameCancelsWatchdog()
    {
        var recorder = new FakeRecordingService();
        await using var handler = CreateHandler(recorder);

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:50.0000  MAP_CHANGE,572,\"Draenor\",11193.750000,-3964.583984,12243.750000,-10493.750000"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:59.0000  MAP_CHANGE,601,\"Skyreach\",1367.989990,839.651978,2227.123535,1434.616577"
        );
        await HandleLineAsync(handler, "6/23/2026 16:43:10.0000  SPELL_DAMAGE");

        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task SameDungeonNonMythicPlusZoneKeepsWatchdogArmedAcrossMapChange()
    {
        var recorder = new FakeRecordingService();
        await using var handler = CreateHandler(recorder);

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(handler, "6/23/2026 16:41:50.0000  ZONE_CHANGE,1209,\"Skyreach\",23");
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:50.0010  MAP_CHANGE,601,\"Skyreach\",1367.989990,839.651978,2227.123535,1434.616577"
        );
        await HandleLineAsync(handler, "6/23/2026 16:42:01.0000  SPELL_DAMAGE");

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task CombatLogVersionDoesNotExpireActiveWatchdog()
    {
        var recorder = new FakeRecordingService();
        await using var handler = CreateHandler(recorder);

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:50.0000  ZONE_CHANGE,1116,\"Spires of Arak\",0"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:43:10.0000  COMBAT_LOG_VERSION,22,ADVANCED_LOG_ENABLED,1,BUILD_VERSION,12.0.7,PROJECT_ID,1"
        );

        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task DifferentChallengeStartStopsCurrentRecordingAndStartsNext()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        await HandleLineAsync(
            handler,
            "6/23/2026 16:42:00.0000  CHALLENGE_MODE_START,\"Windrunner Spire\",2805,557,21,[10,9,147]"
        );

        Assert.Equal(["start", "stop", "start"], recorder.Calls);
        var nextContext = Assert.IsType<ChallengeRecordingContext>(recorder.StartedContexts[1]);
        Assert.Equal("Windrunner Spire", nextContext.DungeonName);
    }

    [Fact]
    public async Task WatchdogExpiryCompletesChallengeAsDepleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var outputPath = Path.Combine(database.DirectoryPath, "watchdog-expiry.mp4");
        var recorder = new FakeRecordingService { ActiveOutputPath = outputPath };
        var catalog = new RecordingCatalog(database.Repository);
        await using var handler = CreateHandler(recorder, recordingCatalog: catalog);

        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:44.8003  CHALLENGE_MODE_START,\"Skyreach\",1209,161,21,[10,9,147]"
        );
        File.WriteAllText(outputPath, "recording");
        await HandleLineAsync(
            handler,
            "6/23/2026 16:41:50.0000  ZONE_CHANGE,1116,\"Spires of Arak\",0"
        );
        await HandleLineAsync(handler, "6/23/2026 16:42:51.0000  SPELL_DAMAGE");

        var recordings = await catalog.ListAvailableFilesAsync(
            database.DirectoryPath,
            cancellationToken
        );
        var recording = Assert.Single(recordings);
        Assert.NotNull(recording.ChallengeMode);
        Assert.Equal(ChallengeModeOutcome.Depleted, recording.ChallengeMode.Outcome);
    }

    [Fact]
    public async Task ChallengeModeEndWithFractionalTrailingRatingCompletesChallengeAsTimed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var outputPath = Path.Combine(database.DirectoryPath, "maisara-caverns.mp4");
        var recorder = new FakeRecordingService { ActiveOutputPath = outputPath };
        var catalog = new RecordingCatalog(database.Repository);
        await using var handler = CreateHandler(recorder, recordingCatalog: catalog);

        await HandleLineAsync(
            handler,
            "6/29/2026 15:50:22.4823  CHALLENGE_MODE_START,\"Maisara Caverns\",2874,560,12,[9,10,147]"
        );
        File.WriteAllText(outputPath, "recording");
        await HandleLineAsync(
            handler,
            "6/29/2026 16:09:44.3263  CHALLENGE_MODE_END,2874,1,12,1153679,380.000000,3512.041992"
        );

        var recordings = await catalog.ListAvailableFilesAsync(
            database.DirectoryPath,
            cancellationToken
        );
        var recording = Assert.Single(recordings);
        Assert.NotNull(recording.ChallengeMode);
        Assert.Equal(ChallengeModeOutcome.Timed, recording.ChallengeMode.Outcome);
        Assert.Equal(3512, recording.ChallengeMode.TimerLimitSeconds);
    }

    [Fact]
    public async Task FailedStartDoesNotClaimOwnership()
    {
        var recorder = new FakeRecordingService
        {
            StartException = new InvalidOperationException("Start failed"),
        };
        var handler = CreateHandler(recorder);

        await HandleAsync(handler, WowEvents.ChallengeModeStart);

        recorder.StartException = null;
        await HandleAsync(handler, WowEvents.EncounterStart);
        await HandleAsync(handler, WowEvents.EncounterEnd);

        Assert.Equal(["start", "start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task MalformedChallengeModeStartDoesNotCallRecorder()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleWithArgumentsAsync(
            handler,
            WowEvents.ChallengeModeStart,
            "\"Magisters' Terrace\",2811,558"
        );

        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task MalformedEncounterStartDoesNotCallRecorder()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleWithArgumentsAsync(
            handler,
            WowEvents.EncounterStart,
            "3129,\"Plexus Sentinel\""
        );

        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task ValidStartAfterMalformedStartIsHandled()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleWithArgumentsAsync(
            handler,
            WowEvents.ChallengeModeStart,
            "\"Magisters' Terrace\",2811,558"
        );
        await HandleAsync(handler, WowEvents.ChallengeModeStart);

        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task EncounterEndMustMatchActiveEncounterIdentity()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleAsync(handler, WowEvents.EncounterStart, "123");
        await HandleAsync(handler, WowEvents.EncounterEnd, "456");
        await HandleAsync(handler, WowEvents.EncounterEnd, "123");

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task MalformedEncounterEndStopsActiveEncounter()
    {
        string[] arguments =
        [
            "",
            $"invalid,\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,1,70964",
        ];

        foreach (var value in arguments)
        {
            var recorder = new FakeRecordingService();
            var handler = CreateHandler(recorder);

            await HandleAsync(handler, WowEvents.EncounterStart, "123");
            await HandleWithArgumentsAsync(handler, WowEvents.EncounterEnd, value);

            Assert.Equal(["start", "stop"], recorder.Calls);
        }
    }

    [Fact]
    public async Task MalformedEncounterEndDoesNotStopChallengeModeRecording()
    {
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);

        await HandleAsync(handler, WowEvents.ChallengeModeStart);
        await HandleWithArgumentsAsync(handler, WowEvents.EncounterEnd, "");
        Assert.Equal(["start"], recorder.Calls);

        await HandleAsync(handler, WowEvents.ChallengeModeEnd);
        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task MythicPlusMinimumKeystoneLevelFiltersChallengeStarts()
    {
        var recorder = new FakeRecordingService();
        var settingsProvider = new SettingsProvider(
            new PullWatchSettings
            {
                RecordingFilters = new RecordingFilterSettings
                {
                    MythicPlus = new MythicPlusRecordingFilterSettings
                    {
                        MinimumKeystoneLevel = 20,
                    },
                },
            }
        );
        var handler = CreateHandler(recorder, settingsProvider);

        await HandleWithArgumentsAsync(
            handler,
            WowEvents.ChallengeModeStart,
            "\"Magisters' Terrace\",2811,558,19,[9,10,147]"
        );
        await HandleAsync(handler, WowEvents.ChallengeModeEnd);

        Assert.Empty(recorder.Calls);

        await HandleWithArgumentsAsync(
            handler,
            WowEvents.ChallengeModeStart,
            "\"Magisters' Terrace\",2811,558,20,[9,10,147]"
        );

        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task RaidDifficultySelectionFiltersEncounterStarts()
    {
        var recorder = new FakeRecordingService();
        var settingsProvider = new SettingsProvider(
            new PullWatchSettings
            {
                RecordingFilters = new RecordingFilterSettings
                {
                    RaidEncounters = new RaidEncounterRecordingFilterSettings
                    {
                        RecordRaidFinder = false,
                        RecordNormal = false,
                        RecordHeroic = true,
                        RecordMythic = false,
                    },
                },
            }
        );
        var handler = CreateHandler(recorder, settingsProvider);

        await HandleWithArgumentsAsync(
            handler,
            WowEvents.EncounterStart,
            $"3129,\"Plexus Sentinel\",{WowDifficultyIds.NormalRaid},20,2810"
        );

        Assert.Empty(recorder.Calls);

        await HandleWithArgumentsAsync(
            handler,
            WowEvents.EncounterStart,
            $"3129,\"Plexus Sentinel\",{WowDifficultyIds.HeroicRaid},20,2810"
        );

        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task MythicRaidDifficultySelectionIncludesFlexibleMythicDifficulty()
    {
        var recorder = new FakeRecordingService();
        var settingsProvider = new SettingsProvider(
            new PullWatchSettings
            {
                RecordingFilters = new RecordingFilterSettings
                {
                    RaidEncounters = new RaidEncounterRecordingFilterSettings
                    {
                        RecordRaidFinder = false,
                        RecordNormal = false,
                        RecordHeroic = false,
                        RecordMythic = true,
                    },
                },
            }
        );
        var handler = CreateHandler(recorder, settingsProvider);

        await HandleWithArgumentsAsync(
            handler,
            WowEvents.EncounterStart,
            $"3159,\"Rotmire\",{WowDifficultyIds.FlexibleMythicRaid},20,1592"
        );

        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task DisabledAutomaticRecordingTypesDoNotStart()
    {
        var recorder = new FakeRecordingService();
        var settingsProvider = new SettingsProvider(
            new PullWatchSettings { RecordMythicPlus = false, RecordRaidEncounters = false }
        );
        var handler = CreateHandler(recorder, settingsProvider);

        await HandleAsync(handler, WowEvents.ChallengeModeStart);
        await HandleAsync(handler, WowEvents.EncounterStart);

        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task SettingChangeDoesNotPreventActiveRecordingFromStopping()
    {
        var recorder = new FakeRecordingService();
        var settingsProvider = new SettingsProvider(new PullWatchSettings());
        var handler = CreateHandler(recorder, settingsProvider);

        await HandleAsync(handler, WowEvents.ChallengeModeStart);
        settingsProvider.TryUpdate(settingsProvider.Current with { RecordMythicPlus = false });
        await HandleAsync(handler, WowEvents.ChallengeModeEnd);

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    private static CombatLogEventHandler CreateHandler(
        IRecordingService recordingService,
        SettingsProvider? settingsProvider = null,
        RecordingCatalog? recordingCatalog = null,
        TimeSpan? challengeWatchdogTimeout = null
    )
    {
        var coordinator = new RecordingCoordinator(
            recordingService,
            NullLogger<RecordingCoordinator>.Instance,
            recordingCatalog: recordingCatalog
        );

        return new CombatLogEventHandler(
            coordinator,
            settingsProvider ?? new SettingsProvider(new PullWatchSettings()),
            NullLogger<CombatLogEventHandler>.Instance,
            challengeWatchdogTimeout
        );
    }

    private static Task HandleLineAsync(CombatLogEventHandler handler, string line)
    {
        Assert.True(CombatLogParser.TryParseEvent(line, out var combatLogEvent));
        return handler.HandleAsync(combatLogEvent, CancellationToken.None);
    }

    private static Task HandleAsync(CombatLogEventHandler handler, string eventName)
    {
        var arguments = eventName switch
        {
            WowEvents.ChallengeModeStart => "\"Magisters' Terrace\",2811,558,22,[9,10,147]",
            WowEvents.ChallengeModeEnd => "2811,0,0,0,0.000000,0.000000",
            WowEvents.EncounterStart =>
                $"3129,\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,2810",
            WowEvents.EncounterEnd =>
                $"3129,\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,1,70964",
            _ => "",
        };
        var rawLine = $"{eventName},{arguments}";

        return handler.HandleAsync(
            new CombatLogEvent(eventName, eventName.Length + 1, rawLine),
            CancellationToken.None
        );
    }

    private static Task HandleAsync(
        CombatLogEventHandler handler,
        string eventName,
        string firstArgument
    )
    {
        var arguments = eventName switch
        {
            WowEvents.EncounterStart =>
                $"{firstArgument},\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,2810",
            WowEvents.EncounterEnd =>
                $"{firstArgument},\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,1,70964",
            _ => firstArgument,
        };
        var rawLine = $"{eventName},{arguments}";

        return handler.HandleAsync(
            new CombatLogEvent(eventName, eventName.Length + 1, rawLine),
            CancellationToken.None
        );
    }

    private static Task HandleWithArgumentsAsync(
        CombatLogEventHandler handler,
        string eventName,
        string arguments
    )
    {
        var rawLine = $"{eventName},{arguments}";

        return handler.HandleAsync(
            new CombatLogEvent(eventName, eventName.Length + 1, rawLine),
            CancellationToken.None
        );
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < timeout, "Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class TemporaryRecordingDatabase : IDisposable
    {
        private readonly TemporaryDirectory _directory;

        private TemporaryRecordingDatabase(
            TemporaryDirectory directory,
            SqliteConnectionFactory connectionFactory
        )
        {
            _directory = directory;
            ConnectionFactory = connectionFactory;
            Repository = new RecordingCatalogRepository(connectionFactory);
        }

        public string DirectoryPath => _directory.Path;

        public SqliteConnectionFactory ConnectionFactory { get; }

        public RecordingCatalogRepository Repository { get; }

        public static async Task<TemporaryRecordingDatabase> CreateAsync(
            CancellationToken cancellationToken
        )
        {
            var directory = new TemporaryDirectory();
            var databasePath = Path.Combine(directory.Path, "pullwatch.db");
            var factory = new SqliteConnectionFactory(
                new RecordingDatabasePathProvider(databasePath)
            );
            var initializer = new RecordingStorageInitializer(factory, NullLoggerFactory.Instance);

            try
            {
                await initializer.InitializeAsync(cancellationToken);
                return new TemporaryRecordingDatabase(directory, factory);
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _directory.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory
                .CreateTempSubdirectory("PullWatchCombatLogEventHandlerTests-")
                .FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
