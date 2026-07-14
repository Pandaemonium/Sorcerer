using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

/// <summary>
/// <see cref="InteractionSystem"/> the player interaction verbs: give, recruit, bonds, services, request-service, read, examine, and open/doors -- each resolving intent and eligibility, then submitting consequence packets.
/// Split from the interaction system (Phase 0.4); the ctor, the Talk/dialogue interaction
/// core, and shared entity/bond/message helpers stay in the base file. All state changes
/// still go through _engine.ApplyConsequence.
/// </summary>
public sealed partial class InteractionSystem
{
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

        var transaction = GameTransaction.Begin(State);
        var gift = _engine.ApplyConsequence(WorldConsequence.TransferItem(
            "gift",
            State.ControlledEntityId.Value,
            "give",
            key,
            quantity: 1,
            recipientEntityId: target.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: key,
            operation: "giveItem",
            message: $"{target.Name} accepts {key}. The gift becomes part of what they know about you."));
        if (!gift.Applied)
        {
            var failure = gift.Error ?? $"{target.Name} cannot receive {key}.";
            var failureDeltas = gift.Deltas.ToList();
            RollBackGiftTransaction(transaction, failureDeltas, 0, target, key, gift.Deltas, failure);
            return new ActionResult
            {
                Action = "give",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = new[] { failure },
                Deltas = failureDeltas,
            };
        }

