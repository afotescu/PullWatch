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
        return handler.HandleAsync(
            new CombatLogEvent(eventName, eventName.Length, eventName),
            CancellationToken.None);
    }

    private static Task HandleAsync(
        CombatLogEventHandler handler,
        string eventName,
        string firstArgument)
    {
        var rawLine = $"{eventName},{firstArgument}";

        return handler.HandleAsync(
            new CombatLogEvent(eventName, eventName.Length + 1, rawLine),
            CancellationToken.None);
    }
}
