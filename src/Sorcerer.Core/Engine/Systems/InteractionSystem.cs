using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class InteractionSystem
{
    private readonly GameEngine _engine;
    private readonly ItemSystem _itemSystem;
    private readonly TurnSystem _turnSystem;

    public InteractionSystem(GameEngine engine, ItemSystem itemSystem, TurnSystem turnSystem)
    {
        _engine = engine;
        _itemSystem = itemSystem;
        _turnSystem = turnSystem;
    }

    private GameState State => _engine.State;

    public ActionResult Talk(string text)
    {
        var turnBefore = State.Turn;
        if (TryRouteDialogueShortcut(text, out var shortcut))
        {
            return shortcut;
        }

        var target = ResolveNearbyEntity(
            text,
            entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
            range: 2);
        target ??= ResolveNearbyActorMention(text);
        if (target is null)
        {
            return ActionResult.Simple("talk", false, false, turnBefore, State.Turn, "No one nearby is ready to talk.");
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        ResolveDialogueIntent(target, text, messages, deltas);
        deltas.AddRange(RealizePromisesForEntity(target, "talk", messages));
        foreach (var line in messages)
        {
            State.AddMessage(line);
        }

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "talk",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    public ActionResult Give(string item, string? targetText)
    {
        var turnBefore = State.Turn;
        if (string.IsNullOrWhiteSpace(item))
        {
            return ActionResult.Simple("give", false, false, turnBefore, State.Turn, "Name something to give.");
        }

        var target = ResolveNearbyEntity(
            targetText,
            entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
            range: 2);
        target ??= ResolveNearbyActorMention(targetText);
        if (target is null)
        {
            return ActionResult.Simple("give", false, false, turnBefore, State.Turn, "No one nearby can receive that gift.");
        }

        var inventory = State.ControlledEntity.Get<InventoryComponent>();
        var key = FindInventoryKey(inventory, item);
        if (key is null)
        {
            return ActionResult.Simple("give", false, false, turnBefore, State.Turn, $"You are not carrying {item}.");
        }

        if (inventory.TreasuredItems.Contains(key))
        {
            return ActionResult.Simple("give", false, false, turnBefore, State.Turn, $"{key} is protected; unprotect it before giving it away.");
        }

        DecrementInventory(inventory, key);
        var value = GiftValue(key, target);
        var bond = AdjustBond(target, loyalty: value, admiration: Math.Max(1, value - 1), posture: "grateful");
        AddEntityMemory(target, $"{target.Name} accepted {key} from the sorcerer.", "gift", 2);
        State.Memories.Append(
            target.Id.Value,
            $"{target.Name} accepted {key} from the sorcerer.",
            "gift",
            2,
            shareable: true);
        var messages = new List<string>
        {
            $"{target.Name} accepts {key}. Something personal begins to keep score.",
            BondMoodLine(target, bond),
        };
        var deltas = new List<StateDelta>
        {
            new(
                "giveItem",
                target.Id.Value,
                $"{target.Name} receives {key}.",
                new Dictionary<string, object?>
                {
                    ["item"] = key,
                    ["loyalty"] = bond.Loyalty,
                    ["admiration"] = bond.Admiration,
                }),
        };
        foreach (var line in messages)
        {
            State.AddMessage(line);
        }

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "give",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    public ActionResult Recruit(string? targetText)
    {
        var turnBefore = State.Turn;
        var target = ResolveNearbyEntity(
            targetText,
            entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
            range: 2);
        target ??= ResolveNearbyActorMention(targetText);
        if (target is null)
        {
            return ActionResult.Simple("recruit", false, false, turnBefore, State.Turn, "No one nearby is ready to be asked.");
        }

        var playerSoulId = PlayerSoulId();
        var subjectSoulId = SoulIdFor(target);
        var bond = State.Bonds.GetOrCreate(subjectSoulId, playerSoulId);
        var recruitScore = bond.Loyalty + bond.Admiration - bond.Resentment;
        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        if (recruitScore < 5 && !bond.Posture.Equals("grateful", StringComparison.OrdinalIgnoreCase))
        {
            var refusal = $"{target.Name} listens, but the bond is not strong enough to make a life out of it.";
            messages.Add(refusal);
            State.AddMessage(refusal);
            AdjustBond(target, resentment: 1, posture: bond.Posture);
            var refusalTurnDeltas = AdvanceTurn();
            return new ActionResult
            {
                Action = "recruit",
                Success = false,
                ConsumedTurn = true,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = messages.Concat(refusalTurnDeltas.Select(delta => delta.Summary)).ToArray(),
                Deltas = refusalTurnDeltas,
            };
        }

        var actor = target.Get<ActorComponent>();
        target.Set(actor with { Faction = "player" });
        target.Set(new ControllerComponent(ControllerKind.Ai));
        target.Set(new AiComponent("follower"));
        PreserveMembershipAndAddRole(target, "follower");
        var updatedBond = State.Bonds.Adjust(subjectSoulId, playerSoulId, loyalty: 1, admiration: 1, posture: "follower");
        AddEntityMemory(target, $"{target.Name} chose to follow the sorcerer.", "recruit", 3);
        var message = $"{target.Name} chooses to follow you, carrying old loyalties separately from the new bond.";
        messages.Add(message);
        deltas.Add(new StateDelta(
            "recruit",
            target.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["posture"] = updatedBond.Posture,
                ["combatFaction"] = "player",
                ["membership"] = target.TryGet<FactionComponent>(out var membership) ? membership.FactionId : "",
            }));
        foreach (var line in messages)
        {
            State.AddMessage(line);
        }

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "recruit",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    public ActionResult Bonds(string? targetText)
    {
        var turn = State.Turn;
        var playerSoulId = PlayerSoulId();
        var actors = State.Entities.Values
            .Where(entity => entity.Id != State.ControlledEntityId)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => string.IsNullOrWhiteSpace(targetText)
                || entity.Id.Value.Contains(targetText, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(targetText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => entity.Id.Value)
            .Select(entity => State.Bonds.TryGet(SoulIdFor(entity), playerSoulId, out var bond)
                ? $"{entity.Name}: {BondSummary(bond)}"
                : $"{entity.Name}: no personal bond yet")
            .ToArray();
        return ActionResult.Simple("bonds", true, false, turn, turn, actors.Length == 0 ? new[] { "No personal bonds have crystallized yet." } : actors);
    }

    public ActionResult Read(string? target)
    {
        var turnBefore = State.Turn;
        var entity = ResolveNearbyEntity(target, candidate => candidate.Has<ReadableComponent>(), range: 1);
        if (entity is null)
        {
            return ActionResult.Simple("read", false, false, turnBefore, State.Turn, "There is nothing readable within reach.");
        }

        var readable = entity.Get<ReadableComponent>();
        var body = string.IsNullOrWhiteSpace(readable.TextKey)
            ? $"{readable.Title}: the words hold still just long enough to be understood."
            : readable.TextKey;
        State.Canon.Add(
            "readable",
            entity.Id.Value,
            body,
            readable.Title,
            TagsFor(entity),
            "read",
            State.Turn);
        _turnSystem.EnqueueBackgroundJob("canon_detail", entity, priority: 3);
        var messages = new List<string> { body };
        var deltas = RealizePromisesForEntity(entity, "read", messages);
        foreach (var line in messages)
        {
            State.AddMessage(line);
        }

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "read",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    public ActionResult Examine(string? target)
    {
        var entity = ResolveNearbyEntity(target, entity => entity.Id != State.ControlledEntityId, range: 2);
        if (entity is null)
        {
            return ActionResult.Simple("examine", false, false, State.Turn, State.Turn, "There is nothing close enough to examine.");
        }

        var messages = DescribeEntity(entity);
        _turnSystem.EnqueueBackgroundJob("entity_detail", entity, priority: 2);
        return ActionResult.Simple("examine", true, false, State.Turn, State.Turn, messages.ToArray());
    }

    public ActionResult Open(string? target)
    {
        var turnBefore = State.Turn;
        var door = ResolveNearbyEntity(target, entity => entity.Has<DoorComponent>(), range: 1);
        if (door is null)
        {
            return ActionResult.Simple("open", false, false, turnBefore, State.Turn, "There is nothing here you can open.");
        }

        var doorComponent = door.Get<DoorComponent>();
        if (doorComponent.IsOpen)
        {
            return ActionResult.Simple("open", false, false, turnBefore, State.Turn, $"{door.Name} is already open.");
        }

        if (!string.IsNullOrWhiteSpace(doorComponent.KeyId)
            && !_itemSystem.IsCarrying(doorComponent.KeyId))
        {
            return ActionResult.Simple("open", false, false, turnBefore, State.Turn, $"{door.Name} is locked.");
        }

        door.Set(doorComponent with { IsOpen = true });
        if (door.TryGet<PhysicalComponent>(out var physical))
        {
            door.Set(physical with { BlocksMovement = false, BlocksSight = false });
        }

        if (door.TryGet<RenderableComponent>(out var renderable))
        {
            door.Set(renderable with { Glyph = '/', Palette = "open" });
        }

        var messages = new List<string> { $"You open {door.Name}." };
        var deltas = new List<StateDelta>
        {
            new(
                "open",
                door.Id.Value,
                messages[0],
                new Dictionary<string, object?> { ["open"] = true }),
        };

        ResolveDoorConsequences(door, messages, deltas);
        foreach (var message in messages)
        {
            State.AddMessage(message);
        }

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "open",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    public StateDelta AddPromise(string kind, string text, Entity? anchor = null, string triggerHint = "", string source = "wild_magic")
    {
        var subject = State.ControlledEntity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : State.ControlledEntityId.Value;
        var promise = State.PromiseLedger.Add(
            kind,
            text,
            playerVisible: true,
            source: source,
            salience: 2,
            subject: subject,
            claimedPlace: State.RegionId,
            triggerHint: triggerHint,
            realizationKind: InferRealizationKind(kind, text));

        var bound = BindPromiseIfPossible(promise, anchor, triggerHint);
        var finalPromise = bound ?? promise;
        var promiseNoun = finalPromise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase) ? "curse" : "promise";
        var message = finalPromise.Status == "bound"
            ? $"A {promiseNoun} binds to {finalPromise.BoundTargetId ?? finalPromise.BoundPlace}: {finalPromise.Text}"
            : $"A {promiseNoun} enters the world: {finalPromise.Text}";
        State.AddMessage(message);
        return new StateDelta(
            "createPromise",
            finalPromise.Id,
            message,
            new Dictionary<string, object?>
            {
                ["kind"] = finalPromise.Kind,
                ["status"] = finalPromise.Status,
                ["subject"] = finalPromise.Subject,
                ["boundPlace"] = finalPromise.BoundPlace,
                ["boundTargetId"] = finalPromise.BoundTargetId,
                ["triggerHint"] = finalPromise.TriggerHint,
                ["realizationKind"] = finalPromise.RealizationKind,
            });
    }

    private Entity? ResolveNearbyEntity(
        string? target,
        Func<Entity, bool> predicate,
        int range)
    {
        var candidates = NearbyCandidates(predicate, range);

        if (!string.IsNullOrWhiteSpace(target))
        {
            var normalizedTarget = target.Trim();
            return candidates.FirstOrDefault(entity =>
                entity.Id.Value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || (entity.TryGet<TagsComponent>(out var tags)
                    && tags.Tags.Any(tag => tag.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))));
        }

        if (State.SelectedTarget is { } selected)
        {
            var selectedEntity = candidates.FirstOrDefault(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        return candidates.FirstOrDefault();
    }

    private IReadOnlyList<Entity> NearbyCandidates(Func<Entity, bool> predicate, int range)
    {
        var origin = State.ControlledEntity.Get<PositionComponent>().Position;
        return State.Entities.Values
            .Where(predicate)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && InteractionDistance(origin, position.Position) <= range)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position) ? GameEngine.Distance(origin, position.Position) : int.MaxValue)
            .ThenBy(entity => entity.Id.Value)
            .ToArray();
    }

    private bool TryRouteDialogueShortcut(string text, out ActionResult result)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("give ", StringComparison.OrdinalIgnoreCase))
        {
            var afterGive = text[(lower.IndexOf("give ", StringComparison.OrdinalIgnoreCase) + 5)..].Trim();
            var marker = afterGive.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                result = Give(afterGive[..marker].Trim(), afterGive[(marker + 4)..].Trim());
                return true;
            }
        }

        if (lower.Contains("recruit", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("join me", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("follow me", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("come with", StringComparison.OrdinalIgnoreCase))
        {
            result = Recruit(text);
            return true;
        }

        result = null!;
        return false;
    }

    private Entity? ResolveNearbyActorMention(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeSearchText(text);
        var candidates = NearbyCandidates(
            entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
            range: 2);
        return candidates.FirstOrDefault(entity =>
            normalized.Contains(NormalizeSearchText(entity.Id.Value), StringComparison.OrdinalIgnoreCase)
            || EntityNameTokens(entity).Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase))
            || (entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Any(tag => normalized.Contains(NormalizeSearchText(tag), StringComparison.OrdinalIgnoreCase))));
    }

    private static IEnumerable<string> EntityNameTokens(Entity entity) =>
        entity.Name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSearchText)
            .Where(token => token.Length >= 3);

    private static string NormalizeSearchText(string text) =>
        new(text
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray());

    private void ResolveDialogueIntent(
        Entity target,
        string text,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("threat", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("obey", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("fear", StringComparison.OrdinalIgnoreCase))
        {
            var bond = AdjustBond(target, fear: 2, resentment: 1, posture: "afraid");
            var message = $"{target.Name} hears the threat and remembers the shape of it.";
            messages.Add(message);
            deltas.Add(new StateDelta(
                "bondShift",
                target.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["posture"] = bond.Posture,
                    ["fear"] = bond.Fear,
                    ["resentment"] = bond.Resentment,
                }));
            AddEntityMemory(target, message, "dialogue:threat", 2);
            return;
        }

        if (lower.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("confide", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("trust me", StringComparison.OrdinalIgnoreCase))
        {
            ResolveSecretDialogue(target, messages, deltas);
            return;
        }

        var line = DialogueLine(target);
        messages.Add(line);
    }

    private void ResolveSecretDialogue(Entity target, List<string> messages, List<StateDelta> deltas)
    {
        var playerSoulId = PlayerSoulId();
        var subjectSoulId = SoulIdFor(target);
        var bond = State.Bonds.GetOrCreate(subjectSoulId, playerSoulId);
        var trust = bond.Loyalty + bond.Admiration - bond.Fear - bond.Resentment;
        if (trust < 3)
        {
            var refusal = $"{target.Name} almost says something true, then keeps it.";
            messages.Add(refusal);
            deltas.Add(new StateDelta("dialogueRefusal", target.Id.Value, refusal, new Dictionary<string, object?>()));
            return;
        }

        var text = $"{target.Name} confides a checkpoint secret that wants to become a place beyond this room.";
        var promise = State.PromiseLedger.Add(
            "quest",
            text,
            playerVisible: true,
            source: "dialogue",
            salience: 3,
            subject: target.Id.Value,
            claimedPlace: "checkpoint beyond this room",
            triggerHint: "travel",
            realizationKind: "site");
        var bound = BindPromiseIfPossible(promise, null, "travel") ?? promise;
        var message = $"{target.Name} gives you a secret with a road still folded inside it.";
        messages.Add(message);
        deltas.Add(new StateDelta(
            "dialoguePromise",
            bound.Id,
            message,
            new Dictionary<string, object?>
            {
                ["promiseId"] = bound.Id,
                ["subject"] = target.Id.Value,
            }));
        AddEntityMemory(target, text, bound.Id, 3);
    }

    private string DialogueLine(Entity target)
    {
        if (_engine.IsHostile(target, State.ControlledEntity))
        {
            return $"{target.Name} answers with trained imperial silence.";
        }

        if (target.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("prisoner", StringComparer.OrdinalIgnoreCase))
        {
            var bond = State.Bonds.Adjust(
                SoulIdFor(target),
                PlayerSoulId(),
                loyalty: 2,
                fear: 1,
                admiration: 1,
                posture: "grateful");
            var legend = PlayerLegendSummary();
            return string.IsNullOrWhiteSpace(legend)
                ? $"{target.Name} whispers, \"If you get me out, Hollowmere will remember the color of your magic.\" {BondMoodLine(target, bond)}"
                : $"{target.Name} whispers, \"I have heard the shape of you: {legend}. Get me out, and Hollowmere will remember.\" {BondMoodLine(target, bond)}";
        }

        if (target.TryGet<ProfileComponent>(out var profile))
        {
            return $"{profile.PublicName}: {profile.Appearance}";
        }

        return $"{target.Name} has nothing urgent to say.";
    }

    private BondRecord AdjustBond(
        Entity subject,
        int loyalty = 0,
        int fear = 0,
        int admiration = 0,
        int resentment = 0,
        string? posture = null) =>
        State.Bonds.Adjust(
            SoulIdFor(subject),
            PlayerSoulId(),
            loyalty,
            fear,
            admiration,
            resentment,
            posture);

    private string PlayerSoulId() => SoulIdFor(State.ControlledEntity);

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static string? FindInventoryKey(InventoryComponent inventory, string item) =>
        inventory.Items.Keys
            .OrderBy(key => key)
            .FirstOrDefault(key =>
                key.Equals(item.Trim(), StringComparison.OrdinalIgnoreCase)
                || key.Contains(item.Trim(), StringComparison.OrdinalIgnoreCase));

    private static void DecrementInventory(InventoryComponent inventory, string key)
    {
        inventory.Items[key] -= 1;
        if (inventory.Items[key] <= 0)
        {
            inventory.Items.Remove(key);
            inventory.TreasuredItems.Remove(key);
        }
    }

    private static int GiftValue(string item, Entity target)
    {
        var lower = item.ToLowerInvariant();
        var value = lower switch
        {
            var text when text.Contains("pearl", StringComparison.OrdinalIgnoreCase) => 4,
            var text when text.Contains("wand", StringComparison.OrdinalIgnoreCase) => 3,
            var text when text.Contains("salt", StringComparison.OrdinalIgnoreCase) => 3,
            var text when text.Contains("tincture", StringComparison.OrdinalIgnoreCase) => 2,
            var text when text.Contains("key", StringComparison.OrdinalIgnoreCase) => 2,
            var text when text.Contains("gold", StringComparison.OrdinalIgnoreCase) => 1,
            _ => 1,
        };
        if (target.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => lower.Contains(tag, StringComparison.OrdinalIgnoreCase)))
        {
            value += 1;
        }

        return Math.Clamp(value, 1, 5);
    }

    private static string BondMoodLine(Entity target, BondRecord bond) =>
        $"{target.Name}'s posture is {BondSummary(bond)}.";

    private static string BondSummary(BondRecord bond)
    {
        if (bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase))
        {
            return "following";
        }

        if (bond.Loyalty + bond.Admiration >= 5)
        {
            return "warm enough to risk something";
        }

        if (bond.Fear > bond.Loyalty + bond.Admiration)
        {
            return "afraid";
        }

        if (bond.Resentment >= 5)
        {
            return "resentful";
        }

        return bond.Posture;
    }

    private void AddEntityMemory(Entity target, string text, string source, int salience)
    {
        var memories = target.TryGet<MemoryComponent>(out var existing)
            ? existing.Records.ToList()
            : new List<EntityMemoryRecord>();
        memories.Add(new EntityMemoryRecord(
            $"memory_{memories.Count + 1}",
            text,
            source,
            "dialogue",
            salience,
            Shareable: true));
        target.Set(new MemoryComponent(memories));
    }

    private void PreserveMembershipAndAddRole(Entity target, string role)
    {
        if (target.TryGet<FactionComponent>(out var faction))
        {
            var roles = faction.Roles
                .Concat(new[] { role })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            target.Set(new FactionComponent(faction.FactionId, roles));
            return;
        }

        var actor = target.Get<ActorComponent>();
        target.Set(new FactionComponent(actor.Faction, new[] { role }));
    }

    private string PlayerLegendSummary()
    {
        var playerSoulId = PlayerSoulId();
        var tags = State.Legend.Tags
            .Where(tag => tag.ActorSoulId.Equals(playerSoulId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Tag)
            .Select(group => $"{group.Key} {group.Sum(tag => tag.Weight)}")
            .OrderBy(text => text)
            .ToArray();
        return tags.Length == 0 ? "" : string.Join(", ", tags);
    }

    private IReadOnlyList<string> DescribeEntity(Entity entity)
    {
        var lines = new List<string> { $"{entity.Name} ({entity.Id.Value})." };
        if (entity.TryGet<DescriptionComponent>(out var description))
        {
            lines.Add(description.Text);
        }

        foreach (var record in State.Canon.Records
            .Where(record => record.AttachedTo.Equals(entity.Id.Value, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.TurnCreated)
            .Take(2))
        {
            lines.Add($"Known detail: {record.Summary}");
        }

        if (entity.TryGet<PhysicalComponent>(out var physical))
        {
            lines.Add($"Material: {physical.Material}; blocks movement: {physical.BlocksMovement}.");
        }

        if (entity.TryGet<ActorComponent>(out var actor))
        {
            lines.Add($"HP {actor.HitPoints}/{actor.MaxHitPoints}; faction {actor.Faction}.");
        }

        if (entity.TryGet<ReadableComponent>(out var readable))
        {
            lines.Add($"Readable: {readable.Title}.");
        }

        if (entity.TryGet<DoorComponent>(out var door))
        {
            lines.Add(door.IsOpen ? "It is open." : string.IsNullOrWhiteSpace(door.KeyId) ? "It is closed." : "It is locked.");
        }

        if (entity.TryGet<TagsComponent>(out var tags) && tags.Tags.Count > 0)
        {
            lines.Add($"Tags: {string.Join(", ", tags.Tags)}.");
        }

        if (entity.TryGet<StatusContainerComponent>(out var statuses))
        {
            var active = statuses.Statuses.Where(IsStatusActive).Select(status => status.DisplayName).ToArray();
            if (active.Length > 0)
            {
                lines.Add($"Statuses: {string.Join(", ", active)}.");
            }
        }

        return lines;
    }

    private void ResolveDoorConsequences(Entity door, List<string> messages, List<StateDelta> deltas)
    {
        deltas.AddRange(RealizePromisesForEntity(door, "open", messages));

        if (!door.Name.Contains("cell", StringComparison.OrdinalIgnoreCase)
            && !door.Id.Value.Contains("cell", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var doorPosition = door.Get<PositionComponent>().Position;
        var prisoner = State.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("prisoner", StringComparer.OrdinalIgnoreCase)
            && entity.TryGet<PositionComponent>(out var position)
            && GameEngine.Distance(position.Position, doorPosition) <= 2);
        if (prisoner is null || !prisoner.TryGet<ActorComponent>(out var actor))
        {
            return;
        }

        prisoner.Set(actor with { Faction = "player" });
        prisoner.Set(new ControllerComponent(ControllerKind.Ai));
        prisoner.Set(new AiComponent("follower"));
        PreserveMembershipAndAddRole(prisoner, "rescued");
        PreserveMembershipAndAddRole(prisoner, "follower");
        State.Bonds.Adjust(
            SoulIdFor(prisoner),
            PlayerSoulId(),
            loyalty: 4,
            admiration: 2,
            posture: "follower");
        _engine.RecordDeed(
            State.ControlledEntity,
            "freed_prisoner",
            3,
            State.ControlledEntity.Get<PositionComponent>().Position,
            prisoner.Get<PositionComponent>().Position,
            new[] { "mercy", "anti_empire", "hollowmere" });

        var rescue = $"{prisoner.Name} is free enough to choose you, for now.";
        messages.Add(rescue);
        deltas.Add(new StateDelta(
            "freePrisoner",
            prisoner.Id.Value,
            rescue,
            new Dictionary<string, object?>
            {
                ["faction"] = "player",
                ["deed"] = "freed_prisoner",
            }));
    }

    private WorldPromise? BindPromiseIfPossible(WorldPromise promise, Entity? anchor, string triggerHint)
    {
        anchor ??= ResolvePromiseAnchorFromSelectionOrText(promise.Text);
        if (anchor is not null)
        {
            AttachPromiseAnchor(anchor, promise.Id);
            return State.PromiseLedger.Bind(
                promise.Id,
                boundPlace: State.RegionId,
                boundTargetId: anchor.Id.Value,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferTriggerHint(promise.Text, anchor) : triggerHint,
                realizationKind: promise.RealizationKind);
        }

        if (CanBindToRegion(promise))
        {
            return State.PromiseLedger.Bind(
                promise.Id,
                boundPlace: State.RegionId,
                boundTargetId: null,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferTriggerHint(promise.Text, null) : triggerHint,
                realizationKind: promise.RealizationKind);
        }

        return null;
    }

    private void AttachPromiseAnchor(Entity anchor, string promiseId)
    {
        var ids = anchor.TryGet<PromiseAnchorComponent>(out var existing)
            ? existing.PromiseIds.ToList()
            : new List<string>();
        if (!ids.Contains(promiseId, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(promiseId);
        }

        anchor.Set(new PromiseAnchorComponent(ids));
    }

    private IReadOnlyList<StateDelta> RealizePromisesForEntity(Entity entity, string trigger, List<string> messages)
    {
        if (!entity.TryGet<PromiseAnchorComponent>(out var anchor))
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        foreach (var promiseId in anchor.PromiseIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = State.PromiseLedger.Promises.FirstOrDefault(promise =>
                promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase));
            if (existing is null
                || existing.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
                || !PromiseTriggerMatches(existing.TriggerHint, trigger))
            {
                continue;
            }

            var realizedIn = $"{trigger}:{entity.Id.Value}";
            var realized = State.PromiseLedger.SetStatus(existing.Id, "realized", realizedIn);
            if (realized is null)
            {
                continue;
            }

            var message = $"A promise stirs awake: {realized.Text}";
            messages.Add(message);
            deltas.Add(new StateDelta(
                "realizePromise",
                realized.Id,
                message,
                new Dictionary<string, object?>
                {
                    ["status"] = realized.Status,
                    ["trigger"] = trigger,
                    ["target"] = entity.Id.Value,
                    ["realizedIn"] = realized.RealizedIn,
                    ["realizationKind"] = realized.RealizationKind,
                }));
            deltas.AddRange(ApplyPromiseRealization(realized, entity, trigger, messages));
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyPromiseRealization(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var kind = NormalizeId(promise.RealizationKind ?? promise.Kind, "omen");
        return kind switch
        {
            "memory" => RealizePromiseMemory(promise, anchor, trigger, messages),
            "threat" => RealizePromiseThreat(promise, anchor, trigger, messages),
            "item" => RealizePromiseItem(promise, anchor, trigger, messages),
            "quest" => RealizePromiseCanon(promise, anchor, trigger, messages, "quest", "A quest takes shape"),
            "site" => RealizePromiseCanon(promise, anchor, trigger, messages, "site", "A distant place answers"),
            _ => RealizePromiseCanon(promise, anchor, trigger, messages, "omen", "The omen settles into the world"),
        };
    }

    private IReadOnlyList<StateDelta> RealizePromiseMemory(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var worldMemory = State.Memories.Append(
            anchor.Id.Value,
            promise.Text,
            $"promise:{promise.Id}:{trigger}",
            Math.Max(2, promise.Salience + 1),
            shareable: true);
        var existing = anchor.TryGet<MemoryComponent>(out var memory)
            ? memory.Records.ToList()
            : new List<EntityMemoryRecord>();
        existing.Add(new EntityMemoryRecord(
            $"memory_{promise.Id}",
            promise.Text,
            promise.Id,
            trigger,
            Math.Max(2, promise.Salience + 1),
            Shareable: true));
        anchor.Set(new MemoryComponent(existing));

        var message = $"{anchor.Name} remembers something that was not there before.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseMemory",
                worldMemory.Id,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["anchor"] = anchor.Id.Value,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizePromiseThreat(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : State.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin)
            ?? FindOpenAdjacent(State.ControlledEntity.Get<PositionComponent>().Position)
            ?? origin;
        var threatName = PromiseThreatName(promise);
        var threat = _engine.SpawnEntity(
            "promise_threat",
            threatName,
            'D',
            position,
            "empire",
            hp: 8,
            attack: 3,
            tags: new[] { "promise", "threat", "omen" });
        var message = $"{threat.Name} arrives to collect on the promise.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseThreat",
                threat.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["x"] = position.X,
                    ["y"] = position.Y,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizePromiseItem(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : State.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin) ?? origin;
        var itemName = PromiseItemName(promise);
        var item = _itemSystem.BuildItemEntity(itemName, position, quantity: 1);
        item.Name = itemName;
        item.Set(new DescriptionComponent($"This object exists because a promise became concrete: {promise.Text}"));
        State.Entities[item.Id] = item;

        var message = $"{item.Name} appears where the promise can reach it.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseItem",
                item.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["x"] = position.X,
                    ["y"] = position.Y,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizePromiseCanon(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        string canonKind,
        string messagePrefix)
    {
        var canon = State.Canon.Add(
            canonKind,
            anchor.Id.Value,
            promise.Text,
            promise.Text,
            new[] { "promise", promise.Kind, canonKind },
            $"promise:{promise.Id}:{trigger}",
            State.Turn);
        var message = $"{messagePrefix}: {promise.Text}";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseCanon",
                canon.Id,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["anchor"] = anchor.Id.Value,
                    ["kind"] = canonKind,
                    ["trigger"] = trigger,
                }),
        };
    }

    private GridPoint? FindOpenAdjacent(GridPoint origin)
    {
        var offsets = new[]
        {
            new GridPoint(0, -1),
            new GridPoint(1, 0),
            new GridPoint(0, 1),
            new GridPoint(-1, 0),
            new GridPoint(1, -1),
            new GridPoint(1, 1),
            new GridPoint(-1, 1),
            new GridPoint(-1, -1),
        };

        foreach (var offset in offsets)
        {
            var candidate = origin.Translate(offset.X, offset.Y);
            if (CanEnter(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string PromiseThreatName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("collector"))
        {
            return "debt collector";
        }

        if (lower.Contains("soldier") || lower.Contains("empire") || lower.Contains("imperial"))
        {
            return "promised imperial claimant";
        }

        return "promised threat";
    }

    private static string PromiseItemName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("key"))
        {
            return "promised key";
        }

        if (lower.Contains("blade") || lower.Contains("knife") || lower.Contains("sword"))
        {
            return "promised blade";
        }

        if (lower.Contains("pearl"))
        {
            return "promised pearl";
        }

        return "promise token";
    }

    private static bool PromiseTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return true;
        }

        var normalizedTrigger = trigger.Trim().ToLowerInvariant();
        var hints = triggerHint.ToLowerInvariant()
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return hints.Any(hint =>
            hint == normalizedTrigger
            || (normalizedTrigger == "open" && hint is "door" or "opened" or "unlock")
            || (normalizedTrigger == "talk" && hint is "speak" or "name" or "dialogue")
            || (normalizedTrigger == "read" && hint is "notice" or "sign" or "book"));
    }

    private Entity? ResolvePromiseAnchorFromSelectionOrText(string text)
    {
        if (State.SelectedTarget is { } selected)
        {
            var selectedEntity = _engine.EntityAt(selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        var tokens = text.ToLowerInvariant()
            .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return State.Entities.Values
            .Where(entity => entity.Id != State.ControlledEntityId)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Select(entity => new
            {
                Entity = entity,
                Score = PromiseAnchorScore(entity, tokens),
                Distance = entity.TryGet<PositionComponent>(out var position)
                    ? GameEngine.Distance(State.ControlledEntity.Get<PositionComponent>().Position, position.Position)
                    : int.MaxValue,
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Entity.Id.Value)
            .Select(candidate => candidate.Entity)
            .FirstOrDefault();
    }

    private static int PromiseAnchorScore(Entity entity, HashSet<string> tokens)
    {
        var score = 0;
        foreach (var token in entity.Name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tokens.Contains(token))
            {
                score += 3;
            }
        }

        if (entity.TryGet<TagsComponent>(out var tags))
        {
            score += tags.Tags.Count(tag => tokens.Contains(tag));
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            score += fixture.Tags.Count(tag => tokens.Contains(tag));
            if (tokens.Contains(fixture.FixtureType))
            {
                score += 2;
            }
        }

        if (entity.TryGet<ReadableComponent>(out var readable))
        {
            foreach (var token in readable.Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tokens.Contains(token))
                {
                    score += 2;
                }
            }
        }

        return score;
    }

    private static bool CanBindToRegion(WorldPromise promise) =>
        promise.Kind.Equals("prophecy", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("quest", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("threat", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("debt", StringComparison.OrdinalIgnoreCase);

    private static string InferTriggerHint(string text, Entity? anchor)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("read") || anchor?.Has<ReadableComponent>() == true)
        {
            return "read";
        }

        if (lower.Contains("open") || lower.Contains("door") || anchor?.Has<DoorComponent>() == true)
        {
            return "open";
        }

        if (lower.Contains("speak") || lower.Contains("talk") || lower.Contains("name"))
        {
            return "talk";
        }

        return "encounter";
    }

    private static string InferRealizationKind(string kind, string text)
    {
        var lower = $"{kind} {text}".ToLowerInvariant();
        if (lower.Contains("item") || lower.Contains("blade") || lower.Contains("key"))
        {
            return "item";
        }

        if (lower.Contains("enemy") || lower.Contains("collector") || lower.Contains("threat"))
        {
            return "threat";
        }

        if (lower.Contains("quest") || lower.Contains("reward"))
        {
            return "quest";
        }

        if (lower.Contains("remember") || lower.Contains("name"))
        {
            return "memory";
        }

        return kind.Equals("debt", StringComparison.OrdinalIgnoreCase) ? "threat" : "omen";
    }

    private static IReadOnlyList<string> TagsFor(Entity entity)
    {
        var tags = new List<string>();
        if (entity.TryGet<TagsComponent>(out var tagComponent))
        {
            tags.AddRange(tagComponent.Tags);
        }

        if (entity.TryGet<ItemComponent>(out var item))
        {
            tags.AddRange(item.Tags);
            tags.Add(item.Material);
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            tags.AddRange(fixture.Tags);
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag).ToArray();
    }

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > State.Turn;

    private bool CanEnter(GridPoint point) =>
        _engine.InBounds(point)
        && !State.BlockingTerrain.Contains(point)
        && _engine.BlockingEntityAt(point) is null;

    private IReadOnlyList<StateDelta> AdvanceTurn() => _engine.AdvanceTurn();

    private static string NormalizeId(string value, string fallback)
    {
        var cleaned = string.Join(
            '_',
            value.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static int InteractionDistance(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