        var memoryText = $"{target.Name} accepted {key} from the sorcerer.";
        var memory = _engine.ApplyConsequence(WorldConsequence.RecordMemory(
            "gift",
            target.Id.Value,
            memoryText,
            "gift",
            2,
            shareable: true,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: memoryText,
            operation: "giftMemory",
            details: new Dictionary<string, object?>
            {
                ["item"] = key,
                ["giverId"] = State.ControlledEntityId.Value,
            }));
        var messages = gift.Messages.ToList();
        var deltas = gift.Deltas.Concat(memory.Deltas).ToList();
        if (!memory.Applied)
        {
            var failure = memory.Error ?? $"{target.Name}'s gift memory could not be recorded.";
            RollBackGiftTransaction(transaction, deltas, 0, target, key, memory.Deltas, failure);
            return new ActionResult
            {
                Action = "give",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        transaction.Commit();
        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "give",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    private static void RollBackGiftTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        Entity target,
        string item,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        deltas.AddRange(FailureDiagnostics(failedDeltas));
        var rejectedCount = FailureDiagnostics(failedDeltas).Count;
        deltas.Add(new StateDelta(
            "giftSkipped",
            target.Id.Value,
            $"Gift rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["targetEntityId"] = target.Id.Value,
                ["item"] = item,
                ["failure"] = failure,
                ["rejectedCount"] = rejectedCount,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
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

        return ApplyRecruitment(
            target,
            source: "recruit",
            action: "recruit",
            operationPrefix: "recruit",
            messageOperation: "recruit",
            evidence: $"{target.Name} chose to follow the sorcerer.",
            consumeTurn: true,
            turnBefore,
            extraDetails: null);
    }

    public ActionResult RecruitFromDialogue(string actorId, string provider, string? reason)
    {
        var turnBefore = State.Turn;
        var target = _engine.EntityById(actorId);
        if (target is null)
        {
            return ActionResult.Simple(
                "dialogue_recruit",
                false,
                false,
                turnBefore,
                State.Turn,
                "Dialogue recruit action skipped because the speaker no longer exists.");
        }

        return ApplyRecruitment(
            target,
            source: $"dialogue:{provider}",
            action: "dialogue_recruit",
            operationPrefix: "dialogueRecruit",
            messageOperation: "dialogueRecruit",
            evidence: reason ?? $"{target.Name} chose to follow the sorcerer in dialogue.",
            consumeTurn: false,
            turnBefore,
            extraDetails: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "recruit",
                ["providerReason"] = reason,
            });
    }

    private ActionResult ApplyRecruitment(
        Entity target,
        string source,
        string action,
        string operationPrefix,
        string messageOperation,
        string evidence,
        bool consumeTurn,
        int turnBefore,
        IReadOnlyDictionary<string, object?>? extraDetails)
    {
        var playerSoulId = PlayerSoulId();
        var subjectSoulId = SoulIdFor(target);
        var bond = State.Bonds.TryGet(subjectSoulId, playerSoulId, out var existingBond)
            ? existingBond
            : NeutralBond(subjectSoulId, playerSoulId);
        var recruitScore = bond.Loyalty + bond.Admiration - bond.Resentment;
        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        if (recruitScore < 5 && !bond.Posture.Equals("grateful", StringComparison.OrdinalIgnoreCase))
        {
            var refusalTransaction = GameTransaction.Begin(State);
            var refusalDeltaStart = deltas.Count;
            var refusalMessageStart = messages.Count;
            var refusal = $"{target.Name} listens, but the bond is not strong enough to make a life out of it.";
            var refusalMessage = _engine.ApplyConsequence(WorldConsequence.Message(
                source,
                refusal,
                targetEntityId: target.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: refusal,
                operation: $"{operationPrefix}Refused",
                details: MergeRecruitDetails(extraDetails, ("recruitScore", recruitScore), ("posture", bond.Posture))));
            if (!refusalMessage.Applied)
            {
                var failure = refusalMessage.Error ?? $"{target.Name}'s recruitment refusal could not be recorded.";
                RollBackRecruitmentTransaction(
                    refusalTransaction,
                    deltas,
                    refusalDeltaStart,
                    messages,
                    refusalMessageStart,
                    target,
                    operationPrefix,
                    refusalMessage.Deltas,
                    failure);
                return new ActionResult
                {
                    Action = action,
                    Success = false,
                    ConsumedTurn = false,
                    TurnBefore = turnBefore,
                    TurnAfter = State.Turn,
                    Messages = new[] { failure },
                    Deltas = deltas,
                };
            }

            messages.AddRange(refusalMessage.Messages);
            deltas.AddRange(refusalMessage.Deltas);
            var refusalBond = ApplyBondUpdate(
                target,
                source,
                resentment: 1,
                posture: bond.Posture,
                operation: $"{operationPrefix}Bond");
            if (!refusalBond.Applied)
            {
                var failure = refusalBond.Error ?? $"{target.Name}'s refusal bond could not be recorded.";
                RollBackRecruitmentTransaction(
                    refusalTransaction,
                    deltas,
                    refusalDeltaStart,
                    messages,
                    refusalMessageStart,
                    target,
                    operationPrefix,
                    refusalBond.Deltas,
                    failure);
                return new ActionResult
                {
                    Action = action,
                    Success = false,
                    ConsumedTurn = false,
                    TurnBefore = turnBefore,
                    TurnAfter = State.Turn,
                    Messages = new[] { failure },
                    Deltas = deltas,
                };
            }

            deltas.AddRange(refusalBond.Deltas);
            refusalTransaction.Commit();
            var refusalTurnDeltas = consumeTurn ? AdvanceTurn() : Array.Empty<StateDelta>();
            return new ActionResult
            {
                Action = action,
                Success = false,
                ConsumedTurn = consumeTurn,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = messages.Concat(refusalTurnDeltas.PlayerMessages()).ToArray(),
                Deltas = deltas.Concat(refusalTurnDeltas).ToArray(),
            };
        }

        var transaction = GameTransaction.Begin(State);
        var deltaStart = deltas.Count;
        var messageStart = messages.Count;

        var faction = _engine.ApplyConsequence(WorldConsequence.ChangeFaction(
            source,
            target.Id.Value,
            "player",
            roles: new[] { "follower" },
            preserveMembership: true,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: evidence,
            operation: $"{operationPrefix}Faction",
            details: extraDetails));
        if (!faction.Applied)
        {
            var failure = faction.Error ?? $"{target.Name} cannot change allegiance right now.";
            RollBackRecruitmentTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                target,
                operationPrefix,
                faction.Deltas,
                failure);
            return new ActionResult
            {
                Action = action,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        deltas.AddRange(faction.Deltas);
        var control = _engine.ApplyConsequence(WorldConsequence.UpdateControl(
            source,
            target.Id.Value,
            "ai",
            aiPolicyId: "follower",
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: evidence,
            operation: $"{operationPrefix}Control",
            details: extraDetails));
        if (!control.Applied)
        {
            var failure = control.Error ?? $"{target.Name} cannot follow right now.";
            RollBackRecruitmentTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                target,
                operationPrefix,
                control.Deltas,
                failure);
            return new ActionResult
            {
                Action = action,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        deltas.AddRange(control.Deltas);
        var recruitBond = ApplyBondUpdate(
            target,
            source,
            loyalty: 1,
            admiration: 1,
            posture: "follower",
            operation: $"{operationPrefix}Bond");
        if (!recruitBond.Applied)
        {
            var failure = recruitBond.Error ?? $"{target.Name}'s bond could not become follower-shaped.";
            RollBackRecruitmentTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                target,
                operationPrefix,
                recruitBond.Deltas,
                failure);
            return new ActionResult
            {
                Action = action,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        deltas.AddRange(recruitBond.Deltas);
        var updatedBond = State.Bonds.TryGet(subjectSoulId, playerSoulId, out var recruitedBond)
            ? recruitedBond
            : NeutralBond(subjectSoulId, playerSoulId);
        var memoryText = $"{target.Name} chose to follow the sorcerer.";
        var memory = _engine.ApplyConsequence(WorldConsequence.RecordMemory(
            source,
            target.Id.Value,
            memoryText,
            source,
            3,
            shareable: true,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: evidence,
            operation: $"{operationPrefix}Memory",
            details: extraDetails));
        if (!memory.Applied)
        {
            var failure = memory.Error ?? $"{target.Name}'s recruitment memory could not be recorded.";
            RollBackRecruitmentTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                target,
                operationPrefix,
                memory.Deltas,
                failure);
            return new ActionResult
            {
                Action = action,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        deltas.AddRange(memory.Deltas);
        var message = $"{target.Name} chooses to follow you, carrying old loyalties separately from the new bond.";
        var recruitMessage = _engine.ApplyConsequence(WorldConsequence.Message(
            source,
            message,
            targetEntityId: target.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: evidence,
            operation: messageOperation,
            details: MergeRecruitDetails(
                extraDetails,
                ("posture", updatedBond.Posture),
                ("combatFaction", "player"),
                ("membership", target.TryGet<FactionComponent>(out var membership) ? membership.FactionId : ""))));
        if (!recruitMessage.Applied)
        {
            var failure = recruitMessage.Error ?? $"{target.Name}'s recruitment message could not be recorded.";
            RollBackRecruitmentTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                target,
                operationPrefix,
                recruitMessage.Deltas,
                failure);
            return new ActionResult
            {
                Action = action,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        messages.AddRange(recruitMessage.Messages);
        deltas.AddRange(recruitMessage.Deltas);

        transaction.Commit();
        var turnDeltas = consumeTurn ? AdvanceTurn() : Array.Empty<StateDelta>();
        return new ActionResult
        {
            Action = action,
            Success = true,
            ConsumedTurn = consumeTurn,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    private static void RollBackRecruitmentTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        List<string> messages,
        int messageStart,
        Entity target,
        string operationPrefix,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        RemoveRangeFrom(messages, messageStart);
        deltas.AddRange(FailureDiagnostics(failedDeltas));
        var rejectedCount = FailureDiagnostics(failedDeltas).Count;
        deltas.Add(new StateDelta(
            "recruitmentSkipped",
            target.Id.Value,
            $"Recruitment rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["targetEntityId"] = target.Id.Value,
                ["operationPrefix"] = operationPrefix,
                ["failure"] = failure,
                ["rejectedCount"] = rejectedCount,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private static IReadOnlyDictionary<string, object?> MergeRecruitDetails(
        IReadOnlyDictionary<string, object?>? baseDetails,
        params (string Key, object? Value)[] fields)
    {
        var details = baseDetails is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(baseDetails, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            details[key] = value;
        }

        return details;
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
        var providers = NearbyServiceProviders(targetText, "services").ToArray();
        if (providers.Length == 0)
        {
            return ActionResult.Simple("services", false, false, turn, turn, "No nearby services are being offered.");
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        foreach (var provider in providers)
        {
            deltas.AddRange(_promiseRealizationSystem.RealizeAnchoredPromises(
                provider,
                "services",
                messages,
                alreadyPersistedMessages: messages.ToList()));
        }

        var lines = providers
            .SelectMany(provider => VisibleServices(provider)
                .Select(service => FormatServiceLine(provider, service)))
            .ToArray();
        messages.AddRange(lines.Length == 0 ? new[] { "No nearby services are being offered." } : lines);
        return new ActionResult
        {
            Action = "services",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = messages.ToArray(),
            Deltas = deltas,
        };
    }

    public ActionResult RequestService(string serviceText, string? targetText)
    {
        var turnBefore = State.Turn;
        if (string.IsNullOrWhiteSpace(serviceText))
        {
            return ActionResult.Simple("request", false, false, turnBefore, State.Turn, "Name the service you want.");
        }

        var provider = ResolveServiceProvider(targetText, serviceText, "request");
        if (provider is null)
        {
            return ActionResult.Simple("request", false, false, turnBefore, State.Turn, "No nearby provider offers that service.");
        }

        var messages = new List<string>();
        var deltas = _promiseRealizationSystem.RealizeAnchoredPromises(
            provider,
            "request",
            messages,
            alreadyPersistedMessages: messages.ToList()).ToList();
        var service = FindService(VisibleServices(provider), serviceText);
        if (service is null)
        {
            return new ActionResult
            {
                Action = "request",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = messages.Concat(new[] { $"{provider.Name} is not offering {serviceText}." }).ToArray(),
                Deltas = deltas,
            };
        }

        var applied = _engine.ApplyConsequence(WorldConsequence.RequestService(
            "service",
            provider.Id.Value,
            serviceText,
            State.ControlledEntityId.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: provider.Id.Value,
            evidence: service.Description,
            operation: "requestService",
            message: $"{provider.Name} provides {service.Name}.",
            details: new Dictionary<string, object?>
            {
                ["serviceId"] = service.Id,
                ["serviceName"] = service.Name,
                ["effectKind"] = service.EffectKind,
                ["providerId"] = provider.Id.Value,
            }));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"{provider.Name} cannot complete {service.Name}.";
            return new ActionResult
            {
                Action = "request",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = messages.Concat(new[] { failure }).ToArray(),
                Deltas = deltas.Concat(applied.Deltas).ToArray(),
            };
        }

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "request",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages
                .Concat(applied.Messages)
                .Concat(turnDeltas.PlayerMessages())
                .ToArray(),
            Deltas = deltas.Concat(applied.Deltas).Concat(turnDeltas).ToArray(),
        };
    }

    public ActionResult Read(string? target)
    {
        var turnBefore = State.Turn;
        var entity = ResolveNearbyEntity(target, candidate => candidate.Has<ReadableComponent>(), range: 1);
        if (entity is null)
        {
            var hint = OutOfReachHint(_engine, State, target, candidate => candidate.Has<ReadableComponent>());
            return ActionResult.Simple("read", false, false, turnBefore, State.Turn, hint ?? "There is nothing readable within reach.");
        }

        var readable = entity.Get<ReadableComponent>();
        var body = string.IsNullOrWhiteSpace(readable.TextKey)
            ? $"{readable.Title}: the words hold still just long enough to be understood."
            : readable.TextKey;
        var canon = _engine.ApplyConsequence(WorldConsequence.AddCanon(
            "read",
            "readable",
            entity.Id.Value,
            body,
            readable.Title,
            TagsFor(entity),
            evidence: body,
            operation: "readCanon"));
        var readMessage = _engine.ApplyConsequence(WorldConsequence.Message(
            "read",
            body,
            targetEntityId: entity.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: body,
            reason: "The player read a nearby readable entity.",
            operation: "readMessage",
            details: new Dictionary<string, object?>
            {
                ["readableId"] = entity.Id.Value,
                ["title"] = readable.Title,
            }));
        var queuedJob = _turnSystem.EnqueueBackgroundJob("canon_detail", entity, priority: 3);
        var messages = readMessage.Messages.ToList();
        var alreadyPersistedMessages = readMessage.Messages.Concat(queuedJob.Messages).ToList();
        messages.AddRange(queuedJob.Messages);
        var deltas = readMessage.Deltas
            .Concat(canon.Deltas)
            .Concat(queuedJob.Deltas)
            .Concat(ApplyClaimSeeds(entity, "read"))
            .Concat(_promiseRealizationSystem.RealizeAnchoredPromises(entity, "read", messages, alreadyPersistedMessages))
            .Concat(TeachCharterFormsFrom(entity, messages, alreadyPersistedMessages))
            .ToList();
        deltas.AddRange(PersistUnwrittenMessages(
            messages,
            alreadyPersistedMessages,
            "read",
            "readFallbackMessage",
            entity.Id.Value,
            new Dictionary<string, object?>
            {
                ["readableId"] = entity.Id.Value,
                ["title"] = readable.Title,
            }));

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "read",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    /// <summary>
    /// Charter acquisition through ordinary verbs (docs/CHARTER_MAGIC.md): a readable entity
    /// tagged "teaches_charter:&lt;spellId&gt;" teaches that form to the reading soul the first
    /// time it is read. Content-driven - any authored or generated manual can carry the tag.
    /// </summary>
    private IReadOnlyList<StateDelta> TeachCharterFormsFrom(
        Entity entity,
        List<string> messages,
        List<string> alreadyPersistedMessages)
    {
        const string prefix = "teaches_charter:";
        if (!entity.TryGet<TagsComponent>(out var tags))
        {
            return Array.Empty<StateDelta>();
        }

        var reader = State.ControlledEntity;
        var soulId = reader.TryGet<SoulComponent>(out var soul) ? soul.SoulId : reader.Id.Value;
        var deltas = new List<StateDelta>();
        foreach (var tag in tags.Tags)
        {
            if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var spellId = tag[prefix.Length..].Trim();
            var spell = Sorcerer.Core.Magic.CharterSpellbook.Default.Find(spellId);
            if (spell is null || !State.Souls.LearnCharterSpell(soulId, spell.Id))
            {
                continue;
            }

            var learned = _engine.ApplyConsequence(WorldConsequence.Message(
                "read",
                $"You memorize the charter form printed in the margins: {spell.Name}. ('charter {spell.Id}' casts it.)",
                targetEntityId: entity.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: reader.Id.Value,
                reason: "Reading licensed paraphernalia teaches its charter form.",
                operation: "charterFormLearned",
                details: new Dictionary<string, object?>
                {
                    ["spellId"] = spell.Id,
                    ["soulId"] = soulId,
                }));
            deltas.AddRange(learned.Deltas);
            messages.AddRange(learned.Messages);
            alreadyPersistedMessages.AddRange(learned.Messages);
        }

        return deltas;
    }

    public ActionResult Examine(string? target)
    {
        var entity = ResolveNearbyEntity(target, entity => entity.Id != State.ControlledEntityId, range: 2);
        if (entity is null)
        {
            var hint = OutOfReachHint(_engine, State, target, entity => entity.Id != State.ControlledEntityId);
            return ActionResult.Simple("examine", false, false, State.Turn, State.Turn, hint ?? "There is nothing close enough to examine.");
        }

        var messages = DescribeEntity(entity).ToList();
        var originalMessageCount = messages.Count;
        var alreadyPersistedMessages = new List<string>();
        var deltas = ApplyClaimSeeds(entity, "inspect").ToList();
        deltas.AddRange(_promiseRealizationSystem.RealizeAnchoredPromises(entity, "inspect", messages, alreadyPersistedMessages));
        deltas.AddRange(PersistUnwrittenMessages(
            messages.Skip(originalMessageCount),
            alreadyPersistedMessages,
            "examine",
            "examineFallbackMessage",
            entity.Id.Value,
            new Dictionary<string, object?>
            {
                ["examinedId"] = entity.Id.Value,
                ["examinedName"] = entity.Name,
            }));

        var queuedJob = _turnSystem.EnqueueBackgroundJob("entity_detail", entity, priority: 2);
        messages.AddRange(queuedJob.Messages);
        alreadyPersistedMessages.AddRange(queuedJob.Messages);

        return new ActionResult
        {
            Action = "examine",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = State.Turn,
            TurnAfter = State.Turn,
            Messages = messages.ToArray(),
            Deltas = deltas.Concat(queuedJob.Deltas).ToArray(),
        };
    }

    public ActionResult Open(string? target)
    {
        var turnBefore = State.Turn;
        var door = ResolveNearbyEntity(target, entity => entity.Has<DoorComponent>(), range: 1);
        if (door is null)
        {
            var hint = OutOfReachHint(_engine, State, target, entity => entity.Has<DoorComponent>());
            return ActionResult.Simple("open", false, false, turnBefore, State.Turn, hint ?? "There is nothing here you can open.");
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

        var messages = new List<string>();
        var alreadyPersistedMessages = new List<string>();
        var deltas = _promiseRealizationSystem.RealizeAnchoredPromises(
            door,
            "open",
            messages,
            alreadyPersistedMessages).ToList();
        doorComponent = door.Get<DoorComponent>();

        if (doorComponent.IsOpen)
        {
            if (deltas.Count > 0)
            {
                ResolveDoorConsequences(door, messages, deltas, alreadyPersistedMessages);
                deltas.AddRange(PersistUnwrittenMessages(
                    messages,
                    alreadyPersistedMessages,
                    "open",
                    "openFallbackMessage",
                    door.Id.Value,
                    new Dictionary<string, object?>
                    {
                        ["doorId"] = door.Id.Value,
                        ["actorId"] = actor.Id.Value,
                    }));

                var earlyTurnDeltas = context.ConsumeTurn ? AdvanceTurn() : Array.Empty<StateDelta>();
                return new ActionResult
                {
                    Action = context.ResultAction,
                    Success = true,
                    ConsumedTurn = context.ConsumeTurn,
                    TurnBefore = turnBefore,
                    TurnAfter = State.Turn,
                    Messages = messages.Concat(earlyTurnDeltas.PlayerMessages()).ToArray(),
                    Deltas = deltas.Concat(earlyTurnDeltas).ToArray(),
                };
            }

            return ActionResult.Simple(context.ResultAction, false, false, turnBefore, State.Turn, $"{door.Name} is already open.");
        }

        if (!string.IsNullOrWhiteSpace(doorComponent.KeyId)
            && !_itemSystem.IsCarrying(actor, doorComponent.KeyId))
        {
            var lockedText = $"{door.Name} is locked.";
            var lockedMessage = _engine.ApplyConsequence(WorldConsequence.Message(
                context.Source,
                lockedText,
                targetEntityId: door.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: actor.Id.Value,
                evidence: lockedText,
                reason: "An actor tried to open a locked door without the matching key.",
                operation: $"{context.DeltaOperation}LockedMessage",
                details: new Dictionary<string, object?>
                {
                    ["doorId"] = door.Id.Value,
                    ["actorId"] = actor.Id.Value,
                    ["keyId"] = doorComponent.KeyId,
                    ["playerVisible"] = true,
                }));
            messages.AddRange(lockedMessage.Messages);
            deltas.AddRange(lockedMessage.Deltas);
            return new ActionResult
            {
                Action = context.ResultAction,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = messages.ToArray(),
                Deltas = deltas,
            };
        }

        var summary = actor.Id == State.ControlledEntityId
            ? $"You open {door.Name}."
            : $"{actor.Name} opens {door.Name}.";
        var details = new Dictionary<string, object?>
        {
            ["source"] = context.Source,
            ["actorId"] = actor.Id.Value,
            ["beneficiaryId"] = State.ControlledEntityId.Value,
        };
        if (!string.IsNullOrWhiteSpace(context.Provider))
        {
            details["provider"] = context.Provider;
        }

        var opened = _engine.ApplyConsequence(WorldConsequence.OpenOrUnlock(
            context.Source,
            door.Id.Value,
            actor.Id.Value,
            unlock: true,
            open: true,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: summary,
            operation: context.DeltaOperation,
            emitMessage: true,
            message: summary,
            details: details));
        if (!opened.Applied)
        {
            var failure = opened.Error ?? $"{door.Name} does not open.";
            return new ActionResult
            {
                Action = context.ResultAction,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = State.Turn,
                Messages = messages.Concat(new[] { failure }).ToArray(),
                Deltas = deltas.Concat(opened.Deltas).ToArray(),
            };
        }

        messages.AddRange(opened.Messages);
        deltas.AddRange(opened.Deltas);

        alreadyPersistedMessages.AddRange(opened.Messages);
        ResolveDoorConsequences(door, messages, deltas, alreadyPersistedMessages);
        deltas.AddRange(PersistUnwrittenMessages(
            messages,
            alreadyPersistedMessages,
            "open",
            "openFallbackMessage",
            door.Id.Value,
            new Dictionary<string, object?>
            {
                ["doorId"] = door.Id.Value,
                ["actorId"] = actor.Id.Value,
            }));

        var turnDeltas = context.ConsumeTurn ? AdvanceTurn() : Array.Empty<StateDelta>();
        return new ActionResult
        {
            Action = context.ResultAction,
            Success = true,
            ConsumedTurn = context.ConsumeTurn,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }
}
