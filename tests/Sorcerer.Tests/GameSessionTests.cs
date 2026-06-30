using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Magic;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

public sealed class GameSessionTests
{
    [Fact]
    public async Task MoveCommandConsumesTurn()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(0, result.TurnBefore);
        Assert.Equal(1, result.TurnAfter);
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
        Assert.Equal(1, result.TurnAfter);
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
        Assert.NotNull(session.Observation().PendingCast);

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
    public async Task HostileActorsMoveAfterConsumedTurn()
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;

        var result = await session.ExecuteAsync(new WaitCommand());

        var after = soldier.Get<PositionComponent>().Position;
        Assert.True(result.ConsumedTurn);
        Assert.NotEqual(before, after);
        Assert.Contains(result.Deltas, delta => delta.Operation == "aiMove");
    }

    [Fact]
    public async Task StatusRegistryTraitsSuppressAiActions()
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;
        session.Engine.ApplyStatus(soldier, "webbed", duration: 3);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.True(result.ConsumedTurn);
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
        session.Engine.ApplyStatus(session.Engine.State.ControlledEntity, "sticky_webbed", duration: 3);

        var result = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(before, session.Engine.State.ControlledEntity.Get<PositionComponent>().Position);
        Assert.Contains("binding", result.Messages.Single(), StringComparison.OrdinalIgnoreCase);
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

        session.Engine.SetTerrain(point, "ice_wall", duration: 2);
        await session.ExecuteAsync(new WaitCommand());

        Assert.True(session.Engine.State.Terrain.ContainsKey(point));
        Assert.Contains(point, session.Engine.State.BlockingTerrain);

        await session.ExecuteAsync(new WaitCommand());

        Assert.False(session.Engine.State.Terrain.ContainsKey(point));
        Assert.DoesNotContain(point, session.Engine.State.BlockingTerrain);
        Assert.Contains(session.Engine.State.Messages, message => message.Contains("ice wall", StringComparison.OrdinalIgnoreCase)
            && message.Contains("fades", StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains(jobsBefore.Messages, message => message.Contains("Queued", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(debugBefore.Debug!.BackgroundJobs!, job => job.Purpose == "entity_detail" && job.State == "Queued");

        await session.ExecuteAsync(new WaitCommand());
        var jobsAfter = await session.ExecuteAsync(new JobsCommand());
        var debugAfter = session.Observation(debug: true);

        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "entity_detail"
            && record.AttachedTo == "brazier_1");
        Assert.Contains(jobsAfter.Messages, message => message.Contains("Applied", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(debugAfter.Debug!.BackgroundJobs!, job => job.Purpose == "entity_detail" && job.State == "Applied");
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

        Assert.Equal("Create or alter terrain.", createTiles.Summary);
        Assert.Contains("terrain", createTiles.Aliases);
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
        Assert.Contains(session.View().Reagents!, reagent => reagent.Name == "moon pearl");
    }

    [Fact]
    public async Task PickupUseEquipAndFocusUseSharedInventoryModel()
    {
        var session = GameSession.CreateImperialEncounter();
        await session.ExecuteAsync(new MoveCommand(Direction.East));

        var pickup = await session.ExecuteAsync(new PickupCommand("red tincture"));
        var use = await session.ExecuteAsync(new UseItemCommand("red tincture"));
        var equip = await session.ExecuteAsync(new EquipCommand("charcoal wand"));
        var focus = await session.ExecuteAsync(new FocusCommand("charcoal wand"));

        Assert.True(pickup.Success);
        Assert.True(use.Success);
        Assert.True(equip.Success);
        Assert.True(focus.Success);
        Assert.Contains(session.View().Inventory!, item => item.Name == "charcoal wand" && item.Equipped && item.Focused);
        Assert.DoesNotContain(session.View().Inventory!, item => item.Name == "red tincture");
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
        Assert.Contains("marble authority", read.Messages.Single());
        Assert.True(pickupKey.Success);
        Assert.True(examine.Success);
        Assert.True(open.Success);
        Assert.Contains(open.Deltas, delta => delta.Operation == "freePrisoner");
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
    public async Task AlertHostileBodyResistsPossessionAndConsumesTurn()
    {
        var session = GameSession.CreateImperialEncounter();
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(8, 4)));

        var result = await session.ExecuteAsync(new PossessCommand("soldier_1"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("player", session.Engine.State.ControlledEntityId.Value);
        Assert.Contains(result.Messages, message => message.Contains("refuses", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IncapacitatedBodyCanBePossessedWithoutMovingInventory()
    {
        var session = GameSession.CreateImperialEncounter();
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        session.Engine.ApplyStatus(soldier, "webbed", duration: 3);

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
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task MagicPossessOperationUsesSameBodyControlRules()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new PossessSpellProvider()));
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        session.Engine.ApplyStatus(soldier, "webbed", duration: 3);

        var result = await session.ExecuteAsync(new CastCommand("step into the webbed soldier's body"));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Contains("possess", result.Magic!.EffectTypes);
        Assert.Equal("soldier_1", session.Engine.State.ControlledEntityId.Value);
        Assert.Equal("player_soul", soldier.Get<SoulComponent>().SoulId);
        Assert.Contains(playerBody.Get<InventoryComponent>().Items, pair => pair.Key == "moon pearl");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    private sealed class FixtureSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
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
}
