using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;

namespace Sorcerer.Core;

/// <summary>General restricted-threshold play: force and forgery operate on components/tags.</summary>
public sealed partial class GameSession
{
    private ActionResult BreachThreshold(string? targetText)
    {
        var state = Engine.State;
        var before = state.Turn;
        var threshold = NearbyThreshold(targetText);
        if (threshold is null)
        {
            return ActionResult.Simple("breach", false, false, before, before, "No restricted threshold is within reach.");
        }

        var player = state.ControlledEntity;
        var actor = player.Get<ActorComponent>();
        var vigor = player.TryGet<BodyStatsComponent>(out var body) ? body.Vigor : 1;
        var gear = EquipmentEffectService.Recompute(player, ItemCatalog.LoadDefault());
        var durability = threshold.TryGet<PhysicalComponent>(out var physical) ? physical.Durability : 0;
        var force = vigor + actor.Attack + gear.Attack;
        var difficulty = 6 + (durability / 3);
        if (force < difficulty)
        {
            var failedTurnDeltas = Engine.AdvanceTurn();
            return new ActionResult
            {
                Action = "breach",
                Success = false,
                ConsumedTurn = true,
                TurnBefore = before,
                TurnAfter = state.Turn,
                Messages = new[] { $"You fail to force {threshold.Name} ({force} force vs {difficulty}). Better leverage or stronger equipment would do it." }
                    .Concat(failedTurnDeltas.PlayerMessages()).ToArray(),
                Deltas = failedTurnDeltas,
            };
        }

        var opened = Engine.ApplyConsequence(WorldConsequence.AddTags(
            "breach",
            threshold.Id.Value,
            new[] { "forced_open", "access_granted" },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: player.Id.Value,
            evidence: $"Force {force} exceeded threshold difficulty {difficulty}.",
            reason: "A body and its worn leverage forced an authored restricted threshold.",
            operation: "breachThreshold",
            details: new Dictionary<string, object?> { ["playerVisible"] = true }));
        if (!opened.Applied)
        {
            return ActionResult.Simple("breach", false, false, before, before, opened.Error ?? "The threshold resisted the breach.");
        }

        var canon = Engine.ApplyConsequence(WorldConsequence.AddCanon(
            "breach",
            "threshold_breached",
            threshold.Id.Value,
            $"{player.Name} visibly forced entry through {threshold.Name}.",
            $"{threshold.Name} was visibly breached.",
            tags: new[] { "force", "trespass", "witnessable" },
            sourceEntityId: player.Id.Value,
            evidence: threshold.Name,
            reason: "Force is effective but leaves world-facing evidence.",
            operation: "recordBreach"));
        var turnDeltas = Engine.AdvanceTurn();
        var deltas = opened.Deltas.Concat(canon.Deltas).Concat(turnDeltas).ToArray();
        return new ActionResult
        {
            Action = "breach",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = before,
            TurnAfter = state.Turn,
            Messages = new[] { $"You force {threshold.Name} open. The damage is obvious." }
                .Concat(deltas.PlayerMessages()).Distinct().ToArray(),
            Deltas = deltas,
        };
    }

    private ActionResult ForgeCredential(string text)
    {
        var state = Engine.State;
        var before = state.Turn;
        var player = state.ControlledEntity;
        if (!player.TryGet<InventoryComponent>(out var inventory))
        {
            return ActionResult.Simple("forge", false, false, before, before, "You have no materials to forge with.");
        }

        var catalog = ItemCatalog.LoadDefault();
        var blank = inventory.Items.Keys.FirstOrDefault(key => inventory.Items.GetValueOrDefault(key) > 0
            && catalog.Find(key) is { } item
            && item.Tags.Contains("blank", StringComparer.OrdinalIgnoreCase)
            && item.Tags.Contains("forgery", StringComparer.OrdinalIgnoreCase));
        var ink = inventory.Items.Keys.FirstOrDefault(key => inventory.Items.GetValueOrDefault(key) > 0
            && catalog.Find(key) is { } item
            && item.Tags.Contains("ink", StringComparer.OrdinalIgnoreCase)
            && item.Tags.Contains("forgery", StringComparer.OrdinalIgnoreCase));
        var credential = catalog.Items.FirstOrDefault(item =>
            item.Tags.Contains("credential", StringComparer.OrdinalIgnoreCase)
            && item.Tags.Contains("access", StringComparer.OrdinalIgnoreCase));
        if (blank is null || ink is null || credential is null)
        {
            return ActionResult.Simple(
                "forge",
                false,
                false,
                before,
                before,
                "Forging a credential needs a blank forgery form and permit ink.");
        }

        var snapshot = GameStateSnapshot.Capture(state);
        var packets = new[]
        {
            WorldConsequence.ModifyInventory("forgery", player.Id.Value, blank, "remove", 1, sourceEntityId: player.Id.Value, operation: "spendForgeryBlank"),
            WorldConsequence.ModifyInventory("forgery", player.Id.Value, ink, "remove", 1, sourceEntityId: player.Id.Value, operation: "spendForgeryInk"),
            WorldConsequence.ModifyInventory("forgery", player.Id.Value, credential.Id, "add", 1, visibility: WorldConsequenceVisibility.Message, sourceEntityId: player.Id.Value, reason: text, operation: "forgeCredential", emitMessage: true),
        };
        var deltas = new List<StateDelta>();
        foreach (var packet in packets)
        {
            var applied = Engine.ApplyConsequence(packet);
            if (!applied.Applied)
            {
                snapshot.Restore(state);
                return ActionResult.Simple("forge", false, false, before, before, applied.Error ?? "The forgery failed transactionally.");
            }

            deltas.AddRange(applied.Deltas);
        }

        deltas.AddRange(Engine.AdvanceTurn());
        return new ActionResult
        {
            Action = "forge",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = before,
            TurnAfter = state.Turn,
            Messages = new[] { $"You turn {blank} and {ink} into {credential.Name}. Its authority is now an ordinary carried capability." }
                .Concat(deltas.PlayerMessages()).Distinct().ToArray(),
            Deltas = deltas,
        };
    }

    private Entity? NearbyThreshold(string? reference)
    {
        var player = Engine.State.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;
        return Engine.State.Entities.Values
            .Where(entity => entity.Has<InteriorEntranceComponent>())
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.Distance(origin, position.Position) <= 2)
            .Where(entity => string.IsNullOrWhiteSpace(reference)
                || entity.Id.Value.Contains(reference, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(reference, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => entity.Id.Value)
            .FirstOrDefault();
    }
}
