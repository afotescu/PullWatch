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
    public async Task FailedStartDoesNotClaimOwnership()
    {
        var recorder = new FakeRecordingService
        {
            StartException = new InvalidOperationException("Start failed")
        };
        var handler = CreateHandler(recorder);

        await HandleAsync(handler, WowEvents.ChallengeModeStart);

        recorder.StartException = null;
        await HandleAsync(handler, WowEvents.EncounterStart);
        await HandleAsync(handler, WowEvents.EncounterEnd);

        Assert.Equal(["start", "start", "stop"], recorder.Calls);
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

    private static CombatLogEventHandler CreateHandler(IRecordingService recordingService)
    {
        var coordinator = new RecordingCoordinator(
            recordingService,
            NullLogger<RecordingCoordinator>.Instance);

        return new CombatLogEventHandler(
            coordinator,
            NullLogger<CombatLogEventHandler>.Instance);
    }

    private static Task HandleAsync(CombatLogEventHandler handler, string eventName)
    {
        var arguments = eventName switch
        {
            WowEvents.ChallengeModeStart => "\"Magisters' Terrace\",2811,558,22,[9,10,147]",
            WowEvents.ChallengeModeEnd => "2811,0,0,0,0.000000,0.000000",
            WowEvents.EncounterStart => "3129,\"Plexus Sentinel\",16,20,2810",
            WowEvents.EncounterEnd => "3129,\"Plexus Sentinel\",16,20,1,70964",
            _ => ""
        };
        var rawLine = $"{eventName},{arguments}";

        return handler.HandleAsync(
            new CombatLogEvent(eventName, eventName.Length + 1, rawLine),
            CancellationToken.None);
    }

    private static Task HandleAsync(
        CombatLogEventHandler handler,
        string eventName,
        string firstArgument)
    {
        var arguments = eventName switch
        {
            WowEvents.EncounterStart => $"{firstArgument},\"Plexus Sentinel\",16,20,2810",
            WowEvents.EncounterEnd => $"{firstArgument},\"Plexus Sentinel\",16,20,1,70964",
            _ => firstArgument
        };
        var rawLine = $"{eventName},{arguments}";

        return handler.HandleAsync(
            new CombatLogEvent(eventName, eventName.Length + 1, rawLine),
            CancellationToken.None);
    }
}
