using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
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

    [Theory]
    [InlineData("shadow_pinned")]
    [InlineData("kneeling")]
    [InlineData("boneless_knees")]
    public async Task BindingStatusVariantsCanonicalizeToRootedAndSuppressAiActions(string status)
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;
        session.Engine.ApplyStatus(soldier, status, duration: 3);

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
        session.Engine.ApplyStatus(session.Engine.State.ControlledEntity, "sticky_webbed", duration: 3);

        var result = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(before, session.Engine.State.ControlledEntity.Get<PositionComponent>().Position);
        Assert.Contains("binding", result.Messages.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyStatusUsesSecondPersonForControlledEntity()
    {
        var session = GameSession.CreateImperialEncounter();

        var delta = session.Engine.ApplyStatus(
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

        var delta = session.Engine.ApplyStatus(
            session.Engine.State.ControlledEntity,
            "river_concealed",
            duration: 0);

        Assert.Equal(4, delta.Details["duration"]);
        Assert.Contains(session.Engine.State.ControlledEntity.Get<StatusContainerComponent>().Statuses, status =>
            status.Id == "concealed"
            && status.ExpiresTurn == 4);
    }

    [Fact]
    public async Task ConcealedControlledEntityAvoidsDistantHostileNotice()
    {
        var session = GameSession.CreateImperialEncounter();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var before = soldier.Get<PositionComponent>().Position;
        session.Engine.ApplyStatus(session.Engine.State.ControlledEntity, "river_concealed", duration: 4);

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
        session.Engine.ApplyStatus(session.Engine.State.ControlledEntity, "river_concealed", duration: 4);

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
        session.Engine.ApplyStatus(soldier, "burning", duration: 3);

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
        session.Engine.ApplyStatus(player, "mending", duration: 3);

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
        session.Engine.ApplyStatus(soldier, "burning", duration: 3);

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
        Assert.Contains(wait.Messages, message => message.Contains("delayed magic", StringComparison.OrdinalIgnoreCase));
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

        session.Engine.DamageEntity(player, 5, "physical");
        Assert.Equal(startingHp, player.Get<ActorComponent>().HitPoints);
        Assert.Equal(5, player.Get<DelayedDamageComponent>().Buffered);

        session.Engine.AdvanceTurn();

        Assert.False(player.TryGet<DelayedDamageComponent>(out _));
        Assert.True(player.Get<ActorComponent>().HitPoints < startingHp);
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

        session.Engine.AttackEntity(player, soldier);

        Assert.Equal(hpBefore - 1, soldier.Get<ActorComponent>().HitPoints);
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
        Assert.Contains(deltas, delta => delta.Operation == "delayIncoming");
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

        await session.ExecuteAsync(new CastCommand("cast forget the caster"));
        Assert.Single(soldier.Get<MemoryComponent>().Records);
        Assert.Contains(session.Engine.State.Memories.Records, record => record.SubjectId == "soldier_1");

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
        Assert.Empty(removeSoldier.Get<MemoryComponent>().Records);
        var playerSoulId = removeSession.Engine.State.ControlledEntity.Get<SoulComponent>().SoulId;
        var soldierSoulId = removeSoldier.Get<SoulComponent>().SoulId;
        Assert.True(removeSession.Engine.State.Bonds.TryGet(soldierSoulId, playerSoulId, out var bond));
        Assert.True(bond.Loyalty >= 5);
        Assert.False(removeSession.Engine.IsHostile(removeSoldier, removeSession.Engine.State.ControlledEntity));
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
        session.Engine.AttackEntity(soldier, player);

        Assert.True(soldier.Get<ActorComponent>().HitPoints < soldierHpBefore);
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

        await session.ExecuteAsync(new MoveCommand(Direction.East));
        Assert.Equal(new GridPoint(1, 0), session.Engine.State.LastControlledMoveDelta);
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

        await session.ExecuteAsync(new CastCommand("cast a flowing current"));

        Assert.Equal(positionBefore.Translate(1, 0), player.Get<PositionComponent>().Position);
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
    public async Task WaterTerrainExtinguishesBurningBeforeOngoingDamage()
    {
        var session = GameSession.CreateImperialEncounter();
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(soldier.Get<ActorComponent>() with { HitPoints = 10 });
        var point = new GridPoint(4, 4);
        soldier.Set(new PositionComponent(point));
        session.Engine.ApplyStatus(soldier, "burning", duration: 3);
        session.Engine.SetTerrain(point, "shallow_water", duration: 4);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(10, soldier.Get<ActorComponent>().HitPoints);
        Assert.DoesNotContain(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
        Assert.Equal("steam_mist", session.Engine.State.Terrain[point]);
        Assert.Contains(result.Messages, message => message.Contains("mist", StringComparison.OrdinalIgnoreCase));
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
        session.Engine.SetTerrain(point, "wild_fire", duration: 4);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(8, soldier.Get<ActorComponent>().HitPoints);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
        Assert.Contains(result.Messages, message => message.Contains("burning", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message => message.Contains("ongoing harm", StringComparison.OrdinalIgnoreCase));
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
        session.Engine.SetTerrain(point, "vines", duration: 4);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status =>
            status.Id == "rooted"
            && status.DisplayName == "vine-snared");
        Assert.Contains(result.Messages, message => message.Contains("vine-snared", StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains(read.Messages, message => message.Contains("marble authority", StringComparison.OrdinalIgnoreCase));
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
