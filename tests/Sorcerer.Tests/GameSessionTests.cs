using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Sorcerer.Magic.Capabilities;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

public sealed class GameSessionTests
{
    [Theory]
    [InlineData("addTags", WorldConsequenceTypes.AddTags)]
    [InlineData("add-tags", WorldConsequenceTypes.AddTags)]
    [InlineData("requestService", WorldConsequenceTypes.RequestService)]
    [InlineData("request-service", WorldConsequenceTypes.RequestService)]
    [InlineData("openOrUnlock", WorldConsequenceTypes.OpenOrUnlock)]
    [InlineData("freePrisoner", WorldConsequenceTypes.FreeCaptive)]
    [InlineData("release-captive", WorldConsequenceTypes.FreeCaptive)]
    [InlineData("recordMemory", WorldConsequenceTypes.RecordMemory)]
    public void WorldConsequenceTypeNormalizationCanonicalizesCommonSourceSpellings(
        string sourceType,
        string expectedType)
    {
        var normalized = WorldConsequenceTypes.Normalize(sourceType);

        Assert.Equal(expectedType, normalized);
        Assert.True(WorldConsequenceTypes.IsKnown(sourceType));
        Assert.True(WorldConsequenceTypes.IsKnown(normalized));
    }

    [Fact]
    public void ApplyConsequenceRollsBackAcceptedMutationThatInvalidatesState()
    {
        var session = GameSession.CreateImperialEncounter();
        var playerPosition = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var entityCount = session.Engine.State.Entities.Count;

        var result = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "overlap saint",
            playerPosition.X,
            playerPosition.Y,
            operation: "testInvalidSpawn"));

        Assert.False(result.Applied);
        var rejection = Assert.Single(result.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.Equal(WorldConsequenceTypes.SpawnEntity, rejection.Details["consequenceType"]);
        Assert.Equal(true, rejection.Details["rolledBack"]);
        var issueCodes = Assert.IsAssignableFrom<IEnumerable<object?>>(rejection.Details["issueCodes"]!);
        Assert.Contains(issueCodes, code => Equals(code, "blocking_overlap"));
        Assert.Equal(entityCount, session.Engine.State.Entities.Count);
        Assert.DoesNotContain(session.Engine.State.Entities.Values, entity =>
            entity.Name.Equals("overlap saint", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void StandaloneWorldConsequenceGuardRollsBackAcceptedMutationThatInvalidatesState()
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;
        var playerPosition = state.ControlledEntity.Get<PositionComponent>().Position;
        var entityCount = state.Entities.Count;

        var result = WorldConsequenceGuard.ApplyWithNewApplier(state, WorldConsequence.SpawnEntity(
            "test",
            "overlap witness",
            playerPosition.X,
            playerPosition.Y,
            operation: "testStandaloneInvalidSpawn"));

        Assert.False(result.Applied);
        var rejection = Assert.Single(result.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.Equal(WorldConsequenceTypes.SpawnEntity, rejection.Details["consequenceType"]);
        Assert.Equal(true, rejection.Details["rolledBack"]);
        var issueCodes = Assert.IsAssignableFrom<IEnumerable<object?>>(rejection.Details["issueCodes"]!);
        Assert.Contains(issueCodes, code => Equals(code, "blocking_overlap"));
        Assert.Equal(entityCount, state.Entities.Count);
        Assert.DoesNotContain(state.Entities.Values, entity =>
            entity.Name.Equals("overlap witness", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void WorldConsequenceGuardTurnsSilentNonApplyIntoRejectedAuditDelta()
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;

        var result = WorldConsequenceGuard.Apply(
            state,
            WorldConsequence.Message("test", "This should not appear.", targetEntityId: "player"),
            _ => WorldConsequenceApplyResult.Empty("forced silent non-apply"));

        Assert.False(result.Applied);
        var rejection = Assert.Single(result.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.Equal(WorldConsequenceTypes.Message, rejection.Details["consequenceType"]);
        Assert.Equal("forced silent non-apply", rejection.Details["error"]);
        Assert.Equal(true, rejection.Details["rolledBack"]);
        Assert.Equal(true, rejection.Details["auditOnly"]);
        Assert.Equal(false, rejection.Details["playerVisible"]);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void WorldConsequenceGuardRollsBackThrownHandlerMutation()
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;
        var messageCount = state.Messages.Count;

        var result = WorldConsequenceGuard.Apply(
            state,
            WorldConsequence.Message("test", "This should not appear.", targetEntityId: "player"),
            _ =>
            {
                state.Messages.Add("leaked mutation");
                throw new InvalidOperationException("fixture consequence failure");
            });

        Assert.False(result.Applied);
        Assert.Equal("fixture consequence failure", result.Error);
        var rejection = Assert.Single(result.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.Equal(WorldConsequenceTypes.Message, rejection.Details["consequenceType"]);
        Assert.Equal(nameof(InvalidOperationException), rejection.Details["exceptionType"]);
        Assert.Equal("fixture consequence failure", rejection.Details["error"]);
        Assert.Equal(true, rejection.Details["rolledBack"]);
        Assert.Equal(true, rejection.Details["auditOnly"]);
        Assert.Equal(false, rejection.Details["playerVisible"]);
        Assert.Equal(messageCount, state.Messages.Count);
        Assert.DoesNotContain("leaked mutation", state.Messages);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void NonImmediateConsequenceSchedulesThroughSharedApplyPoint()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;

        var scheduled = session.Engine.ApplyConsequence(new WorldConsequence(
            WorldConsequenceTypes.AddTags,
            "test",
            SourceEntityId: player.Id.Value,
            TargetEntityId: player.Id.Value,
            Payload: new Dictionary<string, object?>
            {
                ["tags"] = new[] { "timed_mark" },
                ["operation"] = "testTimedAddTags",
            },
            Timing: WorldConsequenceTiming.AfterTurn));

        Assert.True(scheduled.Applied, scheduled.Error);
        Assert.Contains(scheduled.Deltas, delta =>
            delta.Operation == "scheduleTimedConsequence"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ScheduleEvent)
            && Equals(delta.Details["scheduledConsequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["scheduledTiming"], WorldConsequenceTiming.AfterTurn));
        Assert.Single(session.Engine.State.ScheduledEvents.Events);
        Assert.False(player.TryGet<TagsComponent>(out var beforeTags)
            && beforeTags.Tags.Contains("timed_mark", StringComparer.OrdinalIgnoreCase));

        var turnDeltas = session.Engine.AdvanceTurn();

        Assert.Empty(session.Engine.State.ScheduledEvents.Events);
        Assert.Contains("timed_mark", player.Get<TagsComponent>().Tags);
        Assert.Contains(turnDeltas, delta =>
            delta.Operation == "testTimedAddTags"
            && delta.Target == player.Id.Value
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task MoveCommandConsumesTurn()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(0, result.TurnBefore);
        Assert.Equal(1, result.TurnAfter);
        Assert.Contains("You move.", result.Messages);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "move"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity)
            && Equals(delta.Details["direction"], Direction.East.ToString()));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "moveMessage"
            && delta.Summary == "You move."
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["direction"], Direction.East.ToString())
            && Equals(delta.Details["playerVisible"], true));
    }

    [Fact]
    public async Task MoveBlockedByTerrainEmitsMessageConsequence()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 4)));

        var result = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(0, result.TurnBefore);
        Assert.Equal(0, result.TurnAfter);
        Assert.Contains("Something solid refuses you.", result.Messages);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "moveBlockedMessage"
            && delta.Summary == "Something solid refuses you."
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["direction"], Direction.East.ToString())
            && Equals(delta.Details["blockedReason"], "terrain")
            && Equals(delta.Details["toX"], 13)
            && Equals(delta.Details["toY"], 4)
            && Equals(delta.Details["playerVisible"], true));
    }

    [Fact]
    public async Task CommandControlMessagesUseTypedConsequences()
    {
        var session = GameSession.CreateImperialEncounter();

        var target = await session.ExecuteAsync(new TargetCommand(new GridPoint(5, 5)));
        var clearTarget = await session.ExecuteAsync(new ClearTargetCommand());
        var begin = await session.ExecuteAsync(new BeginCastCommand("ask the lock to remember rain"));
        var cancel = await session.ExecuteAsync(new CancelCastCommand());

        Assert.True(target.Success);
        Assert.Contains(target.Deltas, delta =>
            delta.Operation == "setSelectedTarget"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SetSelectedTarget)
            && Equals(delta.Details["x"], 5)
            && Equals(delta.Details["y"], 5));
        Assert.Contains(target.Deltas, delta =>
            delta.Operation == "targetMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["x"], 5)
            && Equals(delta.Details["y"], 5));
        Assert.True(clearTarget.Success);
        Assert.Contains(clearTarget.Deltas, delta =>
            delta.Operation == "clearSelectedTarget"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SetSelectedTarget)
            && Equals(delta.Details["clear"], true));
        Assert.Null(session.Engine.State.SelectedTarget);
        Assert.True(begin.Success);
        Assert.Contains(begin.Deltas, delta =>
            delta.Operation == "pendingCastMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["pendingCastId"], "cast_1"));
        Assert.True(cancel.Success);
        Assert.Contains(cancel.Deltas, delta =>
            delta.Operation == "pendingCastMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["pendingCastId"], "cast_1")
            && Equals(delta.Details["status"], "cancelled"));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Equals("Target set to 5,5.", StringComparison.Ordinal));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Equals("Target cleared.", StringComparison.Ordinal));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Equals("Pending cast cast_1 is resolving.", StringComparison.Ordinal));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Equals("Pending cast cast_1 dissipates.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TechnicalMagicFailureDoesNotConsumeTurn()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = await session.ExecuteAsync(new CastCommand("turn the soldier blue"));

        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.True(result.TechnicalFailure);
        Assert.Equal(0, result.TurnAfter);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicTechnicalFailure"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message));
    }

    [Fact]
    public async Task MalformedMagicTargetDoesNotConsumeTurnOrRunActors()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MalformedTargetSpellProvider()));
        var soldier = session.Engine.EntityById("soldier_1")!;
        var soldierPositionBefore = soldier.Get<PositionComponent>().Position;
        var playerHpBefore = session.Engine.State.ControlledEntity.Get<ActorComponent>().HitPoints;

        var result = await session.ExecuteAsync(new CastCommand("strike the target described by a malformed object"));

        Assert.False(result.Success);
        Assert.True(result.TechnicalFailure);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(0, result.TurnAfter);
        Assert.Equal(playerHpBefore, session.Engine.State.ControlledEntity.Get<ActorComponent>().HitPoints);
        Assert.Equal(soldierPositionBefore, soldier.Get<PositionComponent>().Position);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicTechnicalFailure"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation.StartsWith("ai", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Messages, message => message.Contains("System.Collections", StringComparison.Ordinal));
        Assert.Contains(result.Messages, message => message.Contains("Malformed target object", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AcceptedMagicConsumesTurnThroughSharedSession()
    {
        var provider = new FixtureSpellProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));

        var result = await session.ExecuteAsync(new CastCommand("strike the nearest soldier"));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("cast", result.Action);
        Assert.NotEmpty(result.Deltas);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicOutcome"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message));
        Assert.Equal(1, result.TurnAfter);
    }

    [Fact]
    public async Task MaterializedMagicResolutionDoesNotMutateUntilApplied()
    {
        var controller = new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The air tastes of blue salt.",
            new SpellEffect(
                "message",
                new Dictionary<string, object?>
                {
                    ["text"] = "The air tastes of blue salt.",
                }))));
        var session = GameSession.CreateImperialEncounter(controller, seed: 41);
        DisableImperialAi(session);

        var savedAt = new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);
        var before = GameSaveService.Serialize(session.Engine.State, savedAt: savedAt);
        var materialized = await controller.ResolveAsync(
            session.Engine,
            new CastCommand("make the air taste of blue salt"),
            CancellationToken.None);
        var afterResolve = GameSaveService.Serialize(session.Engine.State, savedAt: savedAt);

        Assert.Equal(before, afterResolve);
        Assert.False(materialized.TechnicalFailure);
        Assert.True(materialized.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(materialized.ResolvedMagicJson));

        var result = controller.ApplyResolved(session.Engine, materialized);

        Assert.True(result.Success, string.Join("\n", result.Messages));
        Assert.True(result.ConsumedTurn);
        Assert.Equal(1, result.TurnAfter);
        Assert.NotEqual(before, GameSaveService.Serialize(session.Engine.State, savedAt: savedAt));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicOutcome"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message));
    }

    [Fact]
    public async Task MaterializedMagicResolutionAppliesAfterSaveLoadWithoutProvider()
    {
        var resolveController = new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "A blue salt wind crosses the room.",
            new SpellEffect(
                "message",
                new Dictionary<string, object?>
                {
                    ["text"] = "A blue salt wind crosses the room.",
                }))));
        var original = GameSession.CreateImperialEncounter(resolveController, seed: 42);
        DisableImperialAi(original);
        var materialized = await resolveController.ResolveAsync(
            original.Engine,
            new CastCommand("send a blue salt wind across the room"),
            CancellationToken.None);

        var savedAt = new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);
        var savedBeforeApply = GameSaveService.Serialize(original.Engine.State, savedAt: savedAt);
        var loaded = GameSaveService.Deserialize(savedBeforeApply);
        var replayController = new WildMagicController(new ThrowingSpellProvider());
        var replay = new GameSession(loaded.State, replayController);

        Assert.Equal(savedBeforeApply, GameSaveService.Serialize(replay.Engine.State, savedAt: savedAt));

        var result = replayController.ApplyResolved(replay.Engine, materialized);

        Assert.True(result.Success, string.Join("\n", result.Messages));
        Assert.True(result.ConsumedTurn);
        Assert.Equal(1, replay.Engine.State.Turn);
        Assert.NotEqual(savedBeforeApply, GameSaveService.Serialize(replay.Engine.State, savedAt: savedAt));
    }

    [Fact]
    public async Task CastMessagesHideWorldTurnAuditWrappers()
    {
        var provider = new FixtureSpellProvider(AcceptedSpell(
            "The spell answers quietly.",
            new SpellEffect(
                "message",
                new Dictionary<string, object?>
                {
                    ["text"] = "A harmless spark keeps its own counsel.",
                })));
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        DisableImperialAi(session);
        session.Engine.State.PromiseLedger.Add(
            "rumor",
            "There is a foxfire needle in the old wall.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "foxfire needle",
            triggerHint: "inspect",
            realizationKind: "item");

        var result = await session.ExecuteAsync(new CastCommand("make a quiet harmless spark"));

        Assert.True(result.Success);
        Assert.Equal(1, result.Messages.Count(message => message.StartsWith("A lead tugs", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "promise_stir")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public async Task RejectedCastMessagesHideWorldTurnAuditWrappers()
    {
        var provider = new FixtureSpellProvider(new SpellResolution(
            Accepted: false,
            Severity: "minor",
            OutcomeText: "",
            Effects: Array.Empty<SpellEffect>(),
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: "The spell will not fit through the room."));
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        DisableImperialAi(session);
        session.Engine.State.PromiseLedger.Add(
            "rumor",
            "There is a silver road under the plaster.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "silver road",
            triggerHint: "travel",
            realizationKind: "route");

        var result = await session.ExecuteAsync(new CastCommand("become the whole road at once"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message));
        Assert.Equal(1, result.Messages.Count(message => message.StartsWith("A lead tugs", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "promise_stir")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public async Task MockProviderConditionWordsApplyTickingStatus()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(soldier.Get<ActorComponent>() with { HitPoints = 10 });

        var result = await session.ExecuteAsync(new CastCommand("set the nearest soldier's boots burning with a blue coal"));

        Assert.True(result.Success);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
        Assert.Equal(8, soldier.Get<ActorComponent>().HitPoints);
        Assert.Contains(result.Messages, message => message.Contains("ongoing harm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ZeroNumericMagicCostsDoNotEmitPlayerFacingCostLines()
    {
        var provider = new ZeroManaCostSpellProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));

        var result = await session.ExecuteAsync(new CastCommand("make the nearest soldier's armor bloom"));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "cost:mana");
        Assert.DoesNotContain(result.Messages, message => message.Contains("Cost: 0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TransformItemNoOpIsRejectedBeforeSelfChangeMessage()
    {
        var provider = new NoOpTransformItemSpellProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));

        var result = await session.ExecuteAsync(new CastCommand("change the tincture without changing anything"));

        Assert.False(result.Success);
        Assert.False(result.TechnicalFailure);
        Assert.True(result.ConsumedTurn);
        Assert.DoesNotContain(result.Messages, message => message.Contains("changes into red tincture", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message => message.Contains("transformItem needs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PendingCastDoesNotMutateUntilAwaited()
    {
        var provider = new FixtureSpellProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        var soldier = session.Engine.EntityById("soldier_1")!;
        var hpBefore = soldier.Get<Sorcerer.Core.Entities.ActorComponent>().HitPoints;

        var begin = await session.ExecuteAsync(new BeginCastCommand("strike the nearest soldier"));

        Assert.True(begin.Success);
        Assert.False(begin.ConsumedTurn);
        Assert.Equal(0, begin.TurnAfter);
        Assert.Equal(hpBefore, soldier.Get<Sorcerer.Core.Entities.ActorComponent>().HitPoints);
        var pending = session.Observation().PendingCast;
        Assert.NotNull(pending);
        Assert.Equal("ready", pending!.State);
        Assert.Equal("fixture", pending.Provider);
        Assert.Equal(true, pending.Accepted);
        Assert.Contains("damage", pending.EffectTypes!);

        var blocked = await session.ExecuteAsync(new MoveCommand(Direction.East));
        Assert.False(blocked.Success);
        Assert.False(blocked.ConsumedTurn);

        var resolved = await session.ExecuteAsync(new AwaitCastCommand());

        Assert.True(resolved.Success);
        Assert.True(resolved.ConsumedTurn);
        Assert.Null(session.Observation().PendingCast);
        Assert.True(soldier.Get<Sorcerer.Core.Entities.ActorComponent>().HitPoints < hpBefore);
    }

    [Fact]
    public async Task CancelPendingCastCancelsBackgroundResolution()
    {
        var provider = new CancellationAwareSpellProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));

        var begin = await session.ExecuteAsync(new BeginCastCommand("let the slow spell gather itself"));

        Assert.True(begin.Success);
        Assert.False(begin.ConsumedTurn);
        Assert.Equal("resolving", session.Observation().PendingCast?.State);

        var cancel = await session.ExecuteAsync(new CancelCastCommand());

        Assert.True(cancel.Success);
        Assert.False(cancel.ConsumedTurn);
        Assert.Null(session.Observation().PendingCast);
        Assert.True(await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task PendingCastObservationSurfacesTechnicalFailure()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new TechnicalFailureSpellProvider()));

        var begin = await session.ExecuteAsync(new BeginCastCommand("ask the model to stumble"));
        var pending = session.Observation().PendingCast;

        Assert.True(begin.Success);
        Assert.NotNull(pending);
        Assert.Equal("failed", pending!.State);
        Assert.Equal("technical-fixture", pending.Provider);
        Assert.Equal(false, pending.Accepted);
        Assert.Equal(true, pending.TechnicalFailure);
        Assert.Equal("fixture failure", pending.Error);
    }

    [Fact]
    public async Task PendingCastAllowsReadOnlyLedgerAndStateCommands()
    {
        var provider = new FixtureSpellProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));

        var begin = await session.ExecuteAsync(new BeginCastCommand("strike the nearest soldier"));
        var readOnlyCommands = new GameCommand[]
        {
            new InspectCommand(),
            new MapCommand(),
            new AtlasCommand(),
            new JournalCommand(),
            new RumorsCommand(),
            new CharacterCommand(),
            new ReagentsCommand(),
            new WaresCommand(),
            new ServicesCommand(),
            new BondsCommand(),
            new StandingCommand(),
            new FollowersCommand(),
            new JobsCommand(),
            new HelpCommand(),
        };

        Assert.True(begin.Success);
        Assert.NotNull(session.Observation().PendingCast);
        foreach (var command in readOnlyCommands)
        {
            var result = await session.ExecuteAsync(command);
            Assert.NotEqual("pending_cast", result.Action);
            Assert.False(result.ConsumedTurn);
            Assert.Equal(0, result.TurnAfter);
            Assert.NotNull(session.Observation().PendingCast);
        }

        var blockedMove = await session.ExecuteAsync(new MoveCommand(Direction.East));
        Assert.False(blockedMove.Success);
        Assert.Equal("pending_cast", blockedMove.Action);
        Assert.NotNull(session.Observation().PendingCast);
    }

    [Fact]
    public async Task MapCommandReturnsCoordinateMapInsteadOfInspectText()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
        var position = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;

        var result = await session.ExecuteAsync(new MapCommand(2));

        Assert.True(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal("map", result.Action);
        Assert.Contains("Map radius 2", result.Messages[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"center {position.X},{position.Y}", result.Messages[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Messages, message =>
            message.StartsWith($"{position.Y:00} ", StringComparison.Ordinal)
            && message.Contains('@', StringComparison.Ordinal));
        Assert.Contains(result.Messages, message => message.StartsWith("Legend:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Messages, message => message.StartsWith("HP ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HostileActorsMoveAfterConsumedTurn()
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;

        var result = await session.ExecuteAsync(new WaitCommand());

        var after = soldier.Get<PositionComponent>().Position;
        Assert.True(result.ConsumedTurn);
        Assert.Contains("You wait.", result.Messages);
        Assert.NotEqual(before, after);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "aiMove"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "waitMessage"
            && delta.Summary == "You wait."
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["playerVisible"], true));
    }

    [Fact]
    public async Task StatusRegistryTraitsSuppressAiActions()
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;
        ApplyStatus(session, soldier, "webbed", duration: 3);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.True(result.ConsumedTurn);
        Assert.Equal(before, soldier.Get<PositionComponent>().Position);
        Assert.DoesNotContain(result.Deltas, delta => delta.Target == "soldier_1" && delta.Operation == "aiMove");
    }

    [Theory]
    [InlineData("shadow_pinned")]
    [InlineData("kneeling")]
    [InlineData("boneless_knees")]
    public async Task BindingStatusVariantsCanonicalizeToRootedAndSuppressAiActions(string status)
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;
        ApplyStatus(session, soldier, status, duration: 3);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal("rooted", soldier.Get<StatusContainerComponent>().Statuses.Single().Id);
        Assert.Equal(before, soldier.Get<PositionComponent>().Position);
        Assert.DoesNotContain(result.Deltas, delta => delta.Target == "soldier_1" && delta.Operation == "aiMove");
    }

    [Fact]
    public async Task AddStatusAcceptsTraitAliasFromLiveProviderShape()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new TraitStatusSpellProvider()));
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;

        var result = await session.ExecuteAsync(new CastCommand("bind the nearest enemy in sticky blue webbing"));

        Assert.True(result.Success);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status =>
            status.Id == "rooted"
            && status.DisplayName == "webbed_blue_webbing");
        Assert.Equal(before, soldier.Get<PositionComponent>().Position);
        Assert.DoesNotContain(result.Deltas, delta => delta.Target == "soldier_1" && delta.Operation == "aiMove");
    }

    [Fact]
    public async Task StatusRegistryTraitsBlockControlledMovement()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        var before = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        ApplyStatus(session, session.Engine.State.ControlledEntity, "sticky_webbed", duration: 3);

        var result = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(before, session.Engine.State.ControlledEntity.Get<PositionComponent>().Position);
        Assert.Contains("binding", result.Messages.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "moveBlockedMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["direction"], Direction.East.ToString())
            && Equals(delta.Details["blockedReason"], "status")
            && Equals(delta.Details["playerVisible"], true));
    }

    [Fact]
    public void ApplyStatusUsesSecondPersonForControlledEntity()
    {
        var session = GameSession.CreateImperialEncounter();

        var delta = ApplyStatus(session,
            session.Engine.State.ControlledEntity,
            "river_concealed",
            duration: 4);

        Assert.Equal("You are river concealed.", delta.Summary);
        Assert.Contains(session.Engine.State.ControlledEntity.Get<StatusContainerComponent>().Statuses, status =>
            status.Id == "concealed"
            && status.DisplayName == "river_concealed");
    }

    [Fact]
    public void OmittedStatusDurationUsesRegistryDefault()
    {
        var session = GameSession.CreateImperialEncounter();

        var delta = ApplyStatus(session,
            session.Engine.State.ControlledEntity,
            "river_concealed",
            duration: 0);

        Assert.Equal(4, delta.Details["duration"]);
        Assert.Contains(session.Engine.State.ControlledEntity.Get<StatusContainerComponent>().Statuses, status =>
            status.Id == "concealed"
            && status.ExpiresTurn == 4);
    }

    [Fact]
    public void ExpiredStatusesUseSharedRemoveStatusConsequence()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        ApplyStatus(session, player, "river_concealed", duration: 1);

        var deltas = session.Engine.AdvanceTurn();

        Assert.DoesNotContain(player.Get<StatusContainerComponent>().Statuses, status => status.Id == "concealed");
        Assert.Contains(deltas, delta =>
            delta.Operation == "expireStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RemoveStatus)
            && Equals(delta.Details["status"], "concealed")
            && Equals(delta.Details["playerVisible"], false));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("concealed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConcealedControlledEntityAvoidsDistantHostileNotice()
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;
        ApplyStatus(session, session.Engine.State.ControlledEntity, "river_concealed", duration: 4);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Deltas, delta => delta.Target == "soldier_1" && delta.Operation == "aiMove");
        Assert.Equal(before, soldier.Get<PositionComponent>().Position);
    }

    [Fact]
    public async Task ConcealedControlledEntityCanStillBeNoticedUpClose()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(8, 4)));
        ApplyStatus(session, session.Engine.State.ControlledEntity, "river_concealed", duration: 4);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(result.Deltas, delta => delta.Operation == "attack" && delta.Target == "player");
    }

    [Fact]
    public async Task BurningStatusDamagesOnTurnPump()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(soldier.Get<ActorComponent>() with { HitPoints = 5, MaxHitPoints = 10 });
        ApplyStatus(session, soldier, "burning", duration: 3);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(3, soldier.Get<ActorComponent>().HitPoints);
        Assert.Contains(result.Messages, message => message.Contains("ongoing harm", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task RegeneratingStatusHealsOnTurnPump()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        player.Set(player.Get<ActorComponent>() with { HitPoints = 10, MaxHitPoints = 24 });
        ApplyStatus(session, player, "mending", duration: 3);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(12, player.Get<ActorComponent>().HitPoints);
        Assert.Contains(result.Messages, message => message.Contains("regenerate", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task OngoingHarmCanDefeatActorsAndClearBlocking()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(soldier.Get<ActorComponent>() with { HitPoints = 1, MaxHitPoints = 10 });
        ApplyStatus(session, soldier, "burning", duration: 3);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.False(soldier.Get<ActorComponent>().Alive);
        Assert.False(soldier.Get<PhysicalComponent>().BlocksMovement);
        Assert.Contains(result.Messages, message => message.Contains("ongoing harm", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task CreateTriggerDelaysStatusUntilDueTurn()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The spell hides a coal in tomorrow.",
            new SpellEffect(
                "createTrigger",
                new Dictionary<string, object?>
                {
                    ["name"] = "boot coal",
                    ["kind"] = "delay",
                    ["delay"] = 2,
                    ["target"] = "soldier_1",
                    ["effectType"] = "addStatus",
                    ["status"] = "burning",
                    ["duration"] = 3,
                    ["description"] = "The delayed magic opens its hand.",
                })))));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;

        var cast = await session.ExecuteAsync(new CastCommand("make the soldier burn two turns from now"));

        Assert.True(cast.Success);
        Assert.Single(session.Engine.State.Triggers.Records);
        Assert.DoesNotContain(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");

        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "triggerApplyStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["triggerName"], "boot coal"));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateTrigger)
            && Equals(delta.Details["action"], "complete")
            && Equals(delta.Details["triggerName"], "boot coal")
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Messages, message => message.Contains("delayed magic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wait.Messages, message =>
            message.Contains("boot coal completed", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenCreateTriggerDoesNotWritePlayerMessage()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "hidden bell",
            "delay",
            delay: 2,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "message",
            effectFields: new Dictionary<string, object?> { ["text"] = "The hidden bell rings." },
            description: "The hidden bell waits.",
            playerVisible: false,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenTrigger"));

        Assert.True(result.Applied);
        Assert.Empty(result.Messages);
        var trigger = Assert.Single(session.Engine.State.Triggers.Records);
        Assert.Equal("hidden bell", trigger.Name);
        Assert.False(trigger.PlayerVisible);
        var delta = Assert.Single(result.Deltas);
        Assert.Equal("testHiddenTrigger", delta.Operation);
        Assert.False(delta.IsPlayerVisible());
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("hidden bell settles into a later turn", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenTriggerMessageDoesNotWritePlayerMessageWhenItFires()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var result = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "hidden bell",
            "delay",
            delay: 1,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "message",
            effectFields: new Dictionary<string, object?> { ["text"] = "The hidden bell rings." },
            description: "The hidden bell waits.",
            playerVisible: false,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenTrigger"));

        var deltas = session.Engine.AdvanceTurn();

        Assert.True(result.Applied);
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.Contains(deltas, delta =>
            delta.Operation == "trigger"
            && Equals(delta.Details["triggerName"], "hidden bell")
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "message"
            && Equals(delta.Details["triggerName"], "hidden bell")
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("hidden bell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("hidden bell", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void TriggerWithMissingExplicitTargetDoesNotFallBackToPlayer()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var hpBefore = player.Get<ActorComponent>().HitPoints;
        var result = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "lost ember",
            "delay",
            delay: 1,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "damage",
            effectFields: new Dictionary<string, object?>
            {
                ["target"] = "missing_soldier",
                ["amount"] = 5,
                ["damageType"] = "ember",
            },
            description: "The lost ember looks for its named victim.",
            playerVisible: true,
            operation: "testMissingTargetTrigger"));

        var deltas = session.Engine.AdvanceTurn();

        Assert.True(result.Applied);
        Assert.Equal(hpBefore, player.Get<ActorComponent>().HitPoints);
        Assert.DoesNotContain(deltas, delta => delta.Operation == "triggerDamage");
        Assert.Contains(deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateTrigger)
            && Equals(delta.Details["action"], "complete")
            && Equals(delta.Details["triggerName"], "lost ember"));
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void TriggerCanDeliverGenericTypedConsequence()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var brazier = session.Engine.EntityById("brazier_1")!;
        var result = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "delayed tag",
            "delay",
            delay: 1,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "consequence",
            effectFields: new Dictionary<string, object?>
            {
                ["consequenceType"] = "addTags",
                ["targetEntityId"] = brazier.Id.Value,
                ["operation"] = "triggerAddTags",
                ["tags"] = new[] { "trigger_blossom" },
            },
            description: "The trigger adds a tag through the shared consequence grammar.",
            playerVisible: false,
            operation: "testGenericTrigger"));

        var deltas = session.Engine.AdvanceTurn();

        Assert.True(result.Applied);
        Assert.Contains(brazier.Get<TagsComponent>().Tags, tag => tag == "trigger_blossom");
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.Contains(deltas, delta =>
            delta.Operation == "triggerAddTags"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["triggerId"], result.TargetId)
            && Equals(delta.Details["triggerName"], "delayed tag"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateTrigger)
            && Equals(delta.Details["action"], "complete")
            && Equals(delta.Details["triggerName"], "delayed tag"));
    }

    [Fact]
    public void GenericTriggerConsequenceMergesTopLevelFieldsIntoNestedPayload()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var brazier = session.Engine.EntityById("brazier_1")!;
        var result = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "delayed blue mark",
            "delay",
            delay: 1,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "consequence",
            effectFields: new Dictionary<string, object?>
            {
                ["consequenceType"] = "apply_status",
                ["targetEntityId"] = brazier.Id.Value,
                ["status"] = "trigger-blue",
                ["duration"] = 2,
                ["displayName"] = "trigger blue",
                ["consequencePayload"] = new Dictionary<string, object?>
                {
                    ["operation"] = "triggerTopLevelStatus",
                },
            },
            description: "The trigger applies a status through merged consequence payload fields.",
            playerVisible: false,
            operation: "testGenericTriggerMergedPayload"));

        var deltas = session.Engine.AdvanceTurn();

        Assert.True(result.Applied);
        Assert.Contains(brazier.Get<StatusContainerComponent>().Statuses, status =>
            status.Id.Equals("trigger_blue", StringComparison.OrdinalIgnoreCase)
            && status.DisplayName.Equals("trigger blue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deltas, delta =>
            delta.Operation == "triggerTopLevelStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["triggerId"], result.TargetId)
            && Equals(delta.Details["status"], "trigger_blue")
            && Equals(delta.Details["duration"], 2));
        Assert.DoesNotContain(deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task SpellCreateTriggerCanArmGenericTypedConsequence()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The brazier waits for a later sign.",
            new SpellEffect(
                "createTrigger",
                new Dictionary<string, object?>
                {
                    ["name"] = "later brazier mark",
                    ["kind"] = "delay",
                    ["delay"] = 2,
                    ["effect"] = new Dictionary<string, object?>
                    {
                        ["type"] = "consequence",
                        ["consequenceType"] = "addTags",
                        ["targetEntityId"] = "brazier_1",
                        ["tags"] = new[] { "spell_trigger_mark" },
                    },
                    ["description"] = "The brazier takes the sign later.",
                })))));
        DisableImperialAi(session);
        var brazier = session.Engine.EntityById("brazier_1")!;

        var cast = await session.ExecuteAsync(new CastCommand("mark the brazier one turn from now"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(cast.Success);
        Assert.Contains(brazier.Get<TagsComponent>().Tags, tag => tag == "spell_trigger_mark");
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "addTag"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["triggerName"], "later brazier mark"));
    }

    [Fact]
    public void RejectedGenericTriggerConsequenceRollsBackAndExpires()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var result = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "broken delayed tag",
            "delay",
            delay: 1,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "consequence",
            effectFields: new Dictionary<string, object?>
            {
                ["consequenceType"] = WorldConsequenceTypes.AddTags,
                ["targetEntityId"] = "missing_fixture",
                ["operation"] = "triggerMissingAddTags",
                ["tags"] = new[] { "impossible" },
            },
            description: "The trigger tries to add a tag to a missing fixture.",
            playerVisible: false,
            operation: "testBrokenGenericTrigger"));

        var deltas = session.Engine.AdvanceTurn();
        var secondDeltas = session.Engine.AdvanceTurn();

        Assert.True(result.Applied);
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.DoesNotContain(deltas, delta => delta.Operation == "triggerMissingAddTags");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags));
        Assert.Contains(deltas, delta =>
            delta.Operation == "triggerSkipped"
            && Equals(delta.Target, result.TargetId)
            && Equals(delta.Details["effectType"], "consequence")
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["triggerId"], result.TargetId)
            && Equals(delta.Details["action"], "expire"));
        Assert.DoesNotContain(secondDeltas, delta =>
            delta.Operation == "triggerSkipped"
            && Equals(delta.Target, result.TargetId));
    }

    [Fact]
    public void MalformedGenericTriggerConsequenceSkipsAsHiddenAuditAndExpires()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var result = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "nameless consequence",
            "delay",
            delay: 1,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "consequence",
            effectFields: new Dictionary<string, object?>
            {
                ["targetEntityId"] = "brazier_1",
                ["operation"] = "triggerMissingConsequenceType",
                ["tags"] = new[] { "should_not_land" },
            },
            description: "The trigger is malformed and should not speak in the world.",
            playerVisible: true,
            operation: "testMalformedGenericTrigger"));

        var deltas = session.Engine.AdvanceTurn();
        var secondDeltas = session.Engine.AdvanceTurn();

        Assert.True(result.Applied);
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.DoesNotContain(session.Engine.EntityById("brazier_1")!.Get<TagsComponent>().Tags, tag =>
            tag.Equals("should_not_land", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(deltas, delta => delta.Operation == "triggerMissingConsequenceType");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Target, result.TargetId)
            && Equals(delta.Details["consequenceType"], "trigger_consequence")
            && ((string)delta.Details["error"]!).Contains("consequenceType", StringComparison.OrdinalIgnoreCase)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "triggerSkipped"
            && Equals(delta.Target, result.TargetId)
            && Equals(delta.Details["effectType"], "consequence")
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["triggerId"], result.TargetId)
            && Equals(delta.Details["action"], "expire"));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("nameless consequence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secondDeltas, delta =>
            delta.Operation == "triggerSkipped"
            && Equals(delta.Target, result.TargetId));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void UnsupportedTriggerEffectSkipsAsHiddenAuditAndExpires()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var result = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "wrong-shaped bell",
            "delay",
            delay: 1,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "ring_moon",
            effectFields: new Dictionary<string, object?>(),
            description: "The wrong-shaped bell should not narrate a content error.",
            playerVisible: true,
            operation: "testUnsupportedTriggerEffect"));

        var deltas = session.Engine.AdvanceTurn();
        var secondDeltas = session.Engine.AdvanceTurn();

        Assert.True(result.Applied);
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Target, result.TargetId)
            && Equals(delta.Details["consequenceType"], "trigger_consequence")
            && Equals(delta.Details["effectType"], "ring_moon")
            && ((string)delta.Details["error"]!).Contains("Unsupported trigger effect type", StringComparison.OrdinalIgnoreCase)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "triggerSkipped"
            && Equals(delta.Target, result.TargetId)
            && Equals(delta.Details["effectType"], "ring_moon")
            && Equals(delta.Details["rejectedCount"], 1)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["triggerId"], result.TargetId)
            && Equals(delta.Details["action"], "expire"));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("wrong-shaped bell", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ring_moon", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secondDeltas, delta =>
            delta.Operation == "triggerSkipped"
            && Equals(delta.Target, result.TargetId));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenPersistentEffectDoesNotWritePlayerMessage()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;

        var result = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "damage",
            new Dictionary<string, object?>
            {
                ["target"] = "other",
                ["amount"] = 1,
                ["damageType"] = "thorns",
            },
            uses: 1,
            playerVisible: false,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenPersistentEffect"));

        Assert.True(result.Applied);
        Assert.Empty(result.Messages);
        var effect = Assert.Single(session.Engine.State.PersistentEffects.Records);
        Assert.False(effect.PlayerVisible);
        var delta = Assert.Single(result.Deltas);
        Assert.Equal("testHiddenPersistentEffect", delta.Operation);
        Assert.False(delta.IsPlayerVisible());
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("lasting mark", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sympathetic link", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenPersistentMessageDoesNotWritePlayerMessageWhenItFires()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "message",
            new Dictionary<string, object?>
            {
                ["text"] = "The hidden ward rings.",
            },
            uses: 1,
            playerVisible: false,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: player.Id.Value,
            operation: "testHiddenPersistentMessage"));

        var deltas = session.Engine.AttackEntity(soldier, player);

        Assert.True(created.Applied);
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
        Assert.Contains(deltas, delta =>
            delta.Operation == "persistentMessage"
            && Equals(delta.Details["persistentEffectId"], created.TargetId)
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("hidden ward", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("hidden ward", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenMessageConsequenceDoesNotWritePlayerMessage()
    {
        var session = GameSession.CreateImperialEncounter();

        var hidden = session.Engine.ApplyConsequence(WorldConsequence.Message(
            "test",
            "Secret audit note one.",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenMessage",
            details: new Dictionary<string, object?> { ["playerVisible"] = false }));
        var audit = session.Engine.ApplyConsequence(WorldConsequence.Message(
            "test",
            "Secret audit note two.",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testAuditMessage",
            details: new Dictionary<string, object?> { ["auditOnly"] = true }));

        Assert.True(hidden.Applied);
        Assert.True(audit.Applied);
        Assert.Empty(hidden.Messages);
        Assert.Empty(audit.Messages);
        Assert.False(hidden.Deltas.Single().IsPlayerVisible());
        Assert.False(audit.Deltas.Single().IsPlayerVisible());
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("Secret audit note", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenTacticalConsequencesDoNotWritePlayerMessages()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;
        var position = player.Get<PositionComponent>().Position;
        var hidden = new Dictionary<string, object?> { ["playerVisible"] = false };
        var messageCount = session.Engine.State.Messages.Count;

        var damage = session.Engine.ApplyConsequence(WorldConsequence.Damage(
            "test",
            player.Id.Value,
            1,
            "quiet",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenDamage",
            details: hidden));
        var heal = session.Engine.ApplyConsequence(WorldConsequence.Heal(
            "test",
            player.Id.Value,
            1,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenHeal",
            details: hidden));
        var status = session.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
            "test",
            player.Id.Value,
            "silent_mark",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenStatus",
            details: hidden));
        var terrain = session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test",
            position.X,
            position.Y,
            "silent_growth",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenTerrain",
            details: hidden));
        var move = session.Engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "test",
            player.Id.Value,
            position.X,
            position.Y,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenMove",
            details: hidden));

        Assert.All(new[] { damage, heal, status, terrain, move }, result =>
        {
            Assert.True(result.Applied);
            Assert.Empty(result.Messages);
            Assert.False(result.Deltas.Single().IsPlayerVisible());
        });
        Assert.Contains(player.Get<StatusContainerComponent>().Statuses, item => item.Id == "silent_mark");
        Assert.Equal("silent_growth", session.Engine.State.Terrain[position]);
        Assert.Equal(messageCount, session.Engine.State.Messages.Count);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenSpawnInventoryAndEquipmentConsequencesDoNotWritePlayerMessages()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;
        var position = player.Get<PositionComponent>().Position;
        var spawnPoint = OpenPoint(session, 0);
        var itemPoint = OpenPoint(session, 1);
        var fixturePoint = OpenPoint(session, 2);
        var hidden = new Dictionary<string, object?> { ["playerVisible"] = false };
        var messageCount = session.Engine.State.Messages.Count;

        var spawnEntity = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "silent helper",
            spawnPoint.X,
            spawnPoint.Y,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenSpawnEntity",
            details: hidden));
        var spawnItem = session.Engine.ApplyConsequence(WorldConsequence.SpawnItem(
            "test",
            "silent bead",
            itemPoint.X,
            itemPoint.Y,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenSpawnItem",
            details: hidden));
        var spawnFixture = session.Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
            "test",
            "silent marker",
            fixturePoint.X,
            fixturePoint.Y,
            blocksMovement: false,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenSpawnFixture",
            details: hidden));
        var inventory = session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "test",
            player.Id.Value,
            "silent_coin",
            "add",
            1,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenInventory",
            emitMessage: true,
            details: hidden));
        var transfer = session.Engine.ApplyConsequence(WorldConsequence.TransferItem(
            "test",
            player.Id.Value,
            "drop",
            "silent_coin",
            quantity: 1,
            x: position.X,
            y: position.Y,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenTransfer",
            emitMessage: true,
            details: hidden));
        var equipment = session.Engine.ApplyConsequence(WorldConsequence.UpdateEquipment(
            "test",
            player.Id.Value,
            "equip",
            item: "charcoal wand",
            slot: "hand",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenEquipment",
            emitMessage: true,
            details: hidden));

        Assert.All(new[] { spawnEntity, spawnItem, spawnFixture, inventory, transfer, equipment }, result =>
        {
            Assert.True(result.Applied);
            Assert.Empty(result.Messages);
            Assert.False(result.Deltas.Single().IsPlayerVisible());
        });
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name.Equals("silent helper", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name.Equals("silent bead", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name.Equals("silent marker", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(player.Get<EquipmentComponent>().Slots, pair =>
            pair.Key == "hand" && pair.Value == "charcoal wand");
        Assert.Equal(messageCount, session.Engine.State.Messages.Count);
        Assert.True(session.Engine.ValidateState().IsValid);

        static GridPoint OpenPoint(GameSession session, int index)
        {
            var found = 0;
            for (var y = 1; y < session.Engine.State.Height - 1; y++)
            {
                for (var x = 1; x < session.Engine.State.Width - 1; x++)
                {
                    var point = new GridPoint(x, y);
                    if (session.Engine.State.BlockingTerrain.Contains(point)
                        || session.Engine.BlockingEntityAt(point) is not null)
                    {
                        continue;
                    }

                    if (found++ == index)
                    {
                        return point;
                    }
                }
            }

            throw new InvalidOperationException("No open test point was available.");
        }
    }

    [Fact]
    public void HiddenClaimUpdateAndTradeConsequencesDoNotWritePlayerMessages()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;
        var lio = session.Engine.EntityById("prisoner_1")!;
        lio.Set(new MerchantComponent(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["silent apple"] = 1 },
            Gold: 20));
        var inventory = player.Get<InventoryComponent>();
        inventory.Items["gold"] = 10;
        player.Set(inventory);
        var claim = session.Engine.ApplyConsequence(WorldConsequence.RecordClaim(
            "test",
            lio.Id.Value,
            "player_soul",
            "There is a silent apple under the ash.",
            "item",
            "silent apple",
            salience: 3,
            confidence: 80,
            playerVisible: false,
            operation: "testHiddenClaim"));
        var claimRecord = Assert.Single(session.Engine.State.Claims.Records, item =>
            item.Text == "There is a silent apple under the ash.");
        var messageCount = session.Engine.State.Messages.Count;

        var updateClaim = session.Engine.ApplyConsequence(WorldConsequence.UpdateClaim(
            "test",
            claimRecord.Id,
            status: "bound",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenClaimUpdate",
            emitMessage: true));
        var trade = session.Engine.ApplyConsequence(WorldConsequence.ExecuteTrade(
            "test",
            lio.Id.Value,
            player.Id.Value,
            "buy",
            "silent apple",
            "silent apple",
            price: 3,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenTrade",
            emitMessage: true,
            details: new Dictionary<string, object?> { ["playerVisible"] = false }));

        Assert.True(claim.Applied);
        Assert.True(updateClaim.Applied);
        Assert.True(trade.Applied);
        Assert.Empty(updateClaim.Messages);
        Assert.Empty(trade.Messages);
        Assert.False(updateClaim.Deltas.Single().IsPlayerVisible());
        Assert.False(trade.Deltas.Single().IsPlayerVisible());
        Assert.Equal("bound", session.Engine.State.Claims.Records.Single(item => item.Id == claimRecord.Id).Status);
        Assert.Equal(7, player.Get<InventoryComponent>().Items["gold"]);
        Assert.Equal(1, player.Get<InventoryComponent>().Items["silent apple"]);
        Assert.Equal(messageCount, session.Engine.State.Messages.Count);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void RequestServiceConsequenceCommitsEffectPaymentAndNarration()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;
        player.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;
        var door = session.Engine.EntityById("cell_door_1")!;
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "ward_breaking",
            "ward-breaking",
            "A hush-hush folk charm that worries a lock open.",
            "open_or_unlock",
            itemCost: "grave salt",
            targetHint: "cell door",
            operation: "testOfferService"));

        var applied = session.Engine.ApplyConsequence(WorldConsequence.RequestService(
            "test",
            lio.Id.Value,
            "ward-breaking",
            player.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: player.Id.Value,
            operation: "testRequestService"));

        Assert.True(offer.Applied, offer.Error);
        Assert.True(applied.Applied, applied.Error);
        Assert.Contains(applied.Deltas, delta =>
            delta.Operation == "testRequestService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RequestService)
            && Equals(delta.Details["serviceId"], "ward_breaking"));
        Assert.Contains(applied.Deltas, delta =>
            delta.Operation == "serviceCost"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ModifyInventory)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.RequestService));
        Assert.Contains(applied.Deltas, delta => delta.Operation == "serviceOpenOrUnlock");
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.Equal(1, player.Get<InventoryComponent>().Items["grave salt"]);
        Assert.Contains(session.Engine.State.Messages, message =>
            message.Contains("provides ward-breaking", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task RepeatingTriggerAdvancesThroughSharedLifecycleConsequence()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The spell teaches a bell to ring twice.",
            new SpellEffect(
                "createTrigger",
                new Dictionary<string, object?>
                {
                    ["name"] = "twice bell",
                    ["kind"] = "delay",
                    ["delay"] = 2,
                    ["interval"] = 2,
                    ["uses"] = 2,
                    ["effectType"] = "message",
                    ["text"] = "The twice bell rings.",
                    ["description"] = "The twice bell wakes.",
                })))));
        DisableImperialAi(session);

        var cast = await session.ExecuteAsync(new CastCommand("make a bell ring twice"));
        var firstWait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(cast.Success);
        Assert.Contains(firstWait.Deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateTrigger)
            && Equals(delta.Details["action"], "advance")
            && Equals(delta.Details["triggerName"], "twice bell")
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(firstWait.Messages, message =>
            message.Contains("twice bell advances", StringComparison.OrdinalIgnoreCase));
        Assert.Single(session.Engine.State.Triggers.Records);

        var secondWait = await session.ExecuteAsync(new WaitCommand());
        var thirdWait = await session.ExecuteAsync(new WaitCommand());

        Assert.DoesNotContain(secondWait.Deltas, delta => delta.Operation == "trigger");
        Assert.Contains(thirdWait.Deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateTrigger)
            && Equals(delta.Details["action"], "complete")
            && Equals(delta.Details["triggerName"], "twice bell")
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(thirdWait.Messages, message =>
            message.Contains("twice bell completed", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task CreateTriggerAuraAppliesStatusByRadiusAndFilter()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "A sour green ring begins to breathe.",
            new SpellEffect(
                "createTrigger",
                new Dictionary<string, object?>
                {
                    ["name"] = "green ring",
                    ["kind"] = "aura",
                    ["delay"] = 1,
                    ["uses"] = 1,
                    ["anchor"] = "player",
                    ["radius"] = 4,
                    ["targetFilter"] = "enemies",
                    ["effectType"] = "addStatus",
                    ["status"] = "poisoned",
                    ["duration"] = 3,
                    ["description"] = "The green ring breathes outward.",
                })))));
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(11, 5)));
        var soldier = session.Engine.EntityById("soldier_1")!;
        var captain = session.Engine.EntityById("soldier_2")!;
        var lio = session.Engine.EntityById("prisoner_1")!;

        var result = await session.ExecuteAsync(new CastCommand("make an enemy-poisoning aura around me"));

        Assert.True(result.Success);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "poisoned");
        Assert.Contains(captain.Get<StatusContainerComponent>().Statuses, status => status.Id == "poisoned");
        Assert.DoesNotContain(lio.Get<StatusContainerComponent>().Statuses, status => status.Id == "poisoned");
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "updateTrigger"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateTrigger)
            && Equals(delta.Details["action"], "complete")
            && Equals(delta.Details["triggerName"], "green ring"));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task InvalidTriggerShapeRejectsWithoutArmingTrigger()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The spell tries to invent an impossible machine.",
            new SpellEffect(
                "createTrigger",
                new Dictionary<string, object?>
                {
                    ["kind"] = "delay",
                    ["delay"] = 2,
                    ["effectType"] = "rewritePhysics",
                    ["description"] = "This should never settle.",
                })))));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("make an impossible trigger"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.Contains(result.Messages, message => message.Contains("Unsupported trigger effect", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceRoutesThroughSharedApplier()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "A little blue warrant folds itself into your sleeve.",
            new SpellEffect(
                "worldConsequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = "addTags",
                    ["target"] = "self",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["tags"] = new[] { "spell_marked", "rain_authorized" },
                        ["operation"] = "wildMagicConsequenceTest",
                    },
                    ["visibility"] = WorldConsequenceVisibility.Hidden,
                    ["reason"] = "The spell marked its caster through the shared consequence grammar.",
                })))));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("let the rain deputize me"));

        Assert.True(result.Success);
        Assert.Contains("consequence", result.Magic!.EffectTypes);
        var tags = session.Engine.State.ControlledEntity.Get<TagsComponent>().Tags;
        Assert.Contains("spell_marked", tags);
        Assert.Contains("rain_authorized", tags);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicConsequenceTest"
            && delta.Target == "player"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceAcceptsNestedPayloadAliases()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The brazier remembers the rain in its brass.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["payload"] = new Dictionary<string, object?>
                    {
                        ["world_consequence_type"] = "addTags",
                        ["target_id"] = "brazier_1",
                        ["tags"] = new[] { "rain_remembering" },
                        ["operation"] = "testNestedAliasAddTags",
                        ["consequence_visibility"] = WorldConsequenceVisibility.Hidden,
                    },
                })))));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("make the brazier remember the rain"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.False(result.TechnicalFailure);
        Assert.Contains("rain_remembering", session.Engine.EntityById("brazier_1")!.Get<TagsComponent>().Tags);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "testNestedAliasAddTags"
            && delta.Target == "brazier_1"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["visibility"], WorldConsequenceVisibility.Hidden));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceMergesTopLevelFieldsIntoNestedPayload()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The prisoner carries the blue mark for two breaths.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = "apply_status",
                    ["targetEntityId"] = "prisoner_1",
                    ["status"] = "blue-marked",
                    ["duration"] = 2,
                    ["displayName"] = "blue marked",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["operation"] = "wildMagicTopLevelStatus",
                    },
                })))));
        DisableImperialAi(session);
        var lio = session.Engine.EntityById("prisoner_1")!;

        var result = await session.ExecuteAsync(new CastCommand("mark Lio with blue for two breaths"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Contains(lio.Get<StatusContainerComponent>().Statuses, status =>
            status.Id.Equals("blue_marked", StringComparison.OrdinalIgnoreCase)
            && status.DisplayName.Equals("blue marked", StringComparison.OrdinalIgnoreCase)
            && status.ExpiresTurn is not null);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicTopLevelStatus"
            && delta.Target == "prisoner_1"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["status"], "blue_marked")
            && Equals(delta.Details["duration"], 2));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceCanRequestExistingService()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The lock hears Lio's ward-breaking through your spell.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = "requestService",
                    ["targetEntityId"] = "prisoner_1",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["service"] = "ward-breaking",
                        ["operation"] = "wildMagicRequestService",
                    },
                    ["visibility"] = WorldConsequenceVisibility.Message,
                    ["reason"] = "The spell invoked an existing service affordance through the shared consequence grammar.",
                })))));
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;
        var door = session.Engine.EntityById("cell_door_1")!;
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "ward_breaking",
            "ward-breaking",
            "A hush-hush folk charm that worries a lock open.",
            "open_or_unlock",
            itemCost: "grave salt",
            targetHint: "cell door",
            operation: "testOfferService"));

        var result = await session.ExecuteAsync(new CastCommand("carry Lio's ward-breaking into the cell lock"));

        Assert.True(offer.Applied, offer.Error);
        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Contains("consequence", result.Magic!.EffectTypes);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicRequestService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RequestService)
            && Equals(delta.Details["serviceId"], "ward_breaking"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "serviceCost"
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.RequestService));
        Assert.Contains(result.Deltas, delta => delta.Operation == "serviceOpenOrUnlock");
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.Equal(1, session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["grave salt"]);
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceCanScheduleSharedEffectWithTiming()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The mark waits in the turn pump before showing itself.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = WorldConsequenceTypes.AddTags,
                    ["target"] = "self",
                    ["timing"] = WorldConsequenceTiming.Deferred,
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["tags"] = new[] { "timed_magic_mark" },
                        ["operation"] = "testTimedMagicAddTags",
                        ["delay"] = 2,
                    },
                })))));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("mark me one turn from now"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.True(result.ConsumedTurn);
        Assert.False(result.TechnicalFailure);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "scheduleTimedConsequence"
            && Equals(delta.Details["scheduledConsequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["scheduledTiming"], WorldConsequenceTiming.Deferred));
        Assert.False(session.Engine.State.ControlledEntity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("timed_magic_mark", StringComparer.OrdinalIgnoreCase));

        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(wait.Success);
        Assert.Contains("timed_magic_mark", session.Engine.State.ControlledEntity.Get<TagsComponent>().Tags);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "testTimedMagicAddTags"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequencePromiseSatisfiesPromiseNarrativeGuard()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The omen enters the world as a lead the future can answer.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = "createPromise",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["kind"] = "omen",
                        ["text"] = "A blue blade waits north of here.",
                        ["subject"] = "blue blade",
                        ["claimedPlace"] = "north of here",
                        ["triggerHint"] = "travel",
                        ["realizationKind"] = "item",
                        ["operation"] = "testMagicCreatePromise",
                        ["salience"] = 4,
                    },
                    ["visibility"] = WorldConsequenceVisibility.Message,
                    ["reason"] = "The spell made a future-facing claim through the shared consequence grammar.",
                })))));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("make an omen that a blue blade waits north"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.False(result.TechnicalFailure);
        Assert.Contains("consequence", result.Magic!.EffectTypes);
        Assert.DoesNotContain(result.Messages, message => message.Contains("promised future", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Kind == "omen"
            && promise.Text == "A blue blade waits north of here."
            && promise.ClaimedPlace == "north of here"
            && promise.TriggerHint == "travel"
            && promise.RealizationKind == "item"
            && promise.Salience == 4);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "testMagicCreatePromise"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreatePromise)
            && Equals(delta.Details["kind"], "omen"));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceRouteSatisfiesNarrativeRouteGuard()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "A hidden route opens beyond the containment brazier.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = "createRoute",
                    ["targetEntityId"] = "brazier_1",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["name"] = "brazier crawlway",
                        ["description"] = "A narrow crawlway opens where the brazier smoke thins.",
                        ["routeKind"] = "hidden_route",
                        ["operation"] = "testMagicCreateRoute",
                    },
                })))));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("reveal the hidden route beyond the brazier"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.False(result.TechnicalFailure);
        Assert.Contains("consequence", result.Magic!.EffectTypes);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "testMagicCreateRoute"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreateRoute)
            && Equals(delta.Details["anchorEntityId"], "brazier_1"));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceResolvesSelectorSentUnderTargetId()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "A quiet mark finds the nearest soldier.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = WorldConsequenceTypes.AddTags,
                    // A live-model dialect: a selector word sent under targetId rather than
                    // target. This must resolve like any other selector, not be treated as a
                    // literal (and nonexistent) entity id named "nearest_enemy".
                    ["targetId"] = "nearest_enemy",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["tags"] = new[] { "marked_by_selector" },
                        ["operation"] = "testMagicSelectorTag",
                    },
                })))));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("mark the nearest soldier"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.False(result.TechnicalFailure);
        Assert.Contains(
            session.Engine.EntityById("soldier_1")!.Get<TagsComponent>().Tags,
            tag => tag == "marked_by_selector");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "testMagicSelectorTag"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceDoorEffectSatisfiesNarrativeDoorGuard()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The cell door opens without touching the key.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = "openOrUnlock",
                    ["targetEntityId"] = "cell_door_1",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["unlock"] = true,
                        ["open"] = true,
                        ["operation"] = "testMagicOpenDoor",
                    },
                })))));
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));

        var result = await session.ExecuteAsync(new CastCommand("open the cell door with wild magic"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.False(result.TechnicalFailure);
        Assert.True(session.Engine.EntityById("cell_door_1")!.Get<DoorComponent>().IsOpen);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "testMagicOpenDoor"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OpenOrUnlock)
            && Equals(delta.Details["open"], true));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task AcceptedMagicRollsBackWhenAChildConsequenceRejects()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "The soldier is marked, then the magic reaches for someone absent.",
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = WorldConsequenceTypes.AddTags,
                    ["targetEntityId"] = "soldier_1",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["operation"] = "testMagicAddTag",
                        ["tags"] = new[] { "half_applied_magic" },
                    },
                }),
            new SpellEffect(
                "consequence",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = WorldConsequenceTypes.AddTags,
                    ["targetEntityId"] = "missing_fixture",
                    ["consequencePayload"] = new Dictionary<string, object?>
                    {
                        ["operation"] = "testMagicMissingAddTag",
                        ["tags"] = new[] { "impossible" },
                    },
                })))));
        DisableImperialAi(session);
        var turnBefore = session.Engine.State.Turn;

        var result = await session.ExecuteAsync(new CastCommand("mark the soldier and the absent fixture"));
        var restoredSoldier = session.Engine.EntityById("soldier_1")!;

        Assert.False(result.Success);
        // A world-refused consequence in an accepted cast is an in-world rejection that
        // consumes the turn, never a free-retry technical failure (CORE_EXECUTION_MODEL section 5).
        Assert.False(result.TechnicalFailure);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(turnBefore + 1, session.Engine.State.Turn);
        Assert.DoesNotContain(restoredSoldier.Get<TagsComponent>().Tags, tag => tag == "half_applied_magic");
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "testMagicAddTag");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && delta.Target == "missing_fixture"
            && !delta.IsPlayerVisible());
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message));
        Assert.Contains("missing_fixture", result.Magic!.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task AddCurseCreatesVisibleMechanicalCursePromise()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("curse me with a close curse"));

        Assert.True(result.Success);
        var curse = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise => promise.Kind == "curse");
        Assert.True(curse.PlayerVisible);
        Assert.Equal("close", curse.TriggerHint);
        Assert.Contains("close curse", curse.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepeatingSameCurseStacksInsteadOfDuplicating()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
        DisableImperialAi(session);

        await session.ExecuteAsync(new CastCommand("curse me with a close curse"));
        var result = await session.ExecuteAsync(new CastCommand("curse me with a close curse"));

        Assert.True(result.Success);
        var curse = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise => promise.Kind == "curse");
        Assert.Equal(2, curse.Stacks);
        Assert.Contains(result.Messages, message => message.Contains("deepens", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NegativeNumericMagicCostBitesForItsAbsoluteMagnitude()
    {
        var provider = new NegativeManaCostSpellProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));

        var result = await session.ExecuteAsync(new CastCommand("make the nearest soldier's armor bloom"));

        Assert.True(result.Success);
        var costDelta = Assert.Single(result.Deltas, delta => delta.Operation == "cost:mana");
        Assert.Equal(5, costDelta.Details["amount"]);
    }

    [Fact]
    public async Task CurseSpellCostsUseSharedPromiseConsequenceAndStackAsCosts()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "The bell tolls from under the floor.",
            Effects: new[]
            {
                new SpellEffect(
                    "message",
                    new Dictionary<string, object?>
                    {
                        ["text"] = "The spell answers in brass.",
                    }),
            },
            Costs: new[]
            {
                new SpellCost(
                    "curse",
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Bell Debt",
                        ["description"] = "A red bell will answer this spell later.",
                    }),
            },
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var first = await session.ExecuteAsync(new CastCommand("cast with a bell debt"));
        var second = await session.ExecuteAsync(new CastCommand("cast with the same bell debt again"));

        Assert.True(first.Success);
        Assert.True(second.Success);
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, record => record.Kind == "debt");
        Assert.True(promise.PlayerVisible);
        Assert.Equal("A red bell will answer this spell later.", promise.Text);
        Assert.Equal("unbound", promise.Status);
        Assert.Null(promise.ClaimedPlace);
        Assert.Null(promise.BoundPlace);
        Assert.Equal(2, promise.Stacks);
        var firstCost = Assert.Single(first.Deltas, delta => delta.Operation == "cost:curse");
        Assert.Equal(WorldConsequenceTypes.CreatePromise, firstCost.Details["consequenceType"]);
        Assert.Equal("Bell Debt", firstCost.Details["name"]);
        Assert.Equal(promise.Id, firstCost.Details["promiseId"]);
        Assert.Equal(1, firstCost.Details["stacks"]);
        var secondCost = Assert.Single(second.Deltas, delta => delta.Operation == "cost:curse");
        Assert.Equal(WorldConsequenceTypes.CreatePromise, secondCost.Details["consequenceType"]);
        Assert.Equal(promise.Id, secondCost.Details["promiseId"]);
        Assert.Equal(2, secondCost.Details["stacks"]);
        Assert.Contains(second.Messages, message => message == "Cost: Bell Debt deepens (2 stacks).");
    }

    [Fact]
    public void HiddenCreatePromiseDoesNotWritePlayerMessage()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "memory",
            "Ricky has a brother named Taylor.",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenPromise",
            playerVisible: false));

        Assert.True(result.Applied);
        Assert.Empty(result.Messages);
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text == "Ricky has a brother named Taylor.");
        Assert.False(promise.PlayerVisible);
        var delta = Assert.Single(result.Deltas);
        Assert.Equal("testHiddenPromise", delta.Operation);
        Assert.False(delta.IsPlayerVisible());
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("Ricky has a brother named Taylor", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenStackedPromiseDoesNotWritePlayerMessage()
    {
        var session = GameSession.CreateImperialEncounter();
        var first = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "memory",
            "Ricky has a brother named Taylor.",
            operation: "testHiddenPromise",
            playerVisible: false,
            emitMessage: false));

        var second = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "memory",
            "Ricky has a brother named Taylor.",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenPromise",
            stackExisting: true,
            playerVisible: false));

        Assert.True(first.Applied);
        Assert.True(second.Applied);
        Assert.Empty(second.Messages);
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text == "Ricky has a brother named Taylor.");
        Assert.False(promise.PlayerVisible);
        Assert.Equal(2, promise.Stacks);
        var delta = Assert.Single(second.Deltas);
        Assert.Equal("testHiddenPromise", delta.Operation);
        Assert.False(delta.IsPlayerVisible());
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("Ricky has a brother named Taylor", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void HiddenUpdatePromiseDoesNotWritePlayerMessage()
    {
        var session = GameSession.CreateImperialEncounter();
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "memory",
            "Ricky has a brother named Taylor.",
            operation: "testHiddenPromise",
            playerVisible: false,
            emitMessage: false));
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text == "Ricky has a brother named Taylor.");
        var messageCount = session.Engine.State.Messages.Count;

        var update = session.Engine.ApplyConsequence(WorldConsequence.UpdatePromise(
            "test",
            promise.Id,
            status: "cleared",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenPromiseUpdate",
            emitMessage: true,
            message: "Ricky's brother note changes."));

        Assert.True(create.Applied);
        Assert.True(update.Applied);
        Assert.Empty(update.Messages);
        var delta = Assert.Single(update.Deltas);
        Assert.Equal("testHiddenPromiseUpdate", delta.Operation);
        Assert.False(delta.IsPlayerVisible());
        Assert.Equal("cleared", session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id).Status);
        Assert.Equal(messageCount, session.Engine.State.Messages.Count);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task OrdinarySpellCostsUseSharedConsequences()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "The charm drinks from ordinary things.",
            Effects: new[]
            {
                new SpellEffect(
                    "message",
                    new Dictionary<string, object?>
                    {
                        ["text"] = "The charm has enough to happen.",
                    }),
            },
            Costs: new[]
            {
                new SpellCost(
                    "item",
                    new Dictionary<string, object?>
                    {
                        ["item"] = "grave salt",
                        ["quantity"] = 1,
                    }),
                new SpellCost(
                    "status",
                    new Dictionary<string, object?>
                    {
                        ["status"] = "strained",
                        ["duration"] = 4,
                    }),
                new SpellCost(
                    "health",
                    new Dictionary<string, object?>
                    {
                        ["amount"] = 2,
                    }),
                new SpellCost(
                    "maxMana",
                    new Dictionary<string, object?>
                    {
                        ["amount"] = 1,
                    }),
            },
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var actorBefore = player.Get<ActorComponent>();

        var result = await session.ExecuteAsync(new CastCommand("cast with ordinary costs"));

        Assert.True(result.Success);
        var inventory = player.Get<InventoryComponent>();
        Assert.Equal(1, inventory.Items["grave salt"]);
        var itemCost = Assert.Single(result.Deltas, delta => delta.Operation == "cost:item");
        Assert.Equal(WorldConsequenceTypes.ModifyInventory, itemCost.Details["consequenceType"]);
        Assert.Equal("grave salt", itemCost.Details["item"]);
        var statusCost = Assert.Single(result.Deltas, delta => delta.Operation == "cost:status");
        Assert.Equal(WorldConsequenceTypes.ApplyStatus, statusCost.Details["consequenceType"]);
        Assert.Contains(player.Get<StatusContainerComponent>().Statuses, status => status.Id == "strained");
        var healthCost = Assert.Single(result.Deltas, delta => delta.Operation == "cost:health");
        Assert.Equal(WorldConsequenceTypes.AdjustActorResource, healthCost.Details["consequenceType"]);
        var maxManaCost = Assert.Single(result.Deltas, delta => delta.Operation == "cost:maxMana");
        Assert.Equal(WorldConsequenceTypes.AdjustActorResource, maxManaCost.Details["consequenceType"]);
        var actorAfter = player.Get<ActorComponent>();
        Assert.Equal(actorBefore.HitPoints - 2, actorAfter.HitPoints);
        Assert.Equal(actorBefore.MaxMana - 1, actorAfter.MaxMana);
        Assert.Equal(actorAfter.MaxMana, session.Engine.State.Souls.Get("player_soul").MaxMana);
        Assert.Contains(result.Messages, message => message == "Cost: 1 grave salt.");
        Assert.Contains(result.Messages, message => message == "Cost: strained.");
    }

    [Fact]
    public async Task AreaStatusAppliesStatusToActorsInRadius()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "A ring of fire rolls outward.",
            Effects: new[]
            {
                new SpellEffect(
                    "areaStatus",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["radius"] = 3,
                        ["status"] = "burning",
                        ["duration"] = 4,
                        ["affects"] = "enemies",
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;

        var result = await session.ExecuteAsync(new CastCommand("cast a ring of fire"));

        Assert.True(result.Success);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
    }

    [Fact]
    public async Task ModifyInventoryAddsItemCountToCaster()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "Coins spill from nowhere.",
            Effects: new[]
            {
                new SpellEffect(
                    "modifyInventory",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "self",
                        ["item"] = "silver coin",
                        ["op"] = "add",
                        ["amount"] = 3,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("cast conjure coins"));

        Assert.True(result.Success);
        var inventory = session.Engine.State.ControlledEntity.Get<InventoryComponent>();
        Assert.Equal(3, inventory.Items["silver_coin"]);
    }

    [Fact]
    public async Task AddTagThenRemoveTagRoundTrips()
    {
        var addResolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "A mark appears.",
            Effects: new[]
            {
                new SpellEffect(
                    "addTag",
                    new Dictionary<string, object?> { ["target"] = "soldier_1", ["tags"] = new[] { "marked_for_death" } }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var addSession = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(addResolution)));
        DisableImperialAi(addSession);
        var markedSoldier = addSession.Engine.EntityById("soldier_1")!;

        await addSession.ExecuteAsync(new CastCommand("cast mark him for death"));
        Assert.Contains("marked_for_death", markedSoldier.Get<TagsComponent>().Tags, StringComparer.OrdinalIgnoreCase);

        var removeResolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "The mark fades.",
            Effects: new[]
            {
                new SpellEffect(
                    "removeTag",
                    new Dictionary<string, object?> { ["target"] = "soldier_1", ["tags"] = new[] { "marked_for_death" } }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var removeSession = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(removeResolution)));
        DisableImperialAi(removeSession);
        var unmarkedSoldier = removeSession.Engine.EntityById("soldier_1")!;
        unmarkedSoldier.Set(new TagsComponent(new[] { "imperial", "soldier", "containment", "marked_for_death" }));

        await removeSession.ExecuteAsync(new CastCommand("cast lift the mark"));
        Assert.DoesNotContain("marked_for_death", unmarkedSoldier.Get<TagsComponent>().Tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConjureItemSpawnsFloorItemFromTemplate()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "Glass condenses from breath.",
            Effects: new[]
            {
                new SpellEffect(
                    "conjureItem",
                    new Dictionary<string, object?>
                    {
                        ["template"] = "glass_shard",
                        ["name"] = "shard of frozen breath",
                        ["count"] = 2,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("cast condense glass from breath"));

        Assert.True(result.Success);
        var spawned = session.Engine.State.Entities.Values.Single(entity => entity.Name == "shard of frozen breath");
        Assert.Equal("reagent", spawned.Get<ItemComponent>().ItemType);
        Assert.Equal(2, spawned.Get<StackComponent>().Quantity);
    }

    [Fact]
    public async Task ConjureFixtureSpawnsNonActorFixtureFromTemplate()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "A moon shrine rises from the floor.",
            Effects: new[]
            {
                new SpellEffect(
                    "conjureFixture",
                    new Dictionary<string, object?>
                    {
                        ["template"] = "shrine",
                        ["name"] = "little moon shrine",
                        ["material"] = "moonstone",
                        ["blocksMovement"] = true,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("cast raise a little moon shrine"));

        Assert.True(result.Success);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "spawnFixture"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnFixture));
        var shrine = session.Engine.State.Entities.Values.Single(entity => entity.Name == "little moon shrine");
        Assert.True(shrine.Has<FixtureComponent>());
        Assert.Equal("shrine", shrine.Get<FixtureComponent>().FixtureType);
        Assert.Equal("moonstone", shrine.Get<PhysicalComponent>().Material);
        Assert.Contains("examine", shrine.Get<InteractableComponent>().Verbs);
    }

    [Fact]
    public async Task ConjureCreatureSpawnsTemplateBackedCreatures()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "Two shapes pour out of the spell like spilled ink.",
            Effects: new[]
            {
                new SpellEffect(
                    "conjureCreature",
                    new Dictionary<string, object?>
                    {
                        ["template"] = "small_beast",
                        ["name"] = "shadow wolf",
                        ["faction"] = "player",
                        ["count"] = 2,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("cast shadow wolves"));

        Assert.True(result.Success);
        var wolves = session.Engine.State.Entities.Values.Where(entity => entity.Name == "shadow wolf").ToArray();
        Assert.Equal(2, wolves.Length);
        Assert.All(wolves, wolf => Assert.Equal("player", wolf.Get<ActorComponent>().Faction));
    }

    [Fact]
    public async Task ResistanceReducesIncomingDamageOfMatchingType()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "Fire licks at scaled armor.",
            Effects: new[]
            {
                new SpellEffect(
                    "damage",
                    new Dictionary<string, object?> { ["target"] = "soldier_1", ["amount"] = 10, ["damageType"] = "fire" }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(new ResistanceComponent(new Dictionary<string, int> { ["fire"] = 50 }, new Dictionary<string, int>()));

        var result = await session.ExecuteAsync(new CastCommand("cast blue fire"));

        Assert.True(result.Success);
        Assert.Equal(5, soldier.Get<ActorComponent>().HitPoints);
    }

    [Fact]
    public async Task WeaknessAmplifiesIncomingDamageOfMatchingType()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "Fire finds an old wound.",
            Effects: new[]
            {
                new SpellEffect(
                    "damage",
                    new Dictionary<string, object?> { ["target"] = "soldier_1", ["amount"] = 4, ["damageType"] = "fire" }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(new ResistanceComponent(new Dictionary<string, int>(), new Dictionary<string, int> { ["fire"] = 50 }));

        var result = await session.ExecuteAsync(new CastCommand("cast blue fire"));

        Assert.True(result.Success);
        Assert.Equal(4, soldier.Get<ActorComponent>().HitPoints);
    }

    [Fact]
    public async Task AddResistanceOperationWritesResistanceComponent()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "Scales harden against flame.",
            Effects: new[]
            {
                new SpellEffect(
                    "addResistance",
                    new Dictionary<string, object?> { ["target"] = "soldier_1", ["damageType"] = "fire", ["amount"] = 40 }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;

        var result = await session.ExecuteAsync(new CastCommand("cast harden scales against flame"));

        Assert.True(result.Success);
        Assert.Equal(40, soldier.Get<ResistanceComponent>().Resistances["fire"]);
    }

    [Fact]
    public async Task SetFlagWithDebtKeywordIncursStackingCurseAndSchedulesReckoning()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "The wild magic notes an unpaid price.",
            Effects: new[]
            {
                new SpellEffect(
                    "setFlag",
                    new Dictionary<string, object?> { ["flag"] = "owed_a_life", ["description"] = "a life is owed" }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("cast a debt into the future"));

        Assert.True(result.Success);
        Assert.Equal(true, session.Engine.State.WorldFlags["owed_a_life"]);
        Assert.Contains(
            session.Engine.State.PromiseLedger.Promises,
            promise => promise.Kind == "curse" && promise.Text.Contains("owed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.ScheduledEvents.Snapshot(), record => record.Kind == "debt_collector");
    }

    [Fact]
    public async Task DelayIncomingBuffersDamageUntilReleaseTurn()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "Wounds are held back for later.",
            Effects: new[]
            {
                new SpellEffect("delayIncoming", new Dictionary<string, object?> { ["target"] = "self", ["turns"] = 2 }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var startingHp = player.Get<ActorComponent>().HitPoints;

        await session.ExecuteAsync(new CastCommand("cast delay my wounds"));
        Assert.True(player.TryGet<DelayedDamageComponent>(out _));

        session.Engine.ApplyConsequence(WorldConsequence.Damage(
            "test_setup",
            player.Id.Value,
            5,
            "physical",
            sourceEntityId: player.Id.Value,
            details: new Dictionary<string, object?> { ["emitMessage"] = false }));
        Assert.Equal(startingHp, player.Get<ActorComponent>().HitPoints);
        Assert.Equal(5, player.Get<DelayedDamageComponent>().Buffered);

        var turnDeltas = session.Engine.AdvanceTurn();

        Assert.False(player.TryGet<DelayedDamageComponent>(out _));
        Assert.True(player.Get<ActorComponent>().HitPoints < startingHp);
        Assert.Contains(turnDeltas, delta =>
            delta.Operation == "releaseDelayedDamage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ReleaseDelayedDamage)
            && Equals(delta.Details["damageType"], "delayed")
            && Equals(delta.Details["buffered"], 5));
    }

    [Fact]
    public void HiddenReleaseDelayedDamageDoesNotWritePlayerMessage()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var startingHp = player.Get<ActorComponent>().HitPoints;
        player.Set(new DelayedDamageComponent(8, session.Engine.State.Turn + 1));
        var messageCount = session.Engine.State.Messages.Count;

        var release = session.Engine.ApplyConsequence(WorldConsequence.ReleaseDelayedDamage(
            "test",
            player.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testHiddenReleaseDelayedDamage",
            emitMessage: true,
            details: new Dictionary<string, object?> { ["playerVisible"] = false }));

        Assert.True(release.Applied);
        Assert.Empty(release.Messages);
        Assert.False(player.TryGet<DelayedDamageComponent>(out _));
        Assert.True(player.Get<ActorComponent>().HitPoints < startingHp);
        var delta = Assert.Single(release.Deltas);
        Assert.Equal("testHiddenReleaseDelayedDamage", delta.Operation);
        Assert.False(delta.IsPlayerVisible());
        Assert.Equal(messageCount, session.Engine.State.Messages.Count);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void DelayIncomingRejectsNonActorTargets()
    {
        var session = GameSession.CreateImperialEncounter();
        var brazier = session.Engine.EntityById("brazier_1")!;

        var result = session.Engine.ApplyConsequence(WorldConsequence.DelayIncomingDamage(
            "test",
            brazier.Id.Value,
            2,
            operation: "testDelayPropDamage"));

        Assert.False(result.Applied);
        Assert.False(brazier.TryGet<DelayedDamageComponent>(out _));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.DelayIncomingDamage)
            && delta.Summary.Contains("target is not an actor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaleDelayedDamageOnNonActorCleansUpAtTurnPump()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var brazier = session.Engine.EntityById("brazier_1")!;
        brazier.Set(new DelayedDamageComponent(7, session.Engine.State.Turn + 1));

        var deltas = session.Engine.AdvanceTurn();

        Assert.False(brazier.TryGet<DelayedDamageComponent>(out _));
        Assert.Contains(deltas, delta =>
            delta.Operation == "releaseDelayedDamage"
            && delta.Target == brazier.Id.Value
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ReleaseDelayedDamage)
            && Equals(delta.Details["skipped"], true)
            && Equals(delta.Details["reason"], "non_actor_target")
            && Equals(delta.Details["buffered"], 7));
        Assert.DoesNotContain(deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void OrdinaryAttacksHonorResistance()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(new ResistanceComponent(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["physical"] = 95 },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)));
        var hpBefore = soldier.Get<ActorComponent>().HitPoints;

        var deltas = session.Engine.AttackEntity(player, soldier);

        Assert.Equal(hpBefore - 1, soldier.Get<ActorComponent>().HitPoints);
        Assert.Contains(deltas, delta =>
            delta.Operation == "attack"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Damage)
            && Equals(delta.Details["attacker"], player.Id.Value));
    }

    [Fact]
    public void OrdinaryAttacksHonorDelayedDamageBuffer()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(new DelayedDamageComponent(0, session.Engine.State.Turn + 2));
        var hpBefore = soldier.Get<ActorComponent>().HitPoints;

        var deltas = session.Engine.AttackEntity(player, soldier);

        Assert.Equal(hpBefore, soldier.Get<ActorComponent>().HitPoints);
        Assert.True(soldier.Get<DelayedDamageComponent>().Buffered > 0);
        Assert.Contains(deltas, delta =>
            delta.Operation == "delayIncoming"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Damage));
    }

    [Fact]
    public void RejectedAttackDamageDoesNotFirePersistentCombatHooks()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var brazier = session.Engine.EntityById("brazier_1")!;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_strike",
            "message",
            new Dictionary<string, object?>
            {
                ["text"] = "The blade should not sing for a failed strike.",
            },
            uses: 1,
            playerVisible: true,
            sourceEntityId: player.Id.Value,
            operation: "testCreateStrikeEffect"));

        var deltas = session.Engine.AttackEntity(player, brazier);

        Assert.True(created.Applied);
        Assert.Single(session.Engine.State.PersistentEffects.Records);
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Damage)
            && delta.Summary.Contains("not a living actor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(deltas, delta => delta.Operation == "persistentMessage");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "updatePersistentEffect");
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("blade should not sing", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task EditMemoryRemovingCasterCalmsHostileNpc()
    {
        var addResolution = new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "A false memory settles in.",
            Effects: new[]
            {
                new SpellEffect(
                    "editMemory",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["op"] = "add",
                        ["subject"] = "the caster",
                        ["text"] = "He never saw the caster here.",
                        ["strength"] = 4,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(addResolution)));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;

        var addResult = await session.ExecuteAsync(new CastCommand("cast forget the caster"));
        Assert.Contains(soldier.Get<MemoryComponent>().Records, record =>
            record.Text.Contains("never saw the caster", StringComparison.OrdinalIgnoreCase)
            && record.Source == "wild_magic");
        Assert.Contains(soldier.Get<MemoryComponent>().Records, record =>
            record.Provenance.StartsWith("deed:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Memories.Records, record =>
            record.SubjectId == "soldier_1"
            && record.Text.Contains("never saw the caster", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(addResult.Deltas, delta =>
            delta.Operation == "editMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.EditMemory));

        var removeResolution = new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "The memory is torn out.",
            Effects: new[]
            {
                new SpellEffect(
                    "editMemory",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["op"] = "remove",
                        ["subject"] = "the caster",
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var removeSession = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(removeResolution)));
        DisableImperialAi(removeSession);
        var removeSoldier = removeSession.Engine.EntityById("soldier_1")!;
        removeSoldier.Set(new MemoryComponent(new[]
        {
            new EntityMemoryRecord("editMemory_1", "He saw the caster clearly.", "wild_magic", "planted by wild magic", 4, Shareable: true),
        }));

        var result = await removeSession.ExecuteAsync(new CastCommand("cast erase the caster from his mind"));

        Assert.True(result.Success);
        Assert.DoesNotContain(removeSoldier.Get<MemoryComponent>().Records, record =>
            record.Text.Contains("caster", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(removeSoldier.Get<MemoryComponent>().Records, record =>
            record.Provenance.StartsWith("deed:", StringComparison.OrdinalIgnoreCase));
        var playerSoulId = removeSession.Engine.State.ControlledEntity.Get<SoulComponent>().SoulId;
        var soldierSoulId = removeSoldier.Get<SoulComponent>().SoulId;
        Assert.True(removeSession.Engine.State.Bonds.TryGet(soldierSoulId, playerSoulId, out var bond));
        Assert.True(bond.Loyalty >= 5);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "memoryBondFloor"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.EditMemory)
            && Convert.ToInt32(delta.Details["loyalty"]) >= 5);
        Assert.False(removeSession.Engine.IsHostile(removeSoldier, removeSession.Engine.State.ControlledEntity));
    }

    [Fact]
    public void EditMemoryRemoveRollsBackWhenBondFloorChildRejects()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        player.Set(new SoulComponent(""));
        soldier.Set(new MemoryComponent(new[]
        {
            new EntityMemoryRecord("memory_1", "He saw the caster clearly.", "wild_magic", "planted by wild magic", 4, Shareable: true),
        }));

        var result = session.Engine.ApplyConsequence(WorldConsequence.EditMemory(
            "test",
            soldier.Id.Value,
            "remove",
            subject: "caster",
            aboutCaster: true,
            sourceEntityId: player.Id.Value,
            operation: "testRemoveCasterMemory"));

        var restoredSoldier = session.Engine.EntityById("soldier_1")!;
        Assert.False(result.Applied);
        Assert.Contains(restoredSoldier.Get<MemoryComponent>().Records, record =>
            record.Text.Contains("caster", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "testRemoveCasterMemory");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "editMemorySkipped"
            && Equals(delta.Target, soldier.Id.Value)
            && Equals(delta.Details["failure"], "Bond consequence did not include a target soul id.")
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        var soldierSoulId = restoredSoldier.Get<SoulComponent>().SoulId;
        Assert.False(session.Engine.State.Bonds.TryGet(soldierSoulId, "", out _));
    }

    [Fact]
    public async Task PersistentEffectOnHitPunishesAttacker()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "A ward of thorns settles over you.",
            Effects: new[]
            {
                new SpellEffect(
                    "createPersistentEffect",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "self",
                        ["hook"] = "on_hit",
                        ["effectType"] = "damage",
                        ["amount"] = 3,
                        ["damageType"] = "thorns",
                        ["uses"] = 1,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;

        await session.ExecuteAsync(new CastCommand("cast a ward of thorns"));
        var soldierHpBefore = soldier.Get<ActorComponent>().HitPoints;
        var attack = session.Engine.AttackEntity(soldier, player);

        Assert.True(soldier.Get<ActorComponent>().HitPoints < soldierHpBefore);
        Assert.Contains(attack, delta =>
            delta.Operation == "persistentDamage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Damage)
            && Equals(delta.Details["effectType"], "damage"));
        Assert.Contains(attack, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdatePersistentEffect)
            && Equals(delta.Details["action"], "consume")
            && Equals(delta.Details["remainingUses"], 0));
        Assert.DoesNotContain(attack, delta => delta.Operation == "persistentEffectSkipped");
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
    }

    [Fact]
    public async Task PersistentEffectUsesDecrementThroughSharedLifecycleConsequence()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "A small thorn ward settles over you.",
            Effects: new[]
            {
                new SpellEffect(
                    "createPersistentEffect",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "self",
                        ["hook"] = "on_hit",
                        ["effectType"] = "damage",
                        ["amount"] = 1,
                        ["damageType"] = "thorns",
                        ["uses"] = 2,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;

        await session.ExecuteAsync(new CastCommand("cast a two-use ward of thorns"));
        var attack = session.Engine.AttackEntity(soldier, player);

        var effect = Assert.Single(session.Engine.State.PersistentEffects.Records);
        Assert.Equal(1, effect.RemainingUses);
        Assert.Contains(attack, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdatePersistentEffect)
            && Equals(delta.Details["action"], "consume")
            && Equals(delta.Details["previousRemainingUses"], 2)
            && Equals(delta.Details["remainingUses"], 1));
    }

    [Fact]
    public void PersistentEffectDirectHookCollectsConsequenceDeltasAndConsumes()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "message",
            new Dictionary<string, object?>
            {
                ["text"] = "The ward rings once and spends itself.",
            },
            uses: 1,
            playerVisible: false,
            sourceEntityId: player.Id.Value,
            operation: "testCreatePersistentEffect"));

        var deltas = session.Engine.AttackEntity(soldier, player);

        Assert.True(created.Applied);
        Assert.Contains(deltas, delta =>
            delta.Operation == "persistentMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["effectType"], "message")
            && delta.Summary.Contains("ward rings", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deltas, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdatePersistentEffect)
            && Equals(delta.Details["action"], "consume")
            && Equals(delta.Details["remainingUses"], 0));
        Assert.DoesNotContain(deltas, delta => delta.Operation == "persistentEffectSkipped");
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
    }

    [Fact]
    public void UpdatePersistentEffectAcceptsExpireLikeTheOtherUpdateHandlersDo()
    {
        // "expire" is a terminal action shared by every Update* consequence handler
        // (scheduled events, triggers, persistent effects, behavior tags, tile flows); it must
        // not be silently rejected on persistent effects while working everywhere else.
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "message",
            new Dictionary<string, object?> { ["text"] = "A ward that never got to ring." },
            uses: 3,
            playerVisible: false,
            sourceEntityId: player.Id.Value,
            operation: "testCreatePersistentEffect"));
        Assert.True(created.Applied, created.Error);

        var result = session.Engine.ApplyConsequence(WorldConsequence.UpdatePersistentEffect(
            "test",
            created.TargetId!,
            "expire"));

        Assert.True(result.Applied, result.Error);
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["action"], "expire")
            && Equals(delta.Details["remainingUses"], 0)
            && delta.Summary.Contains("expired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdateBehaviorAcceptsCompleteLikeTheOtherUpdateHandlersDo()
    {
        // "complete" is a terminal action shared by every Update* consequence handler; it must
        // not be silently rejected on behavior tags while working on persistent effects.
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var set = session.Engine.ApplyConsequence(WorldConsequence.SetBehavior(
            "test",
            soldier.Id.Value,
            "dance",
            duration: 5));
        Assert.True(set.Applied, set.Error);

        var result = session.Engine.ApplyConsequence(WorldConsequence.UpdateBehavior(
            "test",
            soldier.Id.Value,
            "dance",
            "complete"));

        Assert.True(result.Applied, result.Error);
        Assert.False(soldier.Has<BehaviorTagsComponent>());
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "updateBehavior"
            && Equals(delta.Details["action"], "complete"));
    }

    [Fact]
    public void PersistentEffectCanDeliverGenericTypedConsequence()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "consequence",
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "addTags",
                ["target"] = "other",
                ["operation"] = "persistentAddTags",
                ["tags"] = new[] { "persistent_mark" },
            },
            uses: 1,
            playerVisible: false,
            sourceEntityId: player.Id.Value,
            operation: "testCreateGenericPersistentEffect"));

        var deltas = session.Engine.AttackEntity(soldier, player);

        Assert.True(created.Applied);
        Assert.Contains(soldier.Get<TagsComponent>().Tags, tag => tag == "persistent_mark");
        Assert.Contains(deltas, delta =>
            delta.Operation == "persistentAddTags"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["persistentEffectId"], created.TargetId)
            && Equals(delta.Details["hook"], "on_hit"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdatePersistentEffect)
            && Equals(delta.Details["action"], "consume")
            && Equals(delta.Details["remainingUses"], 0));
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
    }

    [Fact]
    public void GenericPersistentEffectConsequenceMergesTopLevelFieldsIntoNestedPayload()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "consequence",
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "apply_status",
                ["target"] = "other",
                ["status"] = "persistent-blue",
                ["duration"] = 2,
                ["displayName"] = "persistent blue",
                ["consequencePayload"] = new Dictionary<string, object?>
                {
                    ["operation"] = "persistentTopLevelStatus",
                },
            },
            uses: 1,
            playerVisible: false,
            sourceEntityId: player.Id.Value,
            operation: "testCreateMergedGenericPersistentEffect"));

        var deltas = session.Engine.AttackEntity(soldier, player);

        Assert.True(created.Applied);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status =>
            status.Id.Equals("persistent_blue", StringComparison.OrdinalIgnoreCase)
            && status.DisplayName.Equals("persistent blue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deltas, delta =>
            delta.Operation == "persistentTopLevelStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["persistentEffectId"], created.TargetId)
            && Equals(delta.Details["status"], "persistent_blue")
            && Equals(delta.Details["duration"], 2));
        Assert.Contains(deltas, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["action"], "consume"));
        Assert.DoesNotContain(deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task SpellCreatePersistentEffectCanArmGenericTypedConsequence()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(AcceptedSpell(
            "A later bite will mark the attacker.",
            new SpellEffect(
                "createPersistentEffect",
                new Dictionary<string, object?>
                {
                    ["target"] = "self",
                    ["hook"] = "on_hit",
                    ["uses"] = 1,
                    ["effect"] = new Dictionary<string, object?>
                    {
                        ["type"] = "consequence",
                        ["consequenceType"] = "addTags",
                        ["target"] = "other",
                        ["tags"] = new[] { "spell_persistent_mark" },
                    },
                })))));
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;

        var cast = await session.ExecuteAsync(new CastCommand("mark whoever strikes me"));
        var deltas = session.Engine.AttackEntity(soldier, player);

        Assert.True(cast.Success);
        Assert.Contains(soldier.Get<TagsComponent>().Tags, tag => tag == "spell_persistent_mark");
        Assert.Contains(deltas, delta =>
            delta.Operation == "addTag"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["persistentEffectType"], "consequence"));
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
    }

    [Fact]
    public void RejectedGenericPersistentEffectRollsBackAndRemovesBadRecord()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "consequence",
            new Dictionary<string, object?>
            {
                ["consequenceType"] = WorldConsequenceTypes.AddTags,
                ["targetEntityId"] = "missing_fixture",
                ["operation"] = "persistentMissingAddTags",
                ["tags"] = new[] { "impossible" },
            },
            uses: 1,
            playerVisible: false,
            sourceEntityId: player.Id.Value,
            operation: "testCreateBrokenGenericPersistentEffect"));

        var deltas = session.Engine.AttackEntity(soldier, player);
        var secondDeltas = session.Engine.AttackEntity(soldier, player);

        Assert.True(created.Applied);
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
        Assert.DoesNotContain(deltas, delta => delta.Operation == "persistentMissingAddTags");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags));
        Assert.Contains(deltas, delta =>
            delta.Operation == "persistentEffectSkipped"
            && Equals(delta.Target, created.TargetId)
            && Equals(delta.Details["effectType"], "consequence")
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(deltas, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["effectId"], created.TargetId)
            && Equals(delta.Details["action"], "remove"));
        Assert.DoesNotContain(secondDeltas, delta =>
            delta.Operation == "persistentEffectSkipped"
            && Equals(delta.Target, created.TargetId));
    }

    [Fact]
    public void MalformedGenericPersistentEffectSkipsAsHiddenAuditAndRemovesBadRecord()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "consequence",
            new Dictionary<string, object?>
            {
                ["targetEntityId"] = soldier.Id.Value,
                ["operation"] = "persistentMissingConsequenceType",
                ["tags"] = new[] { "should_not_land" },
            },
            uses: 1,
            playerVisible: true,
            sourceEntityId: player.Id.Value,
            operation: "testCreateMalformedGenericPersistentEffect"));

        var deltas = session.Engine.AttackEntity(soldier, player);
        var secondDeltas = session.Engine.AttackEntity(soldier, player);

        Assert.True(created.Applied);
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
        Assert.DoesNotContain(soldier.Get<TagsComponent>().Tags, tag =>
            tag.Equals("should_not_land", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(deltas, delta => delta.Operation == "persistentMissingConsequenceType");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Target, created.TargetId)
            && Equals(delta.Details["consequenceType"], "persistent_effect_consequence")
            && ((string)delta.Details["error"]!).Contains("consequenceType", StringComparison.OrdinalIgnoreCase)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "persistentEffectSkipped"
            && Equals(delta.Target, created.TargetId)
            && Equals(delta.Details["effectType"], "consequence")
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["effectId"], created.TargetId)
            && Equals(delta.Details["action"], "remove"));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("Persistent effect", StringComparison.OrdinalIgnoreCase)
            || message.Contains("consequenceType", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secondDeltas, delta =>
            delta.Operation == "persistentEffectSkipped"
            && Equals(delta.Target, created.TargetId));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void UnsupportedPersistentEffectSkipsAsHiddenAuditAndRemovesBadRecord()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "ring_moon",
            new Dictionary<string, object?>(),
            uses: 1,
            playerVisible: true,
            sourceEntityId: player.Id.Value,
            operation: "testCreateUnsupportedPersistentEffect"));

        var deltas = session.Engine.AttackEntity(soldier, player);
        var secondDeltas = session.Engine.AttackEntity(soldier, player);

        Assert.True(created.Applied);
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Target, created.TargetId)
            && Equals(delta.Details["consequenceType"], "persistent_effect_consequence")
            && Equals(delta.Details["effectType"], "ring_moon")
            && ((string)delta.Details["error"]!).Contains("Unsupported persistent effect type", StringComparison.OrdinalIgnoreCase)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "persistentEffectSkipped"
            && Equals(delta.Target, created.TargetId)
            && Equals(delta.Details["effectType"], "ring_moon")
            && Equals(delta.Details["rejectedCount"], 1)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "updatePersistentEffect"
            && Equals(delta.Details["effectId"], created.TargetId)
            && Equals(delta.Details["action"], "remove"));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("Persistent effect", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ring_moon", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secondDeltas, delta =>
            delta.Operation == "persistentEffectSkipped"
            && Equals(delta.Target, created.TargetId));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task PersistentEffectOnStrikeAppliesStatusToVictim()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "Your blade drinks venom.",
            Effects: new[]
            {
                new SpellEffect(
                    "createPersistentEffect",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "self",
                        ["hook"] = "on_strike",
                        ["effectType"] = "addStatus",
                        ["status"] = "poisoned",
                        ["duration"] = 3,
                        ["uses"] = 1,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;

        await session.ExecuteAsync(new CastCommand("cast venom blade"));
        session.Engine.AttackEntity(player, soldier);

        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "poisoned");
    }

    [Fact]
    public async Task PersistentEffectRejectsUnsupportedEmbeddedEffect()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "A strange impossible hook tries to settle.",
            Effects: new[]
            {
                new SpellEffect(
                    "createPersistentEffect",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "self",
                        ["hook"] = "on_hit",
                        ["effectType"] = "teleport",
                        ["uses"] = 1,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("cast a teleporting thorn ward"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.False(result.TechnicalFailure);
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
    }

    [Fact]
    public async Task SympatheticLinkRejectsWhenNoSecondActorIsBound()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "The link curls back on itself.",
            Effects: new[]
            {
                new SpellEffect(
                    "createPersistentEffect",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["hook"] = "on_hit",
                        ["kind"] = "sympathetic_link",
                        ["linkTarget"] = "soldier_1",
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("cast a self-only sympathetic link"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Empty(session.Engine.State.PersistentEffects.Records);
    }

    [Fact]
    public async Task SympatheticLinkMirrorsRealDamageOntoLinkedPartner()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "Their wounds become one.",
            Effects: new[]
            {
                new SpellEffect(
                    "createPersistentEffect",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["hook"] = "on_hit",
                        ["kind"] = "sympathetic_link",
                        ["linkTarget"] = "soldier_2",
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var soldier1 = session.Engine.EntityById("soldier_1")!;
        var soldier2 = session.Engine.EntityById("soldier_2")!;
        var soldier2HpBefore = soldier2.Get<ActorComponent>().HitPoints;

        await session.ExecuteAsync(new CastCommand("cast a sympathetic link"));
        var attackDeltas = session.Engine.AttackEntity(player, soldier1);
        var dealt = attackDeltas.First(delta => delta.Operation == "attack").Details["amount"];

        Assert.Equal(soldier2HpBefore - Convert.ToInt32(dealt), soldier2.Get<ActorComponent>().HitPoints);
    }

    [Fact]
    public async Task SetBehaviorCowardFleesInsteadOfClosing()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "Terror seizes the soldier.",
            Effects: new[]
            {
                new SpellEffect("setBehavior", new Dictionary<string, object?> { ["target"] = "soldier_1", ["tag"] = "coward" }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        session.Engine.EntityById("soldier_2")!.Set(new ControllerComponent(ControllerKind.None));
        var soldier = session.Engine.EntityById("soldier_1")!;
        var playerPosition = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var beforePosition = soldier.Get<PositionComponent>().Position;
        var distanceBefore = Math.Abs(beforePosition.X - playerPosition.X) + Math.Abs(beforePosition.Y - playerPosition.Y);

        var result = await session.ExecuteAsync(new CastCommand("cast fear into the soldier"));

        var afterPosition = soldier.Get<PositionComponent>().Position;
        var distanceAfter = Math.Abs(afterPosition.X - playerPosition.X) + Math.Abs(afterPosition.Y - playerPosition.Y);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "attack");
        Assert.True(distanceAfter >= distanceBefore);
    }

    [Fact]
    public async Task SetBehaviorDanceSkipsActorsTurn()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "The soldier cannot help but dance.",
            Effects: new[]
            {
                new SpellEffect("setBehavior", new Dictionary<string, object?> { ["target"] = "soldier_1", ["tag"] = "dance" }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        session.Engine.EntityById("soldier_2")!.Set(new ControllerComponent(ControllerKind.None));
        var soldier = session.Engine.EntityById("soldier_1")!;
        var positionBefore = soldier.Get<PositionComponent>().Position;

        var result = await session.ExecuteAsync(new CastCommand("cast a compulsion to dance"));

        Assert.True(result.Success);
        Assert.Equal(positionBefore, soldier.Get<PositionComponent>().Position);
        Assert.DoesNotContain(
            result.Deltas,
            delta => delta.Target == "soldier_1" && (delta.Operation == "aiMove" || delta.Operation == "attack"));
    }

    [Fact]
    public void ExpiredBehaviorTagsUseSharedLifecycleConsequence()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        var set = session.Engine.ApplyConsequence(WorldConsequence.SetBehavior(
            "test",
            soldier.Id.Value,
            "coward",
            duration: 1));

        var deltas = session.Engine.AdvanceTurn();

        Assert.True(set.Applied);
        Assert.False(soldier.Has<BehaviorTagsComponent>());
        Assert.Contains(deltas, delta =>
            delta.Operation == "expireBehavior"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBehavior)
            && Equals(delta.Details["tag"], "coward")
            && Equals(delta.Details["action"], "expire")
            && Equals(delta.Details["playerVisible"], false));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("coward", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EntityCloneDeepCopiesMutableBehaviorAndResistanceComponents()
    {
        var entity = new Entity(EntityId.Create("clone_subject"), "clone subject")
            .Set(new BehaviorTagsComponent(new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
            {
                ["coward"] = 3,
            }))
            .Set(new ResistanceComponent(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["fire"] = 20 },
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["cold"] = 30 }));

        var clone = entity.Clone();
        clone.Get<BehaviorTagsComponent>().Tags["coward"] = 99;
        clone.Get<ResistanceComponent>().Resistances["fire"] = 90;
        clone.Get<ResistanceComponent>().Weaknesses["cold"] = 80;

        Assert.Equal(3, entity.Get<BehaviorTagsComponent>().Tags["coward"]);
        Assert.Equal(20, entity.Get<ResistanceComponent>().Resistances["fire"]);
        Assert.Equal(30, entity.Get<ResistanceComponent>().Weaknesses["cold"]);
    }

    [Fact]
    public void RecordWorldTurnUsesSharedConsequenceLifecycle()
    {
        var session = GameSession.CreateImperialEncounter();
        var applied = session.Engine.ApplyConsequence(WorldConsequence.RecordWorldTurn(
            "test",
            "turn",
            "promise_stir",
            "promise_needle",
            "A promised needle twitches in the cloth.",
            new Dictionary<string, object?>
            {
                ["causeConsequenceType"] = WorldConsequenceTypes.UpdatePromise,
                ["promiseId"] = "promise_needle",
            }));

        var record = Assert.Single(session.Engine.State.WorldTurns.Records);
        var delta = Assert.Single(applied.Deltas);
        Assert.True(applied.Applied);
        Assert.Equal("promise_stir", record.Kind);
        Assert.Equal("promise_needle", record.SourceId);
        Assert.Equal(WorldConsequenceTypes.UpdatePromise, record.Details["causeConsequenceType"]);
        Assert.Equal("worldTurn", delta.Operation);
        Assert.Equal(record.Id, delta.Target);
        Assert.Equal(WorldConsequenceTypes.RecordWorldTurn, delta.Details["consequenceType"]);
        Assert.Equal(record.Id, delta.Details["worldTurnId"]);
        Assert.Equal(false, delta.Details["playerVisible"]);
    }

    [Fact]
    public async Task SetBehaviorMimicCopiesPlayersLastMovement()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "The soldier's body copies yours against its will.",
            Effects: new[]
            {
                new SpellEffect("setBehavior", new Dictionary<string, object?> { ["target"] = "soldier_1", ["tag"] = "mimic" }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        session.Engine.EntityById("soldier_2")!.Set(new ControllerComponent(ControllerKind.None));
        var soldier = session.Engine.EntityById("soldier_1")!;

        var move = await session.ExecuteAsync(new MoveCommand(Direction.East));
        Assert.Equal(new GridPoint(1, 0), session.Engine.State.LastControlledMoveDelta);
        Assert.Contains(move.Deltas, delta =>
            delta.Operation == "move"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity)
            && Equals(delta.Details["recordControlledMovement"], true)
            && Equals(delta.Details["dx"], 1)
            && Equals(delta.Details["dy"], 0));
        await session.ExecuteAsync(new CastCommand("cast mimic compulsion"));
        var positionBefore = soldier.Get<PositionComponent>().Position;

        await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(positionBefore.Translate(1, 0), soldier.Get<PositionComponent>().Position);
    }

    [Fact]
    public async Task CreateFlowMovesActorsStandingOnItEachTurn()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "The floor begins to flow beneath you.",
            Effects: new[]
            {
                new SpellEffect(
                    "createFlow",
                    new Dictionary<string, object?> { ["target"] = "self", ["radius"] = 0, ["dx"] = 1, ["dy"] = 0, ["duration"] = 5 }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var positionBefore = player.Get<PositionComponent>().Position;

        var result = await session.ExecuteAsync(new CastCommand("cast a flowing current"));

        Assert.Equal(positionBefore.Translate(1, 0), player.Get<PositionComponent>().Position);
        Assert.Contains(result.Messages, message => message.Contains("slide", StringComparison.OrdinalIgnoreCase));
        Assert.Single(session.Engine.State.TileFlows);
    }

    [Fact]
    public async Task AccelerateStatusAppliesRemainingBurningDamageAtOnce()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "The fire rushes to its end.",
            Effects: new[]
            {
                new SpellEffect(
                    "accelerateStatus",
                    new Dictionary<string, object?> { ["target"] = "soldier_1", ["status"] = "burning" }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(new StatusContainerComponent(new[] { new StatusInstance("burning", "burning", session.Engine.State.Turn + 3) }));

        var result = await session.ExecuteAsync(new CastCommand("cast rush the fire to its end"));

        Assert.True(result.Success);
        Assert.Equal(4, soldier.Get<ActorComponent>().HitPoints);
        Assert.DoesNotContain(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
        // The damage message must reach the persistent game log, not only the ActionResult --
        // anything reading GameState.Messages (replay, GUI panels reopened later) needs it there.
        Assert.Contains(session.Engine.State.Messages, message =>
            message.Contains("burning damage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CloseCurseRejectsDistantTargetBeforeMutation()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider()));
        DisableImperialAi(session);
        session.Engine.State.PromiseLedger.Add("curse", "Close curse: magic must stay near.", playerVisible: true, triggerHint: "close");
        var soldier = session.Engine.EntityById("soldier_1")!;
        var hpBefore = soldier.Get<ActorComponent>().HitPoints;

        var result = await session.ExecuteAsync(new CastCommand("strike the distant soldier"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(hpBefore, soldier.Get<ActorComponent>().HitPoints);
        Assert.Contains(result.Messages, message => message.Contains("Close curse", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task NarrowCurseRejectsAreaEffectsBeforeMutation()
    {
        var provider = new FixtureSpellProvider(AcceptedSpell(
            "The room tries to bloom outward.",
            new SpellEffect(
                "areaDamage",
                new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["radius"] = 2,
                    ["amount"] = 4,
                    ["affects"] = "enemies",
                })));
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        DisableImperialAi(session);
        session.Engine.State.PromiseLedger.Add("curse", "Narrow curse: magic must not spread.", playerVisible: true, triggerHint: "narrow");
        var soldier = session.Engine.EntityById("soldier_1")!;
        var hpBefore = soldier.Get<ActorComponent>().HitPoints;

        var result = await session.ExecuteAsync(new CastCommand("blast all enemies"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(hpBefore, soldier.Get<ActorComponent>().HitPoints);
        Assert.Contains(result.Messages, message => message.Contains("Narrow curse", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task DefeatedActorsStopBlockingMovementInvariants()
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(soldier.Get<ActorComponent>() with { HitPoints = 3 });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(8, 4)));

        var result = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.True(result.Success);
        Assert.Contains(result.Deltas, delta => delta.Operation == "attack");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "recordDeed"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordDeed)
            && Equals(delta.Details["kind"], "kill"));
        Assert.False(soldier.Get<ActorComponent>().Alive);
        Assert.False(soldier.Get<PhysicalComponent>().BlocksMovement);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task TemporaryTerrainExpiresAndRestoresMovement()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        var point = new GridPoint(4, 4);

        session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test_setup",
            point.X,
            point.Y,
            "ice_wall",
            duration: 2,
            emitMessage: false));
        await session.ExecuteAsync(new WaitCommand());

        Assert.True(session.Engine.State.Terrain.ContainsKey(point));
        Assert.Contains(point, session.Engine.State.BlockingTerrain);

        var secondWait = await session.ExecuteAsync(new WaitCommand());

        Assert.False(session.Engine.State.Terrain.ContainsKey(point));
        Assert.DoesNotContain(point, session.Engine.State.BlockingTerrain);
        Assert.Contains(secondWait.Deltas, delta =>
            delta.Operation == "expireTerrain"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateTerrain)
            && Equals(delta.Details["terrain"], "ice_wall")
            && Equals(delta.Details["action"], "expire"));
        Assert.Contains(secondWait.Messages, message => message.Contains("ice wall", StringComparison.OrdinalIgnoreCase)
            && message.Contains("fades", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Messages, message => message.Contains("ice wall", StringComparison.OrdinalIgnoreCase)
            && message.Contains("fades", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WaterTerrainExtinguishesBurningBeforeOngoingDamage()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(soldier.Get<ActorComponent>() with { HitPoints = 10 });
        var point = new GridPoint(4, 4);
        soldier.Set(new PositionComponent(point));
        ApplyStatus(session, soldier, "burning", duration: 3);
        session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test_setup",
            point.X,
            point.Y,
            "shallow_water",
            duration: 4,
            emitMessage: false));

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(10, soldier.Get<ActorComponent>().HitPoints);
        Assert.DoesNotContain(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
        Assert.Equal("steam_mist", session.Engine.State.Terrain[point]);
        Assert.Contains(result.Messages, message => message.Contains("mist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "terrainRemoveStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RemoveStatus)
            && Equals(delta.Details["reaction"], "water_extinguish"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "terrainSetTerrain"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SetTerrain)
            && Equals(delta.Details["reaction"], "water_extinguish"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "terrainMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["reaction"], "water_extinguish"));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task FireTerrainIgnitesActorsThroughTurnPump()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(soldier.Get<ActorComponent>() with { HitPoints = 10 });
        var point = new GridPoint(4, 4);
        soldier.Set(new PositionComponent(point));
        session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test_setup",
            point.X,
            point.Y,
            "wild_fire",
            duration: 4,
            emitMessage: false));

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(8, soldier.Get<ActorComponent>().HitPoints);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
        Assert.Contains(result.Messages, message => message.Contains("burning", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message => message.Contains("ongoing harm", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "terrainApplyStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["reaction"], "fire_ignite"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "statusTickDamage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AdjustActorResource));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task VineTerrainSnaresActorsThroughStatusRegistry()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        var point = new GridPoint(4, 4);
        soldier.Set(new PositionComponent(point));
        session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test_setup",
            point.X,
            point.Y,
            "vines",
            duration: 4,
            emitMessage: false));

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status =>
            status.Id == "rooted"
            && status.DisplayName == "vine-snared");
        Assert.Contains(result.Messages, message => message.Contains("vine-snared", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "terrainApplyStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["reaction"], "vine_snare"));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task ExamineQueuesBackgroundJobThatAppliesOnTurnBoundary()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(5, 4)));

        var examine = await session.ExecuteAsync(new ExamineCommand("brazier"));
        var jobsBefore = await session.ExecuteAsync(new JobsCommand());
        var debugBefore = session.Observation(debug: true);

        Assert.True(examine.Success);
        Assert.False(examine.ConsumedTurn);
        Assert.DoesNotContain(examine.Messages, message =>
            message.Contains("Background job queued", StringComparison.OrdinalIgnoreCase)
            && message.Contains("entity_detail", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("Background job queued", StringComparison.OrdinalIgnoreCase)
            && message.Contains("entity_detail", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(examine.Deltas, delta =>
            delta.Operation == "queueBackgroundJob"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.QueueBackgroundJob)
            && Equals(delta.Details["purpose"], "entity_detail")
            && Equals(delta.Details["targetEntityId"], "brazier_1")
            && Equals(delta.Details["queued"], true)
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(examine.Deltas, delta => delta.Operation == "examineFallbackMessage");
        Assert.Contains(jobsBefore.Messages, message => message.Contains("Queued", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(debugBefore.Debug!.BackgroundJobs!, job => job.Purpose == "entity_detail" && job.State == "Queued");

        var wait = await session.ExecuteAsync(new WaitCommand());
        var jobsAfter = await session.ExecuteAsync(new JobsCommand());
        var debugAfter = session.Observation(debug: true);

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundJobStarted"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBackgroundJob)
            && Equals(delta.Details["purpose"], "entity_detail")
            && Equals(delta.Details["targetId"], "brazier_1")
            && Equals(delta.Details["state"], "Running")
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundJobCompleted"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBackgroundJob)
            && Equals(delta.Details["purpose"], "entity_detail")
            && Equals(delta.Details["targetId"], "brazier_1")
            && Equals(delta.Details["state"], "Completed")
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundJobApplied"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBackgroundJob)
            && Equals(delta.Details["purpose"], "entity_detail")
            && Equals(delta.Details["targetId"], "brazier_1")
            && Equals(delta.Details["state"], "Applied")
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(wait.Messages, message =>
            message.StartsWith("Background job", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wait.Messages, message =>
            message.Contains("Background detail settles", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "backgroundMessage");
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "entity_detail"
            && record.AttachedTo == "brazier_1");
        Assert.Contains(jobsAfter.Messages, message => message.Contains("Applied", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(debugAfter.Debug!.BackgroundJobs!, job => job.Purpose == "entity_detail" && job.State == "Applied");
    }

    [Fact]
    public async Task BackgroundTextGeneratorSuppliesTextThroughApplyBoundary()
    {
        var session = GameSession.CreateImperialEncounter(
            backgroundTextGenerator: new FixtureBackgroundTextGenerator("The brazier sings a small provider-written note in soot."));
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(5, 4)));

        await session.ExecuteAsync(new ExamineCommand("brazier"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundTextGenerated"
            && Equals(delta.Details["provider"], "fixture-background")
            && Equals(delta.Details["usedFallback"], false)
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon)
            && Equals(delta.Details["attachedTo"], "brazier_1"));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "entity_detail"
            && record.AttachedTo == "brazier_1"
            && record.Text.Contains("provider-written", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BackgroundTextGeneratorFailureFallsBackWithoutBreakingApplyBoundary()
    {
        var session = GameSession.CreateImperialEncounter(
            backgroundTextGenerator: new FailingBackgroundTextGenerator());
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(5, 4)));

        await session.ExecuteAsync(new ExamineCommand("brazier"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundTextGenerated"
            && Equals(delta.Details["provider"], "failing-background")
            && Equals(delta.Details["technicalFailure"], true)
            && Equals(delta.Details["usedFallback"], true)
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "entity_detail"
            && record.AttachedTo == "brazier_1"
            && record.Text.Contains("brazier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicateBackgroundJobQueueSkipsAsHiddenAuditDelta()
    {
        var session = GameSession.CreateImperialEncounter();

        var first = session.Engine.ApplyConsequence(WorldConsequence.QueueBackgroundJob(
            "test",
            "brazier_1",
            "entity_detail",
            priority: 2,
            operation: "testQueueBackgroundJob"));
        var duplicate = session.Engine.ApplyConsequence(WorldConsequence.QueueBackgroundJob(
            "test",
            "brazier_1",
            "entity_detail",
            priority: 2,
            operation: "testQueueBackgroundJob"));

        Assert.True(first.Applied);
        Assert.False(duplicate.Applied);
        Assert.Single(session.Engine.State.BackgroundJobs.Jobs, job =>
            job.Purpose == "entity_detail"
            && job.TargetId == "brazier_1");
        Assert.Contains(duplicate.Deltas, delta =>
            delta.Operation == "testQueueBackgroundJob"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.QueueBackgroundJob)
            && Equals(delta.Details["queued"], false)
            && Equals(delta.Details["skipReason"], "duplicate_active_job")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public void CompletedBackgroundJobAppliesAtNextTurnPump()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var queued = session.Engine.ApplyConsequence(WorldConsequence.QueueBackgroundJob(
            "test",
            "brazier_1",
            "entity_detail",
            priority: 2,
            operation: "testQueueBackgroundJob"));
        var jobId = queued.TargetId!;
        var completed = session.Engine.ApplyConsequence(WorldConsequence.UpdateBackgroundJob(
            "test",
            jobId,
            BackgroundJobState.Completed.ToString(),
            completedTurn: session.Engine.State.Turn,
            resultText: "The brazier remembers rain written in brass.",
            operation: "testCompleteBackgroundJob"));

        var deltas = session.Engine.AdvanceTurn();

        Assert.True(queued.Applied);
        Assert.True(completed.Applied);
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "entity_detail"
            && record.AttachedTo == "brazier_1"
            && record.Text.Contains("rain written in brass", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deltas, delta =>
            delta.Operation == "backgroundCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon)
            && Equals(delta.Details["attachedTo"], "brazier_1"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "backgroundJobApplied"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBackgroundJob)
            && Equals(delta.Details["jobId"], jobId)
            && Equals(delta.Details["state"], BackgroundJobState.Applied.ToString()));
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn)
            && Equals(delta.Details["kind"], "background_detail_applied")
            && Equals(delta.Details["backgroundJobId"], jobId)
            && Equals(delta.Details["purpose"], "entity_detail")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(session.Engine.State.WorldTurns.Records, record =>
            record.Kind == "background_detail_applied"
            && record.SourceId == "brazier_1"
            && Equals(record.Details["backgroundJobId"], jobId)
            && Equals(record.Details["purpose"], "entity_detail"));
        Assert.Contains(session.Engine.State.BackgroundJobs.Jobs, job =>
            job.Id == jobId
            && job.State == BackgroundJobState.Applied
            && job.AppliedTurn == session.Engine.State.Turn);
    }

    [Fact]
    public void StaleRunningBackgroundJobFailsAndStopsBlockingDuplicateQueue()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var queued = session.Engine.ApplyConsequence(WorldConsequence.QueueBackgroundJob(
            "test",
            "brazier_1",
            "entity_detail",
            priority: 2,
            operation: "testQueueBackgroundJob"));
        var jobId = queued.TargetId!;
        var running = session.Engine.ApplyConsequence(WorldConsequence.UpdateBackgroundJob(
            "test",
            jobId,
            BackgroundJobState.Running.ToString(),
            startedTurn: session.Engine.State.Turn,
            operation: "testStartBackgroundJob"));

        var deltas = session.Engine.AdvanceTurn();
        var requeued = session.Engine.ApplyConsequence(WorldConsequence.QueueBackgroundJob(
            "test",
            "brazier_1",
            "entity_detail",
            priority: 2,
            operation: "testRequeueBackgroundJob"));

        Assert.True(queued.Applied);
        Assert.True(running.Applied);
        Assert.Contains(deltas, delta =>
            delta.Operation == "backgroundJobFailed"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBackgroundJob)
            && Equals(delta.Details["jobId"], jobId)
            && Equals(delta.Details["state"], BackgroundJobState.Failed.ToString())
            && Equals(delta.Details["error"], "stale_running_job"));
        Assert.Contains(session.Engine.State.BackgroundJobs.Jobs, job =>
            job.Id == jobId
            && job.State == BackgroundJobState.Failed
            && job.Error == "stale_running_job");
        Assert.True(requeued.Applied);
        Assert.NotEqual(jobId, requeued.TargetId);
        Assert.Contains(session.Engine.State.BackgroundJobs.Jobs, job =>
            job.Id == requeued.TargetId
            && job.State == BackgroundJobState.Queued);
    }

    [Fact]
    public async Task ObsoleteBackgroundDetailJobFailsWithoutDuplicatingCanon()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(5, 4)));

        var examine = await session.ExecuteAsync(new ExamineCommand("brazier"));
        var seeded = session.Engine.ApplyConsequence(WorldConsequence.AddCanon(
            "test",
            "entity_detail",
            "brazier_1",
            "A prior hand already wrote the brazier detail.",
            "A prior hand already wrote the brazier detail.",
            Array.Empty<string>(),
            operation: "testSeedCanon"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var debug = session.Observation(debug: true);

        Assert.True(examine.Success);
        Assert.True(seeded.Applied);
        Assert.Single(session.Engine.State.Canon.Records, record =>
            record.Kind == "entity_detail"
            && record.AttachedTo == "brazier_1");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundJobFailed"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBackgroundJob)
            && Equals(delta.Details["purpose"], "entity_detail")
            && Equals(delta.Details["targetId"], "brazier_1")
            && Equals(delta.Details["state"], "Failed")
            && Equals(delta.Details["error"], "canon_already_exists"));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "backgroundJobApplied");
        Assert.Contains(debug.Debug!.BackgroundJobs!, job =>
            job.Purpose == "entity_detail"
            && job.TargetId == "brazier_1"
            && job.State == "Failed"
            && job.Error == "canon_already_exists");
    }

    [Fact]
    public async Task ExamineClaimSourcePropRecordsClaimAndSuppressesDuplicatePromise()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(5, 4)));

        var first = await session.ExecuteAsync(new ExamineCommand("brazier"));
        var walkingStonePromises = session.Engine.State.PromiseLedger.Promises
            .Count(promise => promise.Text.Contains("walking stone", StringComparison.OrdinalIgnoreCase));
        var second = await session.ExecuteAsync(new ExamineCommand("brazier"));

        Assert.True(first.Success);
        Assert.False(first.ConsumedTurn);
        Assert.Contains(first.Deltas, delta =>
            delta.Operation == "inspectClaim"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordClaim)
            && Equals(delta.Details["subject"], "walking stone")
            && Equals(delta.Details["playerVisible"], true));
        Assert.Contains(first.Deltas, delta =>
            delta.Operation == "inspectClaimPromise"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreatePromise)
            && Equals(delta.Details["realizationKind"], "site"));
        Assert.Contains(first.Deltas, delta =>
            delta.Operation == "inspectClaimBound"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateClaim));
        Assert.Contains(first.Deltas, delta =>
            delta.Operation == "rumorMinted"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordRumor)
            && Equals(delta.Details["sourceKind"], "claim"));
        Assert.Contains(session.View().Rumors!, rumor =>
            rumor.SourceKind == "claim"
            && rumor.Text.Contains("walking stone", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, walkingStonePromises);

        Assert.True(second.Success);
        Assert.Contains(second.Deltas, delta =>
            delta.Operation == "claimDuplicate"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordClaim)
            && Equals(delta.Details["duplicate"], true)
            && Equals(delta.Details["subject"], "walking stone"));
        Assert.DoesNotContain(second.Deltas, delta => delta.Operation == "inspectClaimPromise");
        Assert.DoesNotContain(second.Deltas, delta => delta.Operation == "rumorMinted");
        Assert.Equal(walkingStonePromises, session.Engine.State.PromiseLedger.Promises
            .Count(promise => promise.Text.Contains("walking stone", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SpellJsonNormalizesCommonOperationAliases()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "Blue webbing binds the soldier.",
              "effects": [
                {
                  "name": "addStatus",
                  "target": { "id": "soldier_1" },
                  "status": "webbed"
                }
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal("addStatus", parsed.Effects.Single().Type);
        Assert.IsType<Dictionary<string, object?>>(parsed.Effects.Single().Fields["target"]);
    }

    [Fact]
    public void SpellJsonFlattensInternalRecordFieldsWrapper()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The room tastes of blue salt.",
              "effects": [
                {
                  "type": "message",
                  "fields": {
                    "text": "The room tastes of blue salt."
                  }
                }
              ],
              "costs": [
                {
                  "type": "mana",
                  "fields": {
                    "amount": 1
                  }
                }
              ],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        var effect = parsed.Effects.Single();
        Assert.Equal("message", effect.Type);
        Assert.Equal("The room tastes of blue salt.", effect.Fields["text"]);
        Assert.False(effect.Fields.ContainsKey("type"));
        Assert.False(effect.Fields.ContainsKey("fields"));
        var cost = parsed.Costs.Single();
        Assert.Equal("mana", cost.Type);
        Assert.Equal(1, cost.Fields["amount"]);
        Assert.False(cost.Fields.ContainsKey("type"));
        Assert.False(cost.Fields.ContainsKey("fields"));
    }

    [Fact]
    public void SpellJsonRepairsLiveProviderShorthandShapes()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The room answers in several dialects.",
              "effects": [
                {
                  "effectId": "status_webbed_target_id:soldier_1_type:webbed_source:magic_color:blue"
                },
                {
                  "effectId": "message_text:A thick blue web binds soldier_1."
                },
                {
                  "kind": "summon",
                  "target": { "x": 3, "y": 5 },
                  "entityName": "brass_moth"
                },
                {
                  "op": "createTiles",
                  "target/x/y": [[4, 5], [5, 6]],
                  "terrain": "ice"
                },
                {
                  "type": "addTrait",
                  "target": { "id": "notice_1" },
                  "trait": ["bound_to_caster_name"]
                }
              ],
              "costs": ["10 mana"],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal(new[] { "addStatus", "message", "summon", "createTiles", "addTrait" }, parsed.Effects.Select(effect => effect.Type).ToArray());
        Assert.Equal("soldier_1", parsed.Effects[0].Fields["target"]);
        Assert.Equal("webbed", parsed.Effects[0].Fields["status"]);
        Assert.Equal("A thick blue web binds soldier_1.", parsed.Effects[1].Fields["text"]);
        Assert.Equal("brass_moth", parsed.Effects[2].Fields["name"]);
        Assert.Equal("mana", parsed.Costs.Single().Type);
        Assert.Equal(10, parsed.Costs.Single().Fields["amount"]);
    }

    [Fact]
    public void SpellJsonPreservesGenericConsequencePayload()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The sleeve remembers the road.",
              "effects": [
                {
                  "type": "world_consequence",
                  "consequenceType": "add_tags",
                  "targetEntityId": "player",
                  "consequencePayload": {
                    "tags": ["road_blessed"],
                    "operation": "wildMagicConsequenceTest"
                  }
                }
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        var effect = parsed.Effects.Single();
        Assert.Equal("consequence", effect.Type);
        Assert.Equal(WorldConsequenceTypes.AddTags, effect.Fields["consequenceType"]);
        Assert.Equal("player", effect.Fields["targetEntityId"]);
        var payload = Assert.IsType<Dictionary<string, object?>>(effect.Fields["consequencePayload"]);
        Assert.Equal("wildMagicConsequenceTest", payload["operation"]);
        Assert.Contains("road_blessed", Assert.IsAssignableFrom<IEnumerable<object?>>(payload["tags"]!));
    }

    [Fact]
    public void SpellJsonTreatsConsequenceEffectTypeAsGenericOperation()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The sleeve keeps minutes.",
              "effects": [
                {
                  "effectType": "consequence",
                  "operation": "wildMagicConsequenceTest",
                  "consequence_type": "message",
                  "payload": {
                    "text": "The sleeve keeps minutes."
                  }
                }
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        var effect = parsed.Effects.Single();
        Assert.Equal("consequence", effect.Type);
        Assert.Equal("message", effect.Fields["consequence_type"]);
        Assert.Equal("wildMagicConsequenceTest", effect.Fields["operation"]);
    }

    [Fact]
    public void SpellJsonRepairsNestedDetailsAndNumericCosts()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The web learns a better name.",
              "effects": [
                {
                  "type": "addStatus",
                  "targetId": "soldier_1",
                  "details": {
                    "name": "entangled",
                    "description": "Stuck in a sticky blue web"
                  }
                }
              ],
              "costs": [10],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal("addStatus", parsed.Effects.Single().Type);
        Assert.Equal("soldier_1", parsed.Effects.Single().Fields["target"]);
        Assert.Equal("entangled", parsed.Effects.Single().Fields["status"]);
        Assert.Equal("mana", parsed.Costs.Single().Type);
        Assert.Equal(10, parsed.Costs.Single().Fields["amount"]);
    }

    [Fact]
    public void SpellJsonInfersCostTypesFromLiveStyleNamedCostObjects()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "moderate",
              "outcomeText": "The flame remembers its handle.",
              "effects": [
                {"type":"damage","targetId":"soldier_1","amount":8}
              ],
              "costs": [
                {"name":"charcoal wand","quantity":0.5,"unitValue":9},
                {"name":"mana","amount":4}
              ],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal(new[] { "item", "mana" }, parsed.Costs.Select(cost => cost.Type).ToArray());
        Assert.Equal("charcoal wand", parsed.Costs[0].Fields["item"]);
        Assert.Equal(4, parsed.Costs[1].Fields["amount"]);
    }

    [Fact]
    public void SpellJsonUnwrapsCommonResolutionEnvelope()
    {
        const string raw = """
            {
              "resolution": {
                "accepted": true,
                "severity": "minor",
                "outcomeText": "The flame catches.",
                "effects": [
                  {"type": "damage", "target": "nearest_enemy", "amount": 5}
                ],
                "costs": [],
                "rejectedReason": null
              }
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.True(parsed.Accepted);
        Assert.Equal("damage", parsed.Effects.Single().Type);
        Assert.Equal("nearest_enemy", parsed.Effects.Single().Fields["target"]);
    }

    [Fact]
    public void SpellJsonAliasesElementNameEffectTypeToDamage()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "Cold bites deep.",
              "effects": [
                {"type": "frost", "target": "nearest_enemy", "amount": 6}
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal("damage", parsed.Effects.Single().Type);
        Assert.Equal("frost", parsed.Effects.Single().Fields["damageType"]);
    }

    [Fact]
    public void SpellJsonRescuesEffectShapedEntryMisplacedInCosts()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The mark settles.",
              "effects": [],
              "costs": [
                {"type": "addTrait", "target": "nearest_enemy", "trait": "marked"}
              ],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Empty(parsed.Costs);
        Assert.Equal("addTrait", parsed.Effects.Single().Type);
        Assert.Equal("nearest_enemy", parsed.Effects.Single().Fields["target"]);
    }

    [Fact]
    public void SpellJsonStripsJunkOutcomeText()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "success",
              "effects": [
                {"type": "damage", "target": "nearest_enemy", "amount": 4}
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal(string.Empty, parsed.OutcomeText);
    }

    [Fact]
    public void SpellJsonRepairsNestedDataStatusShape()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The shadow pins him.",
              "effects": [
                {
                  "effectId": "status_1",
                  "target": { "id": "soldier_1" },
                  "type": "addStatus",
                  "data": {
                    "name": "shadow_pinned",
                    "description": "Unable to move or act."
                  }
                }
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal("addStatus", parsed.Effects.Single().Type);
        Assert.Equal("soldier_1", ((Dictionary<string, object?>)parsed.Effects.Single().Fields["target"]!)["id"]);
        Assert.Equal("shadow_pinned", parsed.Effects.Single().Fields["status"]);
    }

    [Fact]
    public void SpellJsonRepairsStatusNameAndTransformToStateShape()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "moderate",
              "outcomeText": "The bones remember dust.",
              "effects": [
                {
                  "type": "transformEntity",
                  "targetId": "soldier_1",
                  "toState": { "material": "bone_dust" }
                },
                {
                  "type": "addStatus",
                  "targetId": "soldier_1",
                  "statusName": "kneeling"
                }
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal("bone_dust", parsed.Effects[0].Fields["material"]);
        Assert.Equal("soldier_1", parsed.Effects[0].Fields["target"]);
        Assert.Equal("kneeling", parsed.Effects[1].Fields["status"]);
    }

    [Fact]
    public void SpellJsonInfersTraitOperationWhenLiveProviderOmitsType()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "A reed crown settles on Lio.",
              "effects": [
                {
                  "targetId": "prisoner_1",
                  "trait": "crown_of_river_reeds"
                }
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal("addTrait", parsed.Effects.Single().Type);
        Assert.Equal("prisoner_1", parsed.Effects.Single().Fields["target"]);
        Assert.Equal("crown_of_river_reeds", parsed.Effects.Single().Fields["trait"]);
    }

    [Fact]
    public void SpellJsonRepairsKeyedOperationEffectObjects()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The reeds agree to hide you.",
              "effects": [
                {
                  "addStatus": {
                    "targetId": "player",
                    "statusName": "river_concealed",
                    "duration": 4
                  }
                },
                {
                  "message": {
                    "text": "The river-color closes over your outline."
                  }
                }
              ],
              "costs": [],
              "rejectedReason": null
            }
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.Equal(new[] { "addStatus", "message" }, parsed.Effects.Select(effect => effect.Type).ToArray());
        Assert.Equal("player", parsed.Effects[0].Fields["target"]);
        Assert.Equal("river_concealed", parsed.Effects[0].Fields["status"]);
        Assert.Equal("The river-color closes over your outline.", parsed.Effects[1].Fields["text"]);
    }

    [Fact]
    public void SpellJsonParsesFirstObjectWhenModelAddsTrailingText()
    {
        const string raw = """
            {
              "accepted": true,
              "severity": "minor",
              "outcomeText": "The JSON ends before the chatter.",
              "effects": [
                {
                  "type": "message",
                  "text": "The braces {inside this string} are harmless."
                }
              ],
              "costs": [],
              "rejectedReason": null
            }
            commentary: the spell has been resolved.
            """;

        var parsed = SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

        Assert.True(parsed.Accepted);
        Assert.Equal("message", parsed.Effects.Single().Type);
        Assert.Equal("The braces {inside this string} are harmless.", parsed.Effects.Single().Fields["text"]);
    }

    [Fact]
    public void DefaultOperationRegistryLoadsContentCardsWhenAvailable()
    {
        var index = OperationRegistry.CreateDefault().ToIndex();

        var createTiles = index.Cards.Single(card => card.Name == "createTiles");
        var createPromise = index.Cards.Single(card => card.Name == "createPromise");
        var conjureFixture = index.Cards.Single(card => card.Name == "conjureFixture");
        var message = index.Cards.Single(card => card.Name == "message");
        var selected = CapabilityRegistry.CreateDefault().Select("raise a moon shrine beside me");
        var narrowed = OperationRegistry.CreateDefault().ToNarrowedIndex(selected.SelectMany(card => card.EffectTypes));
        var prophecyCard = CapabilityRegistry.CreateDefault()
            .Select("make an omen that a blue blade waits north")
            .Single(card => card.Id == "prophecy");

        Assert.Equal("Create or alter terrain.", createTiles.Summary);
        Assert.Contains("terrain", createTiles.Aliases);
        Assert.Contains("fixture", conjureFixture.PromptGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conjureFixture", narrowed.Names);
        Assert.Contains("consequence", prophecyCard.EffectTypes);
        Assert.Contains("create_promise", prophecyCard.PromptBlock);
        Assert.Contains("future-facing narrative hooks", createPromise.PromptGuidance);
        Assert.Contains("Do not use message to claim", message.PromptGuidance);
    }

    [Fact]
    public async Task ProtectedItemCostFizzleConsumesTurnWithoutMutation()
    {
        var provider = new ProtectedItemCostProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        var soldier = session.Engine.EntityById("soldier_1")!;
        var hpBefore = soldier.Get<Sorcerer.Core.Entities.ActorComponent>().HitPoints;

        var result = await session.ExecuteAsync(new CastCommand("shatter the moon pearl to strike"));

        Assert.False(result.Success);
        Assert.False(result.TechnicalFailure);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(1, result.TurnAfter);
        Assert.Equal(hpBefore, soldier.Get<Sorcerer.Core.Entities.ActorComponent>().HitPoints);
    }

    [Fact]
    public async Task ProtectCommandUpdatesReagentView()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = await session.ExecuteAsync(new UnprotectItemCommand("moon pearl"));

        Assert.True(result.Success);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "unprotect"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ModifyInventory)
            && Equals(delta.Details["op"], "unprotect")
            && Equals(delta.Details["protected"], false));
        Assert.Contains(session.View().Reagents!, reagent => reagent.Name == "moon pearl");
    }

    [Fact]
    public void MagicContextIncludesOnlyUnprotectedReagentsWithSpellBias()
    {
        var session = GameSession.CreateImperialEncounter();

        var context = session.Engine.MagicContext(new OperationIndex(
            Array.Empty<string>(),
            Array.Empty<OperationCardView>()));

        Assert.Contains(context.Reagents!, reagent =>
            reagent.Name == "grave salt"
            && reagent.SpellBias.Contains("ward", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(context.Reagents!, reagent => reagent.Name == "moon pearl");
    }

    [Fact]
    public async Task PickupUseEquipAndFocusUseSharedInventoryModel()
    {
        var session = GameSession.CreateImperialEncounter();
        await session.ExecuteAsync(new MoveCommand(Direction.East));

        var drop = await session.ExecuteAsync(new DropCommand("grave salt"));
        var pickup = await session.ExecuteAsync(new PickupCommand("red tincture"));
        var use = await session.ExecuteAsync(new UseItemCommand("red tincture"));
        var equip = await session.ExecuteAsync(new EquipCommand("charcoal wand"));
        var focus = await session.ExecuteAsync(new FocusCommand("charcoal wand"));

        Assert.True(drop.Success);
        Assert.Contains(drop.Deltas, delta =>
            delta.Operation == "drop"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.TransferItem)
            && Equals(delta.Details["mode"], "drop"));
        Assert.True(pickup.Success);
        Assert.Contains(pickup.Deltas, delta =>
            delta.Operation == "pickup"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.TransferItem)
            && Equals(delta.Details["mode"], "pickup"));
        Assert.True(use.Success);
        Assert.Contains(use.Deltas, delta =>
            delta.Operation == "useItemMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["item"], "red tincture")
            && Equals(delta.Details["useProfile"], "heal:6"));
        Assert.Contains(use.Messages, message =>
            message.Equals("You use red tincture.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(use.Deltas, delta =>
            delta.Operation == "useHeal"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Heal));
        Assert.Contains(use.Deltas, delta =>
            delta.Operation == "useItemSpent"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ModifyInventory));
        Assert.DoesNotContain(use.Deltas, delta => delta.Operation == "useItemSkipped");
        Assert.True(equip.Success);
        Assert.Contains(equip.Deltas, delta =>
            delta.Operation == "equip"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateEquipment)
            && Equals(delta.Details["mode"], "equip"));
        Assert.True(focus.Success);
        Assert.Contains(focus.Deltas, delta =>
            delta.Operation == "focus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateEquipment)
            && Equals(delta.Details["mode"], "focus"));
        Assert.Contains(session.View().Inventory!, item => item.Name == "charcoal wand" && item.Equipped && item.Focused);
        var unfocus = await session.ExecuteAsync(new UnfocusCommand("charcoal wand"));
        var unequip = await session.ExecuteAsync(new UnequipCommand("charcoal wand"));

        Assert.True(unfocus.Success);
        Assert.False(unfocus.ConsumedTurn);
        Assert.Contains(unfocus.Deltas, delta =>
            delta.Operation == "unfocus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateEquipment)
            && Equals(delta.Details["mode"], "unfocus"));
        Assert.True(unequip.Success);
        Assert.Contains(unequip.Deltas, delta =>
            delta.Operation == "unequip"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateEquipment)
            && Equals(delta.Details["mode"], "unequip"));
        Assert.Contains(session.View().Inventory!, item => item.Name == "charcoal wand" && !item.Equipped && !item.Focused);
        Assert.DoesNotContain(session.View().Inventory!, item => item.Name == "red tincture");
        Assert.Contains(session.View().Inventory!, item => item.Name == "grave salt" && item.Quantity == 1);
    }

    [Fact]
    public async Task ReadExamineAndOpenCellDoorCreateWorldConsequences()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 7)));
        var read = await session.ExecuteAsync(new ReadCommand("notice"));

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(7, 6)));
        var pickupKey = await session.ExecuteAsync(new PickupCommand("key"));

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var examine = await session.ExecuteAsync(new ExamineCommand("cell"));
        var open = await session.ExecuteAsync(new OpenCommand("cell"));
        var followers = await session.ExecuteAsync(new FollowersCommand());

        Assert.True(read.Success);
        Assert.Contains(read.Messages, message => message.Contains("marble authority", StringComparison.OrdinalIgnoreCase));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Contains("marble authority", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "readMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["readableId"], "notice_1")
            && Equals(delta.Details["title"], "Thaumic Containment Order 7-112"));
        Assert.DoesNotContain(read.Deltas, delta => delta.Operation == "readFallbackMessage");
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "readCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon));
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "readClaim"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordClaim)
            && Equals(delta.Details["subject"], "southern drainage culvert"));
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "readClaimPromise"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreatePromise)
            && Equals(delta.Details["realizationKind"], "escape_route"));
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "readClaimBound"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateClaim));
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "rumorMinted"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordRumor)
            && Equals(delta.Details["sourceKind"], "claim"));
        Assert.Contains(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Text.Contains("southern drainage culvert", StringComparison.OrdinalIgnoreCase)
            && promise.PlayerVisible
            && promise.RealizationKind == "escape_route");
        Assert.Contains(session.View().Rumors!, rumor =>
            rumor.SourceKind == "claim"
            && rumor.Text.Contains("southern drainage culvert", StringComparison.OrdinalIgnoreCase));
        Assert.True(pickupKey.Success);
        Assert.True(examine.Success);
        Assert.True(open.Success);
        Assert.Contains(open.Messages, message =>
            message.Equals("You open locked imperial cell door.", StringComparison.OrdinalIgnoreCase));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Equals("You open locked imperial cell door.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(open.Deltas, delta =>
            delta.Operation == "open"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OpenOrUnlock)
            && Equals(delta.Details["open"], true)
            && Equals(delta.Details["visibility"], WorldConsequenceVisibility.Message));
        Assert.Contains(open.Deltas, delta =>
            delta.Operation == "freeCaptiveFaction"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ChangeFaction)
            && Equals(delta.Details["membershipFactionId"], "hollowmere"));
        Assert.Contains(open.Deltas, delta =>
            delta.Operation == "freeCaptiveControl"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateControl)
            && Equals(delta.Details["aiPolicyId"], "follower"));
        Assert.Contains(open.Deltas, delta =>
            delta.Operation == "freeCaptiveDeed"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordDeed)
            && Equals(delta.Details["kind"], "freed_prisoner"));
        Assert.Contains(open.Deltas, delta =>
            delta.Operation == "freeCaptiveMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["faction"], "player"));
        Assert.DoesNotContain(open.Deltas, delta => delta.Operation == "openFallbackMessage");
        Assert.Contains(session.Engine.State.PromiseLedger.Promises, promise => promise.Status == "realized");
        Assert.Contains(followers.Messages, message => message.Contains("Lio of Hollowmere", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MagicPromiseCanBindToReadableAnchorAndRealizeOnRead()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new PromiseBindingSpellProvider()));
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 7)));

        var cast = await session.ExecuteAsync(new CastCommand("make the notice remember my name when read"));
        var notice = session.Engine.EntityById("notice_1")!;
        var promise = session.Engine.State.PromiseLedger.Promises.Single(item =>
            item.Text.Contains("posted notice", StringComparison.OrdinalIgnoreCase));

        Assert.True(cast.Success);
        Assert.Equal("bound", promise.Status);
        Assert.Equal("notice_1", promise.BoundTargetId);
        Assert.Equal("read", promise.TriggerHint);
        Assert.Contains(promise.Id, notice.Get<PromiseAnchorComponent>().PromiseIds);

        var read = await session.ExecuteAsync(new ReadCommand("notice"));
        var realized = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id);

        Assert.True(read.Success);
        Assert.Equal("realized", realized.Status);
        Assert.Equal("read:notice_1", realized.RealizedIn);
        Assert.Contains(read.Deltas, delta => delta.Operation == "realizePromise" && delta.Target == promise.Id);
        Assert.Contains(read.Deltas, delta => delta.Operation == "promiseMemory");
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "promiseAwakened"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "promiseMemoryMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Single(
            session.Engine.State.Messages,
            message => message.Contains("remembers something that was not there before", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Memories.Records, memory =>
            memory.SubjectId == "notice_1"
            && memory.Text.Contains("posted notice", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notice.Get<MemoryComponent>().Records, memory =>
            memory.Source == promise.Id
            && memory.Text.Contains("posted notice", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.View().Promises, card =>
            card.Id == promise.Id
            && card.BoundTargetId == "notice_1"
            && card.RealizedIn == "read:notice_1");
    }

    [Fact]
    public async Task PromiseIntentCannotBeSatisfiedByNarratedRouteReveal()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new NarratedRouteRevealSpellProvider()));

        var result = await session.ExecuteAsync(new CastCommand("promise that the posted notice will reveal a hidden route when read"));

        Assert.False(result.Success);
        Assert.True(result.TechnicalFailure);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(0, result.TurnAfter);
        Assert.DoesNotContain(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Text.Contains("hidden route", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("route lies beneath", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message =>
            message.Contains("promised future", StringComparison.OrdinalIgnoreCase)
            || message.Contains("route", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PromiseIntentClipsUnsafeSupplementalNarrationWhenPromiseExists()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new PromiseWithUnsafeNarrationSpellProvider()));

        var result = await session.ExecuteAsync(new CastCommand("promise that the posted notice will reveal a hidden route when read"));

        Assert.True(result.Success);
        Assert.False(result.TechnicalFailure);
        Assert.True(result.ConsumedTurn);
        Assert.Contains(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.BoundTargetId == "notice_1"
            && promise.TriggerHint == "read"
            && promise.Text.Contains("hidden route", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message => message.Contains("future event", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Messages, message => message.Contains("you read", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Messages, message => message.Contains("as you read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MagicRoutePromiseRealizesReadableAnchorIntoRoute()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new PromiseWithUnsafeNarrationSpellProvider()));
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));

        var cast = await session.ExecuteAsync(new CastCommand("promise that the posted notice will reveal a hidden route when read"));
        var notice = session.Engine.EntityById("notice_1")!;
        var promise = session.Engine.State.PromiseLedger.Promises.Single(item =>
            item.Text.Contains("hidden route", StringComparison.OrdinalIgnoreCase));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 7)));

        var read = await session.ExecuteAsync(new ReadCommand("notice"));
        var realized = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id);

        Assert.True(cast.Success);
        Assert.Equal("escape_route", promise.RealizationKind);
        Assert.Equal("notice_1", promise.BoundTargetId);
        Assert.Contains(promise.Id, notice.Get<PromiseAnchorComponent>().PromiseIds);
        Assert.True(read.Success);
        Assert.Equal("realized", realized.Status);
        Assert.Equal("read:notice_1", realized.RealizedIn);
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "promiseRoute"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreateRoute)
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "promiseRouteMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Has<FixtureComponent>()
            && entity.Get<FixtureComponent>().FixtureType == "escape_route"
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id));
        Assert.DoesNotContain(session.Engine.State.Entities.Values, entity =>
            entity.Name.Equals("player_soul", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadAnchoredQuestPromiseRealizesThroughCanonConsequence()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var notice = session.Engine.EntityById("notice_1")!;
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "The posted notice names a quest that was folded under the ink.",
            playerVisible: true,
            source: "test",
            salience: 3,
            subject: "folded notice quest",
            triggerHint: "read",
            realizationKind: "quest");
        session.Engine.State.PromiseLedger.Bind(
            promise.Id,
            session.Engine.State.RegionId,
            notice.Id.Value,
            triggerHint: "read",
            realizationKind: "quest");
        notice.Set(new PromiseAnchorComponent(promise.Id));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 7)));

        var read = await session.ExecuteAsync(new ReadCommand("notice"));

        Assert.True(read.Success);
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "promiseCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon)
            && Equals(delta.Details["promiseId"], promise.Id)
            && Equals(delta.Details["kind"], "quest"));
        Assert.Contains(read.Deltas, delta =>
            delta.Operation == "promiseCanonMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["promiseId"], promise.Id)
            && Equals(delta.Details["kind"], "quest"));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "quest"
            && record.Source == $"promise:{promise.Id}:read");
    }

    [Fact]
    public async Task AlertHostileBodyResistsPossessionAndConsumesTurn()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(8, 4)));

        var result = await session.ExecuteAsync(new PossessCommand("soldier_1"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("player", session.Engine.State.ControlledEntityId.Value);
        Assert.Contains(result.Messages, message => message.Contains("refuses", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessResisted"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["oldBody"], "player")
            && Equals(delta.Details["newBody"], "soldier_1"));
    }

    [Fact]
    public async Task IncapacitatedBodyCanBePossessedWithoutMovingInventory()
    {
        var session = GameSession.CreateImperialEncounter();
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        ApplyStatus(session, soldier, "webbed", duration: 3);

        var result = await session.ExecuteAsync(new PossessCommand("soldier_1"));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("soldier_1", session.Engine.State.ControlledEntityId.Value);
        Assert.Equal("player_soul", soldier.Get<SoulComponent>().SoulId);
        Assert.NotEqual("player_soul", playerBody.Get<SoulComponent>().SoulId);
        Assert.DoesNotContain(session.View().Inventory!, item => item.Name == "moon pearl");
        Assert.Contains(playerBody.Get<InventoryComponent>().Items, pair => pair.Key == "moon pearl");
        Assert.Equal("player", soldier.Get<ActorComponent>().Faction);
        Assert.Equal("empire", playerBody.Get<ActorComponent>().Faction);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessSoulSwap"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SwapSouls)
            && Equals(delta.Details["firstEntityId"], playerBody.Id.Value)
            && Equals(delta.Details["secondEntityId"], soldier.Id.Value)
            && Equals(delta.Details["firstSoulBefore"], "player_soul")
            && Equals(delta.Details["secondSoulBefore"], "soldier_1_soul")
            && Equals(delta.Details["firstSoulAfter"], "soldier_1_soul")
            && Equals(delta.Details["secondSoulAfter"], "player_soul"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessNewBodyControl"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateControl)
            && Equals(delta.Details["controllerKind"], ControllerKind.Player.ToString()));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessOldBodyControl"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateControl)
            && Equals(delta.Details["aiPolicyId"], "displaced_hostile_soul"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessNewBodyFaction"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ChangeFaction)
            && Equals(delta.Details["faction"], "player"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessOldBodyStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["oldBody"], playerBody.Id.Value));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessNewBodyStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["newBody"], soldier.Id.Value));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessControlledEntity"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SetControlledEntity)
            && Equals(delta.Details["previousControlledEntityId"], playerBody.Id.Value)
            && Equals(delta.Details["controlledEntityId"], soldier.Id.Value));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "recordDeed"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordDeed)
            && Equals(delta.Details["kind"], "body_swap"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possess"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["oldBody"], playerBody.Id.Value)
            && Equals(delta.Details["newBody"], soldier.Id.Value));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "possessionSkipped");
        Assert.DoesNotContain(result.Messages, message => message.Contains("controlled by", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Messages, message => message.Contains("now answers to", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task MagicPossessOperationUsesSameBodyControlRules()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new PossessSpellProvider()));
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        ApplyStatus(session, soldier, "webbed", duration: 3);

        var result = await session.ExecuteAsync(new CastCommand("step into the webbed soldier's body"));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Contains("possess", result.Magic!.EffectTypes);
        Assert.Equal("soldier_1", session.Engine.State.ControlledEntityId.Value);
        Assert.Equal("player_soul", soldier.Get<SoulComponent>().SoulId);
        Assert.Contains(playerBody.Get<InventoryComponent>().Items, pair => pair.Key == "moon pearl");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessSoulSwap"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SwapSouls)
            && Equals(delta.Details["firstEntityId"], playerBody.Id.Value)
            && Equals(delta.Details["secondEntityId"], soldier.Id.Value)
            && Equals(delta.Details["firstSoulAfter"], "soldier_1_soul")
            && Equals(delta.Details["secondSoulAfter"], "player_soul"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "possessControlledEntity"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SetControlledEntity)
            && Equals(delta.Details["previousControlledEntityId"], playerBody.Id.Value)
            && Equals(delta.Details["controlledEntityId"], soldier.Id.Value));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "possessionSkipped");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    private static StateDelta ApplyStatus(
        GameSession session,
        Entity target,
        string status,
        int duration,
        string displayName = "",
        string operation = "addStatus",
        string? messageOverride = null)
    {
        var applied = session.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
            "test_setup",
            target.Id.Value,
            status,
            duration,
            displayName,
            operation: operation,
            emitMessage: false,
            message: messageOverride));

        Assert.True(applied.Applied, applied.Error ?? $"Status {status} was not applied.");
        return Assert.Single(applied.Deltas);
    }

    private static SpellResolution AcceptedSpell(string outcome, params SpellEffect[] effects) =>
        new(
            Accepted: true,
            Severity: "minor",
            OutcomeText: outcome,
            Effects: effects,
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);

    private sealed class FixtureSpellProvider : ISpellProvider
    {
        private readonly SpellResolution? _resolution;

        public FixtureSpellProvider(SpellResolution? resolution = null)
        {
            _resolution = resolution;
        }

        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = _resolution ?? new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "A test spell snaps into place.",
                Effects: new[]
                {
                    new SpellEffect(
                        "damage",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "nearest_enemy",
                            ["amount"] = 3,
                            ["damageType"] = "arcane",
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class ThrowingSpellProvider : ISpellProvider
    {
        public string Name => "throwing";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ApplyResolved must not ask the provider to resolve.");
    }

    private sealed class CancellationAwareSpellProvider : ISpellProvider
    {
        private readonly TaskCompletionSource<SpellProviderResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "cancellable";

        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult(true);
                _result.TrySetCanceled(cancellationToken);
            });
            return _result.Task;
        }
    }

    private sealed class TechnicalFailureSpellProvider : ISpellProvider
    {
        public string Name => "technical-fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SpellProviderResult(
                Name,
                RawText: "",
                Resolution: null,
                TechnicalFailure: true,
                Error: "fixture failure"));
    }

    private sealed class ZeroManaCostSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "A test spell flowers without debt.",
                Effects: new[]
                {
                    new SpellEffect(
                        "message",
                        new Dictionary<string, object?>
                        {
                            ["text"] = "Blue blossoms open on polished armor.",
                        }),
                },
                Costs: new[]
                {
                    new SpellCost(
                        "mana",
                        new Dictionary<string, object?>
                        {
                            ["amount"] = 0,
                        }),
                },
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class NegativeManaCostSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "A test spell flowers with a mistaken debt.",
                Effects: new[]
                {
                    new SpellEffect(
                        "message",
                        new Dictionary<string, object?>
                        {
                            ["text"] = "Blue blossoms open on polished armor.",
                        }),
                },
                Costs: new[]
                {
                    new SpellCost(
                        "mana",
                        new Dictionary<string, object?>
                        {
                            ["amount"] = -5,
                        }),
                },
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class NoOpTransformItemSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "The tincture pretends to become itself.",
                Effects: new[]
                {
                    new SpellEffect(
                        "transformItem",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "loose_tincture_1",
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private static void DisableImperialAi(GameSession session)
    {
        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            session.Engine.EntityById(id)!.Set(new ControllerComponent(ControllerKind.None));
        }
    }

    private sealed class MalformedTargetSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "A malformed test spell tries to become damage.",
                Effects: new[]
                {
                    new SpellEffect(
                        "damage",
                        new Dictionary<string, object?>
                        {
                            ["target"] = new Dictionary<string, object?>
                            {
                                ["id"] = new Dictionary<string, object?>
                                {
                                    ["kind"] = "imperial",
                                    ["label"] = "containment soldier",
                                },
                            },
                            ["amount"] = 3,
                            ["damageType"] = "arcane",
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class ProtectedItemCostProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "major",
                OutcomeText: "The pearl tries to break itself into moonlit knives.",
                Effects: new[]
                {
                    new SpellEffect(
                        "damage",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "nearest_enemy",
                            ["amount"] = 8,
                        }),
                },
                Costs: new[]
                {
                    new SpellCost(
                        "item",
                        new Dictionary<string, object?>
                        {
                            ["item"] = "moon pearl",
                            ["quantity"] = 1,
                        }),
                },
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class TraitStatusSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "Blue webbing pins the soldier.",
                Effects: new[]
                {
                    new SpellEffect(
                        "addStatus",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "soldier_1",
                            ["status"] = new Dictionary<string, object?>
                            {
                                ["id"] = "webbed_blue_webbing",
                                ["description"] = "Bound by sticky blue webbing.",
                            },
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class PromiseBindingSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "The notice takes on a little debt of memory.",
                Effects: new[]
                {
                    new SpellEffect(
                        "createPromise",
                        new Dictionary<string, object?>
                        {
                            ["kind"] = "prophecy",
                            ["text"] = "The posted notice will remember my name when read.",
                            ["target"] = "notice_1",
                            ["trigger"] = "read",
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class NarratedRouteRevealSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "The notice's ink bleeds into a map of secret passages; reading it reveals the hidden route.",
                Effects: new[]
                {
                    new SpellEffect(
                        "addStatus",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "notice_1",
                            ["status"] = "revealed",
                        }),
                    new SpellEffect(
                        "message",
                        new Dictionary<string, object?>
                        {
                            ["text"] = "You read the posted containment notice. The route lies beneath the third archway.",
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class PromiseWithUnsafeNarrationSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "The notice's ink bleeds into a map of cracks as you read it, revealing the hidden route.",
                Effects: new[]
                {
                    new SpellEffect(
                        "createPromise",
                        new Dictionary<string, object?>
                        {
                            ["kind"] = "reveal_route",
                            ["text"] = "Reading this posted containment notice will reveal a hidden route when conditions are met.",
                            ["target"] = "notice_1",
                            ["triggerHint"] = "read",
                        }),
                    new SpellEffect(
                        "message",
                        new Dictionary<string, object?>
                        {
                            ["text"] = "You read the posted containment notice. The route lies beneath the third archway.",
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class PossessSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "major",
                OutcomeText: "A test soul crosses the room.",
                Effects: new[]
                {
                    new SpellEffect(
                        "possess",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "soldier_1",
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class FixtureBackgroundTextGenerator(string text) : IBackgroundTextGenerator
    {
        public string Name => "fixture-background";

        public BackgroundTextGenerationResult Generate(BackgroundTextRequest request) =>
            new(text, Provider: Name, Model: "fixture");
    }

    private sealed class FailingBackgroundTextGenerator : IBackgroundTextGenerator
    {
        public string Name => "failing-background";

        public BackgroundTextGenerationResult Generate(BackgroundTextRequest request) =>
            new(null, TechnicalFailure: true, Error: "fixture failure", Provider: Name, Model: "fixture");
    }
}
