using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

public sealed partial class GameSession
{
    private ActionResult Brace()
    {
        var before = Engine.State.Turn;
        var actor = Engine.State.ControlledEntity;
        var equipment = EquipmentEffectService.Recompute(actor, ItemCatalog.LoadDefault());
        if (equipment.Defense <= 0)
        {
            return ActionResult.Simple(
                "brace",
                false,
                false,
                before,
                before,
                "Bracing needs worn defensive equipment. Equip armor or a shield first.");
        }

        var applied = Engine.ApplyConsequence(WorldConsequence.SetBehavior(
            "tactics",
            actor.Id.Value,
            "braced",
            duration: 2,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            reason: "The controlled body commits its worn protection against the next exchange.",
            operation: "brace",
            details: new Dictionary<string, object?>
            {
                ["equipmentDefense"] = equipment.Defense,
                ["playerVisible"] = true,
            }));
        if (!applied.Applied)
        {
            return ActionResult.Simple("brace", false, false, before, before, applied.Error ?? "The stance could not be applied.");
        }

        var turnDeltas = Engine.AdvanceTurn();
        var deltas = applied.Deltas.Concat(turnDeltas).ToArray();
        return new ActionResult
        {
            Action = "brace",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = before,
            TurnAfter = Engine.State.Turn,
            Messages = new[] { $"You set your equipment against the blow (+{equipment.Defense} braced defense)." }
                .Concat(deltas.PlayerMessages())
                .Distinct()
                .ToArray(),
            Deltas = deltas,
        };
    }

    private ActionResult CounterIntent(string text)
    {
        var before = Engine.State.Turn;
        var marker = text.LastIndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0 || marker >= text.Length - 6)
        {
            return ActionResult.Simple(
                "counter",
                false,
                false,
                before,
                before,
                "Use: counter <enemy> with <item>. Inspect reveals authored counterplay.");
        }

        var targetText = text[..marker].Trim();
        var itemText = text[(marker + 6)..].Trim();
        var target = FindNearbyActor(targetText);
        if (target is null)
        {
            return ActionResult.Simple("counter", false, false, before, before, $"No nearby actor matches '{targetText}'.");
        }

        if (!HasActiveBehavior(target, "tactical_committed"))
        {
            return ActionResult.Simple("counter", false, false, before, before, $"{target.Name} has no committed action to interrupt.");
        }

        var player = Engine.State.ControlledEntity;
        var catalog = ItemCatalog.LoadDefault();
        if (!player.TryGet<InventoryComponent>(out var inventory)
            || !TryInventoryKey(inventory, itemText, catalog, out var itemKey))
        {
            return ActionResult.Simple("counter", false, false, before, before, $"You do not carry '{itemText}'.");
        }

        var archetype = ArchetypeFor(target);
        var definition = catalog.Find(itemKey);
        if (archetype is null || !ItemAnswersCounter(itemKey, definition, archetype))
        {
            var hint = archetype?.Counter ?? "inspect the enemy for a specific counter";
            return ActionResult.Simple(
                "counter",
                false,
                false,
                before,
                before,
                $"{itemKey} does not answer this intent. Counter: {hint}.");
        }

        var snapshot = GameStateSnapshot.Capture(Engine.State);
        var deltas = new List<StateDelta>();
        var clear = Engine.ApplyConsequence(WorldConsequence.UpdateBehavior(
            "tactics",
            target.Id.Value,
            "tactical_committed",
            "complete",
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: player.Id.Value,
            reason: $"{itemKey} answers {archetype.Counter}.",
            operation: "counterIntent",
            details: new Dictionary<string, object?> { ["playerVisible"] = false, ["auditOnly"] = true }));
        if (!clear.Applied)
        {
            snapshot.Restore(Engine.State);
            return ActionResult.Simple("counter", false, false, before, before, clear.Error ?? "The prepared action could not be cleared.");
        }

