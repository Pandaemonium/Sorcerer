using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class InteractionSystem
{
    private readonly GameEngine _engine;
    private readonly ItemSystem _itemSystem;
    private readonly PromiseRealizationSystem _promiseRealizationSystem;
    private readonly TurnSystem _turnSystem;

    public InteractionSystem(GameEngine engine, ItemSystem itemSystem, TurnSystem turnSystem)
    {
        _engine = engine;
        _itemSystem = itemSystem;
        _promiseRealizationSystem = new PromiseRealizationSystem(engine.State);
        _turnSystem = turnSystem;
    }

    private GameState State => _engine.State;

    public ActionResult Talk(string text)
    {
        var preparation = PrepareDialogue(text);
        if (preparation.ImmediateResult is not null)
        {
            return preparation.ImmediateResult;
        }

        var turn = preparation.Turn!;
        var target = State.Entities.TryGetValue(EntityId.Create(turn.SpeakerId), out var entity)
            ? entity
            : null;
        if (target is null)
        {
            return ActionResult.Simple("talk", false, false, turn.TurnBefore, State.Turn, "No one nearby is ready to talk.");
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        ResolveDialogueIntent(target, text, messages, deltas);
        return CompleteDialogue(turn, messages, deltas, generated: false, provider: "deterministic", rawText: null, delivery: null, intent: null);
    }

    public DialoguePreparation PrepareDialogue(string text)
    {
        var turnBefore = State.Turn;
        if (TryRouteDialogueShortcut(text, out var shortcut))
        {
            return new DialoguePreparation(null, shortcut);
        }

        var target = ResolveNearbyEntity(
            text,
            entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
            range: 2);
        target ??= ResolveNearbyActorMention(text);
        if (target is null)
        {
            return new DialoguePreparation(
                null,
                ActionResult.Simple("talk", false, false, turnBefore, State.Turn, "No one nearby is ready to talk."));
        }

        var actor = target.TryGet<ActorComponent>(out var actorComponent) ? actorComponent : null;
        var profile = target.TryGet<ProfileComponent>(out var profileComponent)
            ? $"{profileComponent.PublicName}: {profileComponent.Appearance}"
            : null;
        var bond = State.Bonds.TryGet(SoulIdFor(target), PlayerSoulId(), out var bondRecord)
            ? BondSummary(bondRecord)
            : null;
        return new DialoguePreparation(
            new PreparedDialogueTurn(
                turnBefore,
                text,
                target.Id.Value,
                target.Name,
                TagsFor(target).ToArray(),
                PlayerSoulId(),
                _engine.IsHostile(target, State.ControlledEntity),
                profile,
                actor?.Faction,
                bond),
            null);
    }

    public ActionResult ApplyGeneratedDialogue(
        PreparedDialogueTurn turn,
        string spokenText,
        string provider,
        string? rawText,
        string? delivery,
        string? intent)
    {
        var target = State.Entities.TryGetValue(EntityId.Create(turn.SpeakerId), out var entity)
            ? entity
            : null;
        if (target is null)
        {
            return new ActionResult
            {
                Action = "talk",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turn.TurnBefore,
                TurnAfter = State.Turn,
                TechnicalFailure = true,
                Messages = new[] { "Dialogue target disappeared before the reply could resolve." },
                Deltas = new[]
                {
                    new StateDelta(
                        "dialogueProviderFailed",
                        turn.SpeakerId,
                        "Dialogue target disappeared before the reply could resolve.",
                        new Dictionary<string, object?>
                        {
                            ["provider"] = provider,
                        }),
                },
            };
        }

        return CompleteDialogue(
            turn,
            new List<string> { spokenText },
            new List<StateDelta>(),
            generated: true,
            provider,
            rawText,
            delivery,
            intent);
    }

    private ActionResult CompleteDialogue(
        PreparedDialogueTurn turn,
        List<string> messages,
        List<StateDelta> deltas,
        bool generated,
        string provider,
        string? rawText,
        string? delivery,
        string? intent)
    {
        var target = State.Entities[EntityId.Create(turn.SpeakerId)];
        var dialogueLines = messages.ToArray();
        deltas.Add(new StateDelta(
            "dialogue",
            turn.SpeakerId,
            dialogueLines.FirstOrDefault() ?? $"{target.Name} answers.",
            new Dictionary<string, object?>
            {
                ["speakerId"] = turn.SpeakerId,
                ["speakerName"] = turn.SpeakerName,
                ["speakerTags"] = turn.SpeakerTags.ToArray(),
                ["listenerSoulId"] = turn.ListenerSoulId,
                ["playerText"] = turn.PlayerText,
                ["lines"] = dialogueLines,
                ["generated"] = generated,
                ["provider"] = provider,
                ["rawText"] = rawText,
                ["delivery"] = delivery,
                ["intent"] = intent,
            }));
        deltas.AddRange(_promiseRealizationSystem.RealizeAnchoredPromises(target, "talk", messages));
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
            TurnBefore = turn.TurnBefore,
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
        AddEntityMemory(target, $"{target.Name} accepted {key} from the sorcerer.", "gift", 2);
        State.Memories.Append(
            target.Id.Value,
            $"{target.Name} accepted {key} from the sorcerer.",
            "gift",
            2,
            shareable: true);
        var messages = new List<string>
        {
            $"{target.Name} accepts {key}. The gift becomes part of what they know about you.",
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
                    ["memorySource"] = "gift",
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

    public ActionResult Services(string? targetText)
    {
        var turn = State.Turn;
        var providers = NearbyServiceProviders(targetText).ToArray();
        if (providers.Length == 0)
        {
            return ActionResult.Simple("services", false, false, turn, turn, "No nearby services are being offered.");
        }

        var lines = providers
            .SelectMany(provider => VisibleServices(provider)
                .Select(service => FormatServiceLine(provider, service)))
            .ToArray();
        return ActionResult.Simple(
            "services",
            true,
            false,
            turn,
            turn,
            lines.Length == 0 ? new[] { "No nearby services are being offered." } : lines);
    }

    public ActionResult RequestService(string serviceText, string? targetText)
    {
        var turnBefore = State.Turn;
        if (string.IsNullOrWhiteSpace(serviceText))
        {
            return ActionResult.Simple("request", false, false, turnBefore, State.Turn, "Name the service you want.");
        }

        var provider = ResolveServiceProvider(targetText, serviceText);
        if (provider is null)
        {
            return ActionResult.Simple("request", false, false, turnBefore, State.Turn, "No nearby provider offers that service.");
        }

        var service = FindService(VisibleServices(provider), serviceText);
        if (service is null)
        {
            return ActionResult.Simple("request", false, false, turnBefore, State.Turn, $"{provider.Name} is not offering {serviceText}.");
        }

        var inventory = State.ControlledEntity.Get<InventoryComponent>();
        var costProblem = ServiceCostProblem(inventory, service);
        if (costProblem is not null)
        {
            return ActionResult.Simple("request", false, false, turnBefore, State.Turn, costProblem);
        }

        var applied = ApplyServiceEffect(provider, service);
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"{provider.Name} cannot complete that service here.";
            return new ActionResult
            {
                Action = "request",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = new[] { failure },
                Deltas = applied.Deltas,
            };
        }

        PayServiceCost(inventory, service);
        var serviceMessage = $"{provider.Name} provides {service.Name}.";
        State.AddMessage(serviceMessage);
        var deltas = new List<StateDelta>
        {
            new(
                "requestService",
                provider.Id.Value,
                serviceMessage,
                new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                    ["effectKind"] = service.EffectKind,
                    ["goldCost"] = service.GoldCost,
                    ["itemCost"] = service.ItemCost,
                }),
        };
        deltas.AddRange(applied.Deltas);

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "request",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = new[] { serviceMessage }.Concat(applied.Messages).Concat(turnDeltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
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
        var deltas = _promiseRealizationSystem.RealizeAnchoredPromises(entity, "read", messages);
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

        var messages = DescribeEntity(entity).ToList();
        var originalMessageCount = messages.Count;
        var deltas = _promiseRealizationSystem.RealizeAnchoredPromises(entity, "inspect", messages);
        foreach (var line in messages.Skip(originalMessageCount))
        {
            State.AddMessage(line);
        }

        _turnSystem.EnqueueBackgroundJob("entity_detail", entity, priority: 2);
        return new ActionResult
        {
            Action = "examine",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = State.Turn,
            TurnAfter = State.Turn,
            Messages = messages.ToArray(),
            Deltas = deltas,
        };
    }

    public ActionResult Open(string? target)
    {
        var turnBefore = State.Turn;
        var door = ResolveNearbyEntity(target, entity => entity.Has<DoorComponent>(), range: 1);
        if (door is null)
        {
            return ActionResult.Simple("open", false, false, turnBefore, State.Turn, "There is nothing here you can open.");
        }

        return OpenDoor(State.ControlledEntity, door, WorldActionContext.PlayerCommand("open"));
    }

    public ActionResult OpenDoor(Entity actor, Entity door, WorldActionContext context)
    {
        var turnBefore = State.Turn;
        if (!CanReach(actor, door, range: 1))
        {
            return ActionResult.Simple(
                context.ResultAction,
                false,
                false,
                turnBefore,
                State.Turn,
                $"{actor.Name} cannot reach {door.Name}.");
        }

        if (!door.TryGet<DoorComponent>(out var doorComponent))
        {
            return ActionResult.Simple(
                context.ResultAction,
                false,
                false,
                turnBefore,
                State.Turn,
                $"{door.Name} is not something that opens like a door.");
        }

        if (doorComponent.IsOpen)
        {
            return ActionResult.Simple(
                context.ResultAction,
                false,
                false,
                turnBefore,
                State.Turn,
                $"{door.Name} is already open.");
        }

        if (!string.IsNullOrWhiteSpace(doorComponent.KeyId)
            && !_itemSystem.IsCarrying(actor, doorComponent.KeyId))
        {
            return ActionResult.Simple(
                context.ResultAction,
                false,
                false,
                turnBefore,
                State.Turn,
                $"{door.Name} is locked.");
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

        var summary = actor.Id == State.ControlledEntityId
            ? $"You open {door.Name}."
            : $"{actor.Name} opens {door.Name}.";
        var details = new Dictionary<string, object?>
        {
            ["open"] = true,
            ["source"] = context.Source,
            ["actorId"] = actor.Id.Value,
        };
        if (!string.IsNullOrWhiteSpace(context.Provider))
        {
            details["provider"] = context.Provider;
        }

        var messages = new List<string> { summary };
        var deltas = new List<StateDelta>
        {
            new(
                context.DeltaOperation,
                door.Id.Value,
                messages[0],
                details),
        };

        ResolveDoorConsequences(door, messages, deltas);
        foreach (var message in messages)
        {
            State.AddMessage(message);
        }

        var turnDeltas = context.ConsumeTurn ? AdvanceTurn() : Array.Empty<StateDelta>();
        return new ActionResult
        {
            Action = context.ResultAction,
            Success = true,
            ConsumedTurn = context.ConsumeTurn,
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

    private static bool CanReach(Entity actor, Entity target, int range) =>
        actor.TryGet<PositionComponent>(out var actorPosition)
        && target.TryGet<PositionComponent>(out var targetPosition)
        && InteractionDistance(actorPosition.Position, targetPosition.Position) <= range;

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

        var line = DialogueLine(target);
        messages.Add(line);
    }

    private IEnumerable<Entity> NearbyServiceProviders(string? targetText)
    {
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            var provider = ResolveNearbyEntity(targetText, entity => entity.Has<ServiceComponent>(), range: 2);
            return provider is null ? Array.Empty<Entity>() : new[] { provider };
        }

        var origin = State.ControlledEntity.Get<PositionComponent>().Position;
        return State.Entities.Values
            .Where(entity => entity.Has<ServiceComponent>())
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.Distance(origin, position.Position) <= 2)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? GameEngine.Distance(origin, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value)
            .ToArray();
    }

    private Entity? ResolveServiceProvider(string? targetText, string serviceText)
    {
        var providers = NearbyServiceProviders(targetText);
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            return providers.FirstOrDefault();
        }

        return providers.FirstOrDefault(provider => FindService(VisibleServices(provider), serviceText) is not null);
    }

    private static IEnumerable<ServiceOffer> VisibleServices(Entity provider) =>
        provider.TryGet<ServiceComponent>(out var services)
            ? services.Offers.Where(service => service.Revealed)
            : Array.Empty<ServiceOffer>();

    private static ServiceOffer? FindService(IEnumerable<ServiceOffer> services, string serviceText)
    {
        var normalized = serviceText.Trim();
        return services.FirstOrDefault(service =>
            service.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || service.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || service.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(service.Name, StringComparison.OrdinalIgnoreCase)
            || service.Tags?.Any(tag => tag.Equals(normalized, StringComparison.OrdinalIgnoreCase)) == true);
    }

    private static string FormatServiceLine(Entity provider, ServiceOffer service)
    {
        var costs = new List<string>();
        if (service.GoldCost > 0)
        {
            costs.Add($"{service.GoldCost} gold");
        }

        if (!string.IsNullOrWhiteSpace(service.ItemCost))
        {
            costs.Add(service.ItemCost);
        }

        var cost = costs.Count == 0 ? "no listed price" : string.Join(" and ", costs);
        return $"{provider.Name} offers {service.Name}: {service.Description} ({cost}).";
    }

    private string? ServiceCostProblem(InventoryComponent inventory, ServiceOffer service)
    {
        if (service.GoldCost > 0)
        {
            inventory.Items.TryGetValue("gold", out var gold);
            if (gold < service.GoldCost)
            {
                return $"You need {service.GoldCost} gold for {service.Name}.";
            }
        }

        if (!string.IsNullOrWhiteSpace(service.ItemCost))
        {
            var itemKey = FindInventoryKey(inventory, service.ItemCost);
            if (itemKey is null)
            {
                return $"You need {service.ItemCost} for {service.Name}.";
            }

            if (inventory.TreasuredItems.Contains(itemKey))
            {
                return $"{itemKey} is protected; unprotect it before offering it.";
            }
        }

        return null;
    }

    private void PayServiceCost(InventoryComponent inventory, ServiceOffer service)
    {
        if (service.GoldCost > 0)
        {
            inventory.Items.TryGetValue("gold", out var gold);
            var remaining = gold - service.GoldCost;
            if (remaining <= 0)
            {
                inventory.Items.Remove("gold");
                inventory.TreasuredItems.Remove("gold");
            }
            else
            {
                inventory.Items["gold"] = remaining;
            }
        }

        if (!string.IsNullOrWhiteSpace(service.ItemCost))
        {
            var itemKey = FindInventoryKey(inventory, service.ItemCost);
            if (itemKey is not null)
            {
                DecrementInventory(inventory, itemKey);
            }
        }
    }

    private WorldConsequenceApplyResult ApplyServiceEffect(Entity provider, ServiceOffer service)
    {
        var effect = NormalizeServiceEffect(service.EffectKind);
        if (effect is "open_or_unlock" or "unlock_or_open" or "ward_breaking")
        {
            var door = ResolveServiceDoor(service);
            if (door is null)
            {
                return WorldConsequenceApplyResult.Empty("There is no nearby door for that service.");
            }

            return _engine.ApplyConsequence(WorldConsequence.OpenOrUnlock(
                "service",
                door.Id.Value,
                actorId: provider.Id.Value,
                unlock: true,
                open: true,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: provider.Id.Value,
                evidence: service.Description,
                operation: "serviceOpenOrUnlock"));
        }

        if (effect is "create_route" or "escape_route" or "reveal_route")
        {
            return _engine.ApplyConsequence(WorldConsequence.CreateRoute(
                "service",
                provider.Id.Value,
                string.IsNullOrWhiteSpace(service.TargetHint) ? service.Name : service.TargetHint,
                service.Description,
                effect,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: provider.Id.Value,
                evidence: service.Description,
                operation: "serviceCreateRoute"));
        }

        return _engine.ApplyConsequence(WorldConsequence.RecordMemory(
            "service",
            provider.Id.Value,
            $"{provider.Name} provided {service.Name}: {service.Description}",
            "service",
            2,
            shareable: true,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: provider.Id.Value,
            operation: "serviceMemory"));
    }

    private Entity? ResolveServiceDoor(ServiceOffer service)
    {
        var target = FirstNonBlank(service.TargetHint, service.Name);
        return ResolveNearbyEntity(target, entity => entity.Has<DoorComponent>(), range: 2)
            ?? ResolveNearbyEntity(null, entity => entity.Has<DoorComponent>(), range: 2);
    }

    private static string NormalizeServiceEffect(string effect)
    {
        var normalized = string.Join(
            "_",
            effect.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "record_memory" : normalized;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

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
        deltas.AddRange(_promiseRealizationSystem.RealizeAnchoredPromises(door, "open", messages));

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
        if (lower.Contains("route")
            || lower.Contains("passage")
            || lower.Contains("hidden path")
            || lower.Contains("escape")
            || lower.Contains("drain")
            || lower.Contains("tunnel")
            || lower.Contains("grate")
            || lower.Contains("hidden exit"))
        {
            return "escape_route";
        }

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
