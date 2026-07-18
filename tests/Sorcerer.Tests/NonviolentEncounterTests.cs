using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Xunit;

namespace Sorcerer.Tests;

public sealed class NonviolentEncounterTests
{
    [Fact]
    public async Task ExchangeRejectsTokenPaymentWithoutMovingEitherItem()
    {
        var session = Setup(out var target);
        var playerInventory = session.Engine.State.ControlledEntity.Get<InventoryComponent>();
        var targetInventory = target.Get<InventoryComponent>();
        playerInventory.Items["gold"] = 5;
        targetInventory.Items["compliance_writ"] = 1;
        var turn = session.Engine.State.Turn;

        var result = await session.ExecuteAsync(new ExchangeCommand("1 gold for compliance_writ with encounter claimant"));

        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(turn, session.Engine.State.Turn);
        Assert.Equal(5, playerInventory.Items["gold"]);
        Assert.Equal(1, targetInventory.Items["compliance_writ"]);
        Assert.Contains(result.Messages, message => message.Contains("refuses", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FairReciprocalExchangeIsAtomicAndClosesHostility()
    {
        var session = Setup(out var target);
        var player = session.Engine.State.ControlledEntity;
        player.Get<InventoryComponent>().Items["red_tincture"] = 1;
        target.Get<InventoryComponent>().Items["compliance_writ"] = 1;
        Assert.True(session.Engine.IsHostile(target, player));

        var result = await session.ExecuteAsync(new ExchangeCommand("red_tincture for compliance_writ with encounter claimant"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.True(result.ConsumedTurn);
        Assert.Equal(0, player.Get<InventoryComponent>().Items.GetValueOrDefault("red_tincture"));
        Assert.Equal(1, player.Get<InventoryComponent>().Items["compliance_writ"]);
        Assert.Equal(1, target.Get<InventoryComponent>().Items["red_tincture"]);
        Assert.Equal(0, target.Get<InventoryComponent>().Items.GetValueOrDefault("compliance_writ"));
        Assert.False(session.Engine.IsHostile(target, player));
        Assert.Contains(result.Deltas, delta => delta.Operation == "exchangeBond");
    }

    [Fact]
    public async Task ConcessionClosesEncounterButCreatesPoliticalAndCanonicalCost()
    {
        var session = Setup(out var target);
        var state = session.Engine.State;
        var standingBefore = state.Factions.StandingValue("empire", "freedom");

        var result = await session.ExecuteAsync(new ConcedeCommand("encounter claimant"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.False(session.Engine.IsHostile(target, state.ControlledEntity));
        Assert.Equal(standingBefore - 1, state.Factions.StandingValue("empire", "freedom"));
        Assert.Contains(state.Canon.Records, record => record.Kind == "concession" && record.AttachedTo == target.Id.Value);
    }

    [Fact]
    public async Task IntimidationReadsBodySoulAndEquipmentAndCreatesBoundedFlight()
    {
        var session = Setup(out var target);
        var player = session.Engine.State.ControlledEntity;
        player.Set(new BodyStatsComponent(20));
        player.Set(new SoulStatsComponent(20, 20));
        player.Set(new EquipmentEffectComponent(
            5,
            5,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            Array.Empty<string>()));

        var result = await session.ExecuteAsync(new IntimidateCommand("encounter claimant"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Contains(result.Messages, message => message.Contains("breaks off", StringComparison.OrdinalIgnoreCase));
        Assert.True(target.TryGet<BehaviorTagsComponent>(out var behavior));
        Assert.True(behavior.Tags.ContainsKey("coward"));
        Assert.False(session.Engine.IsHostile(target, player));
    }

    [Fact]
    public async Task ValuableOfferCanCloseHostilityWhileATrivialOfferCannot()
    {
        var cheapSession = Setup(out var cheapTarget);
        cheapSession.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["gold"] = 20;

        var cheap = await cheapSession.ExecuteAsync(new OfferCommand("1 gold to encounter claimant"));

        Assert.True(cheap.Success);
        Assert.True(cheapSession.Engine.IsHostile(cheapTarget, cheapSession.Engine.State.ControlledEntity));
        Assert.Contains(cheap.Messages, message => message.Contains("not enough", StringComparison.OrdinalIgnoreCase));

        var valuableSession = Setup(out var valuableTarget);
        valuableSession.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["gold"] = 20;

        var valuable = await valuableSession.ExecuteAsync(new OfferCommand("12 gold to encounter claimant"));

        Assert.True(valuable.Success, string.Join(" | ", valuable.Messages));
        Assert.False(valuableSession.Engine.IsHostile(valuableTarget, valuableSession.Engine.State.ControlledEntity));
        Assert.Contains(valuable.Messages, message => message.Contains("stands down", StringComparison.OrdinalIgnoreCase));
    }

    private static GameSession Setup(out Entity target)
    {
        var session = GameSession.CreateImperialEncounter(seed: 27);
        var state = session.Engine.State;
        foreach (var entity in state.Entities.Values.Where(entity => entity.Id != state.ControlledEntityId && entity.Has<ActorComponent>()))
        {
            entity.Set(entity.Get<ActorComponent>() with { Faction = "neutral" });
            if (entity.Has<AiComponent>())
            {
                entity.Set(new AiComponent("idle"));
            }
        }

        var origin = state.ControlledEntity.Get<PositionComponent>().Position;
        target = new Entity(EntityId.Create("encounter_claimant"), "encounter claimant")
            .Set(new PositionComponent(origin.Translate(1, 0)))
            .Set(new ActorComponent(6, 6, 0, 0, 1, 0, "empire"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("hostile"))
            .Set(new SoulComponent("encounter_claimant_soul"))
            .Set(new InventoryComponent(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            .Set(new TagsComponent(new[] { "npc", "claimant", "threat" }));
        state.Entities[target.Id] = target;
        return session;
    }
}