        deltas.AddRange(clear.Deltas);
        var disrupt = Engine.ApplyConsequence(WorldConsequence.SetBehavior(
            "tactics",
            target.Id.Value,
            "disrupted",
            duration: 2,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: player.Id.Value,
            reason: "A concrete counter broke the actor's prepared action.",
            operation: "disruptIntent"));
        if (!disrupt.Applied)
        {
            snapshot.Restore(Engine.State);
            return ActionResult.Simple("counter", false, false, before, before, disrupt.Error ?? "The target could not be disrupted.");
        }

        deltas.AddRange(disrupt.Deltas);
        // Commodity/component counters are spent; durable gear remains a repeatable tactical tool.
        if (definition is null || definition.StackPolicy.Equals("commodity", StringComparison.OrdinalIgnoreCase))
        {
            var consume = Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
                "tactics",
                player.Id.Value,
                itemKey,
                "remove",
                1,
                sourceEntityId: player.Id.Value,
                reason: "The countering component is expended.",
                operation: "consumeCounterItem"));
            if (!consume.Applied)
            {
                snapshot.Restore(Engine.State);
                return ActionResult.Simple("counter", false, false, before, before, consume.Error ?? "The countering item could not be spent.");
            }

            deltas.AddRange(consume.Deltas);
        }

        deltas.AddRange(Engine.AdvanceTurn());
        return new ActionResult
        {
            Action = "counter",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = before,
            TurnAfter = Engine.State.Turn,
            Messages = new[] { $"You use {definition?.Name ?? itemKey.Replace('_', ' ')} to break {target.Name}'s committed action." }
                .Concat(deltas.PlayerMessages())
                .Distinct()
                .ToArray(),
            Deltas = deltas,
        };
    }

    private Entity? FindNearbyActor(string reference)
    {
        var player = Engine.State.ControlledEntity;
        if (!player.TryGet<PositionComponent>(out var origin))
        {
            return null;
        }

        return Engine.State.Entities.Values
            .Where(entity => entity.Id != player.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.Distance(origin.Position, position.Position) <= 8)
            .Where(entity => entity.Id.Value.Equals(reference, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(reference, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => GameEngine.Distance(origin.Position, entity.Get<PositionComponent>().Position))
            .ThenBy(entity => entity.Id.Value)
            .FirstOrDefault();
    }

    private bool HasActiveBehavior(Entity entity, string tag) =>
        entity.TryGet<BehaviorTagsComponent>(out var behaviors)
        && behaviors.Tags.TryGetValue(tag, out var expiry)
        && (expiry is null || expiry > Engine.State.Turn);

    private static ActorArchetypeDefinition? ArchetypeFor(Entity entity)
    {
        if (!entity.TryGet<TagsComponent>(out var tags))
        {
            return null;
        }

        return ActorArchetypeCatalog.Default.Archetypes.FirstOrDefault(archetype =>
            tags.Tags.Contains(archetype.Id, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryInventoryKey(
        InventoryComponent inventory,
        string reference,
        ItemCatalog catalog,
        out string key)
    {
        var normalized = NormalizeToken(reference, "");
        key = inventory.Items.Keys.FirstOrDefault(candidate =>
            candidate.Equals(reference, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(candidate, "").Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(catalog.Find(candidate)?.Name ?? "", "").Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(candidate, "").Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(catalog.Find(candidate)?.Name ?? "", "").Contains(normalized, StringComparison.OrdinalIgnoreCase)) ?? "";
        return key.Length > 0 && inventory.Items.GetValueOrDefault(key) > 0;
    }

    private static bool ItemAnswersCounter(
        string itemKey,
        ItemDefinition? item,
        ActorArchetypeDefinition archetype)
    {
        var itemWords = SignificantWords(string.Join(' ', new[]
            {
                itemKey,
                item?.Name ?? "",
                item?.Material ?? "",
                item?.Description ?? "",
            }.Concat(item?.Tags ?? Array.Empty<string>())));
        var answerWords = SignificantWords($"{archetype.Counter} {archetype.Weakness}");
        return itemWords.Overlaps(answerWords);
    }

    private static HashSet<string> SignificantWords(string text) =>
        text.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', ';', ':', '-', '_', '/', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4)
            .Select(word => word.EndsWith('s') ? word[..^1] : word)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
