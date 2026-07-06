using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
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
        _promiseRealizationSystem = new PromiseRealizationSystem(engine.State, engine);
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
            var hint = OutOfReachHint(_engine, State, turn.SpeakerId, entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>());
            return ActionResult.Simple("talk", false, false, turn.TurnBefore, State.Turn, hint ?? "No one nearby is ready to talk.");
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        ResolveDialogueIntent(target, turn.PlayerText, messages, deltas);
        return CompleteDialogue(turn, messages, deltas, generated: false, provider: "deterministic", rawText: null, delivery: null, intent: null);
    }

    public DialoguePreparation PrepareDialogue(string text)
    {
        var turnBefore = State.Turn;
        Entity? target;
        if (string.IsNullOrWhiteSpace(text) && State.SelectedTarget is null)
        {
            var candidates = NearbyCandidates(
                entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
                range: 2);
            if (candidates.Count > 1)
            {
                var names = string.Join(", ", candidates.Select(entity => entity.Name));
                return new DialoguePreparation(
                    null,
                    ActionResult.Simple(
                        "talk",
                        false,
                        false,
                        turnBefore,
                        State.Turn,
                        $"Who do you want to talk to? Nearby: {names}. Try \"talk Lio\" or select someone first."));
            }

            target = candidates.FirstOrDefault();
        }
        else
        {
            target = ResolveNearbyEntity(
                text,
                entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
                range: 2);
        }

        target ??= ResolveNearbyActorMention(text);
        if (target is null)
        {
            var hint = OutOfReachHint(_engine, State, text, entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>());
            return new DialoguePreparation(
                null,
                ActionResult.Simple("talk", false, false, turnBefore, State.Turn, hint ?? "No one nearby is ready to talk."));
        }

        var actor = target.TryGet<ActorComponent>(out var actorComponent) ? actorComponent : null;
        var profile = target.TryGet<ProfileComponent>(out var profileComponent)
            ? $"{profileComponent.PublicName}: {profileComponent.Appearance}"
            : null;
        var bond = State.Bonds.TryGet(SoulIdFor(target), PlayerSoulId(), out var bondRecord)
            ? BondSummary(bondRecord)
            : null;
        var want = target.TryGet<WantComponent>(out var wantComponent)
            ? WantSummary(wantComponent)
            : null;
        return new DialoguePreparation(
            new PreparedDialogueTurn(
                turnBefore,
                NormalizeDialoguePlayerText(text, target),
                target.Id.Value,
                target.Name,
                TagsFor(target).ToArray(),
                PlayerSoulId(),
                _engine.IsHostile(target, State.ControlledEntity),
                profile,
                actor?.Faction,
                bond,
                want),
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
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
        // Echo the player's own spoken line into the log before the reply, so a conversation reads
        // as a back-and-forth rather than only the NPC's half (message-log immersion pass).
        if (!string.IsNullOrWhiteSpace(turn.PlayerText))
        {
            var playerLine = _engine.ApplyConsequence(WorldConsequence.Message(
                "player_command",
                $"You say, \"{turn.PlayerText.Trim()}\"",
                targetEntityId: State.ControlledEntityId.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: turn.PlayerText,
                reason: "The player's spoken line, echoed into the log before the reply.",
                operation: "playerSpeech",
                details: new Dictionary<string, object?>
                {
                    ["playerVisible"] = true,
                    ["speakerId"] = State.ControlledEntityId.Value,
                }));
            deltas.AddRange(playerLine.Deltas);
        }

        for (var index = 0; index < dialogueLines.Length; index++)
        {
            var line = dialogueLines[index];
            var applied = _engine.ApplyConsequence(WorldConsequence.Message(
                generated ? $"dialogue:{provider}" : "dialogue",
                line,
                targetEntityId: turn.SpeakerId,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: turn.SpeakerId,
                evidence: turn.PlayerText,
                reason: generated ? "Generated dialogue produced a spoken line." : "Deterministic dialogue produced a spoken line.",
                operation: "dialogueMessage",
                details: new Dictionary<string, object?>
                {
                    ["speakerId"] = turn.SpeakerId,
                    ["speakerName"] = turn.SpeakerName,
                    ["speakerTags"] = turn.SpeakerTags.ToArray(),
                    ["listenerSoulId"] = turn.ListenerSoulId,
                    ["playerText"] = turn.PlayerText,
                    ["lineIndex"] = index,
                    ["generated"] = generated,
                    ["provider"] = provider,
                    ["delivery"] = delivery,
                    ["intent"] = intent,
                }));
            deltas.AddRange(applied.Deltas);
        }

        if (!generated && dialogueLines.Length > 0)
        {
            var spokenText = string.Join(" ", dialogueLines);
            var memoryText = $"{turn.SpeakerName} spoke with the sorcerer. Player: {turn.PlayerText} Reply: {spokenText}";
            var memory = _engine.ApplyConsequence(WorldConsequence.RecordMemory(
                "dialogue_exchange",
                target.Id.Value,
                memoryText,
                "conversation",
                2,
                shareable: false,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: turn.PlayerText,
                reason: "Deterministic dialogue exchange.",
                operation: "dialogueExchangeMemory",
                details: new Dictionary<string, object?>
                {
                    ["speakerId"] = turn.SpeakerId,
                    ["playerText"] = turn.PlayerText,
                    ["spokenText"] = spokenText,
                }));
            deltas.AddRange(memory.Deltas);
        }

        var alreadyPersistedMessages = messages.ToList();
        deltas.AddRange(_promiseRealizationSystem.RealizeAnchoredPromises(target, "talk", messages, alreadyPersistedMessages));
        deltas.AddRange(PersistUnwrittenMessages(
            messages,
            alreadyPersistedMessages,
            "dialogue",
            "dialogueFallbackMessage",
            target.Id.Value,
            new Dictionary<string, object?>
            {
                ["speakerId"] = turn.SpeakerId,
                ["speakerName"] = turn.SpeakerName,
                ["generated"] = generated,
                ["provider"] = provider,
            }));

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "talk",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turn.TurnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
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

    // Names the nearest visible-to-the-player candidate matching the same predicate/target used
    // by an interaction that just failed for being out of range, so the failure message can say
    // where the thing actually is instead of a bare "nothing here." Perception-bound (only
    // entities in PerceptionSystem's current visible set are candidates), so a hidden entity is
    // never named here even if it happens to match. Shared (public static, taking the engine
    // explicitly) because ItemSystem's Pickup lives in a different class but wants the same hint.
    public static string? OutOfReachHint(GameEngine engine, GameState state, string? target, Func<Entity, bool> predicate)
    {
        if (!state.ControlledEntity.TryGet<PositionComponent>(out var originPosition))
        {
            return null;
        }

        var origin = originPosition.Position;
        var visibleIds = engine.Perception().VisibleEntityIds;
        var candidates = state.Entities.Values
            .Where(entity => entity.Id != state.ControlledEntityId)
            .Where(predicate)
            .Where(entity => visibleIds.Contains(entity.Id))
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(target))
        {
            var normalizedTarget = target.Trim();
            var normalizedSearchText = NormalizeSearchText(normalizedTarget);
            candidates = candidates
                .Where(entity =>
                    entity.Id.Value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    || entity.Name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    // Short target words (e.g. "notice", "the door"): does the entity's name
                    // contain the search text?
                    || entity.Name.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    // Conversational mentions (e.g. "Lio, do you trust me?"): does the search
                    // text mention one of the entity's name tokens?
                    || EntityNameTokens(entity).Any(token => normalizedSearchText.Contains(token, StringComparison.OrdinalIgnoreCase))
                    || (entity.TryGet<TagsComponent>(out var tags)
                        && tags.Tags.Any(tag => tag.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))))
                .ToArray();
        }

        var nearest = candidates
            .Select(entity => new { Entity = entity, Position = entity.Get<PositionComponent>().Position })
            .OrderBy(match => GameEngine.Distance(origin, match.Position))
            .ThenBy(match => match.Entity.Id.Value)
            .FirstOrDefault();
        if (nearest is null)
        {
            return null;
        }

        var distance = GameEngine.Distance(origin, nearest.Position);
        var direction = CompassDirection(origin, nearest.Position);
        var tiles = distance == 1 ? "tile" : "tiles";
        return $"{nearest.Entity.Name} is out of reach - {distance} {tiles} {direction}.";
    }

    private static string CompassDirection(GridPoint from, GridPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var vertical = dy < 0 ? "north" : dy > 0 ? "south" : "";
        var horizontal = dx < 0 ? "west" : dx > 0 ? "east" : "";
        if (vertical.Length > 0 && horizontal.Length > 0)
        {
            return $"{vertical}{horizontal}";
        }

        return vertical.Length > 0 ? vertical : horizontal.Length > 0 ? horizontal : "here";
    }

    private static bool CanReach(Entity actor, Entity target, int range) =>
        actor.TryGet<PositionComponent>(out var actorPosition)
        && target.TryGet<PositionComponent>(out var targetPosition)
        && InteractionDistance(actorPosition.Position, targetPosition.Position) <= range;

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

    private static string NormalizeDialoguePlayerText(string text, Entity target) =>
        IsTargetOnlyDialogueText(text, target)
            ? "I approach and wait for you to speak."
            : text.Trim();

    private static bool IsTargetOnlyDialogueText(string text, Entity target)
    {
        var normalized = NormalizeDialogueMatchText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        IEnumerable<string> candidates = new[] { target.Id.Value, target.Name }
            .Concat(EntityNameTokens(target));
        if (target.TryGet<ProfileComponent>(out var profile))
        {
            candidates = candidates.Append(profile.PublicName);
        }

        if (target.TryGet<TagsComponent>(out var tags))
        {
            candidates = candidates.Concat(tags.Tags);
        }

        return candidates
            .Select(NormalizeDialogueMatchText)
            .Any(candidate => candidate.Length > 0
                && candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase));
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

    private static string NormalizeDialogueMatchText(string text) =>
        string.Join(' ', NormalizeSearchText(text).Split(' ', StringSplitOptions.RemoveEmptyEntries));

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
            var transaction = GameTransaction.Begin(State);
            var deltaStart = deltas.Count;
            var messageStart = messages.Count;
            var bond = ApplyBondUpdate(
                target,
                "dialogue:threat",
                fear: 2,
                resentment: 1,
                posture: "afraid",
                operation: "bondShift");
            if (!bond.Applied)
            {
                RollBackThreatDialogueTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    target,
                    bond.Deltas,
                    bond.Error ?? "threat_bond_rejected");
                return;
            }

            var message = $"{target.Name} hears the threat and will not forget it.";
            deltas.AddRange(bond.Deltas);
            var memory = _engine.ApplyConsequence(WorldConsequence.RecordMemory(
                "dialogue:threat",
                target.Id.Value,
                message,
                "dialogue:threat",
                2,
                shareable: true,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: text,
                operation: "threatMemory",
                details: new Dictionary<string, object?>
                {
                    ["requireOwnerEntity"] = true,
                }));
            if (!memory.Applied)
            {
                RollBackThreatDialogueTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    target,
                    memory.Deltas,
                    memory.Error ?? "threat_memory_rejected");
                return;
            }

            messages.Add(message);
            deltas.AddRange(memory.Deltas);
            transaction.Commit();
            return;
        }

        var line = DialogueLine(target, deltas);
        messages.Add(line);
    }

    private static void RollBackThreatDialogueTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        List<string> messages,
        int messageStart,
        Entity target,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        RemoveRangeFrom(messages, messageStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        deltas.Add(new StateDelta(
            "threatDialogueSkipped",
            target.Id.Value,
            $"Threat dialogue rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["targetEntityId"] = target.Id.Value,
                ["failure"] = failure,
                ["rejectedCount"] = diagnostics.Count,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private IEnumerable<Entity> NearbyServiceProviders(
        string? targetText,
        string trigger,
        string? serviceText = null)
    {
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            var provider = ResolveNearbyEntity(
                targetText,
                entity => entity.Has<ServiceComponent>() || HasServicePromise(entity, trigger, serviceText),
                range: 2);
            return provider is null ? Array.Empty<Entity>() : new[] { provider };
        }

        var origin = State.ControlledEntity.Get<PositionComponent>().Position;
        return State.Entities.Values
            .Where(entity => entity.Has<ServiceComponent>() || HasServicePromise(entity, trigger, serviceText))
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.Distance(origin, position.Position) <= 2)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? GameEngine.Distance(origin, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value)
            .ToArray();
    }

    private Entity? ResolveServiceProvider(string? targetText, string serviceText, string trigger)
    {
        var providers = NearbyServiceProviders(targetText, trigger, serviceText).ToArray();
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            return providers.FirstOrDefault();
        }

        return providers.FirstOrDefault(provider => FindService(VisibleServices(provider), serviceText) is not null)
            ?? providers.FirstOrDefault(provider => HasServicePromise(provider, trigger, serviceText));
    }

    private bool HasServicePromise(Entity entity, string trigger, string? serviceText = null)
    {
        if (!entity.TryGet<PromiseAnchorComponent>(out var anchor))
        {
            return false;
        }

        return anchor.PromiseIds.Any(promiseId =>
            State.PromiseLedger.Promises.Any(promise =>
                promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase)
                && promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
                && ServiceRealizationKind(promise)
                && ServiceTriggerMatches(promise.TriggerHint, trigger)
                && ServiceTextMatches(promise, serviceText)));
    }

    private static bool ServiceRealizationKind(WorldPromise promise)
    {
        var text = NormalizeToken(promise.RealizationKind ?? promise.Kind, "");
        return text is "service" or "folk_magic" or "folk_magic_service";
    }

    private static bool ServiceTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return true;
        }

        var normalizedTrigger = NormalizeToken(trigger, "");
        var parts = triggerHint
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => NormalizeToken(part, ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return parts.Contains(normalizedTrigger)
            || parts.Contains("encounter")
            || (normalizedTrigger is "services" or "request" or "service"
                && parts.Overlaps(new[] { "service", "services", "request", "offer", "folk_magic", "door", "lock", "ward", "mend", "heal", "guide" }));
    }

    private static bool ServiceTextMatches(WorldPromise promise, string? serviceText)
    {
        if (string.IsNullOrWhiteSpace(serviceText))
        {
            return true;
        }

        var normalized = serviceText.Trim();
        var subject = promise.Subject?.Trim();
        return (!string.IsNullOrWhiteSpace(subject)
                && (subject.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains(subject, StringComparison.OrdinalIgnoreCase)))
            || promise.Text.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || PromiseServiceNameForMatch(promise).Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(PromiseServiceNameForMatch(promise), StringComparison.OrdinalIgnoreCase);
    }

    private static string PromiseServiceNameForMatch(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("door") || lower.Contains("lock") || lower.Contains("ward"))
        {
            return "ward-breaking";
        }

        if (lower.Contains("route") || lower.Contains("drain") || lower.Contains("tunnel") || lower.Contains("escape"))
        {
            return "hidden-route finding";
        }

        return string.IsNullOrWhiteSpace(promise.Subject) ? "service" : promise.Subject;
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

    private IReadOnlyList<StateDelta> ApplyClaimSeeds(Entity source, string trigger)
    {
        if (!source.TryGet<ClaimSourceComponent>(out var claimSource))
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        for (var index = 0; index < claimSource.Claims.Count; index++)
        {
            var seed = claimSource.Claims[index];
            if (string.IsNullOrWhiteSpace(seed.Text))
            {
                continue;
            }

            var sourceKey = $"{trigger}:{source.Id.Value}:{index}";
            var tags = new[] { "claim_source", trigger, source.Id.Value }
                .Concat(seed.Tags ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var transaction = GameTransaction.Begin(State);
            var deltaStart = deltas.Count;
            var claim = _engine.ApplyConsequence(WorldConsequence.RecordClaim(
                sourceKey,
                source.Id.Value,
                PlayerSoulId(),
                seed.Text,
                seed.Category,
                seed.Subject,
                seed.Salience,
                seed.Confidence,
                seed.PlayerVisible,
                tags,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: source.Id.Value,
                evidence: seed.Text,
                reason: $"A {trigger} interaction surfaced an authored claim seed.",
                operation: $"{trigger}Claim",
                details: new Dictionary<string, object?>
                {
                    ["claimSeedIndex"] = index,
                    ["sourceTrigger"] = trigger,
                    ["sourceEntityId"] = source.Id.Value,
                    ["playerVisible"] = seed.PlayerVisible,
                    ["consequenceVisibility"] = WorldConsequenceVisibility.Hidden,
                }));
            deltas.AddRange(claim.Deltas);
            var duplicateClaim = claim.Details.TryGetValue("duplicate", out var duplicate) && duplicate is true;
            if (!claim.Applied)
            {
                transaction.Rollback();
                continue;
            }

            if (duplicateClaim)
            {
                transaction.Commit();
                continue;
            }

            if (claim.Applied
                && !string.IsNullOrWhiteSpace(claim.TargetId)
                && State.Claims.Records.FirstOrDefault(record =>
                    record.Id.Equals(claim.TargetId, StringComparison.OrdinalIgnoreCase)) is { } claimRecord
                && RumorSystem.ConsequenceFromClaim(State, claimRecord, "claim_source") is { } rumor)
            {
                var rumorApplied = _engine.ApplyConsequence(rumor);
                deltas.AddRange(rumorApplied.Deltas);
                if (!rumorApplied.Applied)
                {
                    RollBackClaimSeedTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        source,
                        trigger,
                        index,
                        rumorApplied.Deltas,
                        rumorApplied.Error ?? "rumor_mint_rejected");
                    continue;
                }
            }

            if (!seed.BindAsPromise)
            {
                transaction.Commit();
                continue;
            }

            var claimSeedTriggerHint = string.IsNullOrWhiteSpace(seed.TriggerHint) ? trigger : seed.TriggerHint;
            var promise = _engine.ApplyConsequence(WorldConsequence.CreatePromise(
                sourceKey,
                string.IsNullOrWhiteSpace(seed.PromiseKind) ? "rumor" : seed.PromiseKind,
                seed.Text,
                triggerHint: claimSeedTriggerHint,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: source.Id.Value,
                evidence: seed.Text,
                reason: $"A {trigger} interaction bound an authored claim seed as a promise.",
                operation: $"{trigger}ClaimPromise",
                playerVisible: seed.PlayerVisible,
                salience: seed.Salience,
                subject: seed.Subject,
                claimedPlace: seed.ClaimedPlace,
                realizationKind: seed.RealizationKind,
                bindPlace: GameSession.ShouldBindToRegion(claimSeedTriggerHint, seed.RealizationKind) ? State.RegionId : null,
                sourceClaimId: claim.TargetId,
                sourceSpeakerId: source.Id.Value,
                sourceListenerSoulId: PlayerSoulId(),
                sourceConfidence: seed.Confidence,
                useCurrentRegionAsClaimedPlace: string.IsNullOrWhiteSpace(seed.ClaimedPlace),
                emitMessage: false,
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = claim.TargetId,
                    ["claimSeedIndex"] = index,
                    ["sourceTrigger"] = trigger,
                    ["sourceEntityId"] = source.Id.Value,
                    ["playerVisible"] = seed.PlayerVisible,
                    ["consequenceVisibility"] = WorldConsequenceVisibility.Hidden,
                }));
            deltas.AddRange(promise.Deltas);
            if (!promise.Applied)
            {
                RollBackClaimSeedTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    source,
                    trigger,
                    index,
                    promise.Deltas,
                    promise.Error ?? "promise_create_rejected");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(claim.TargetId))
            {
                var claimBound = _engine.ApplyConsequence(WorldConsequence.UpdateClaim(
                    sourceKey,
                    claim.TargetId!,
                    status: "bound",
                    boundPromiseId: promise.TargetId,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: source.Id.Value,
                    evidence: seed.Text,
                    reason: "The authored claim seed bound a promise.",
                    operation: $"{trigger}ClaimBound",
                    details: new Dictionary<string, object?>
                    {
                        ["claimSeedIndex"] = index,
                        ["promiseId"] = promise.TargetId,
                        ["sourceTrigger"] = trigger,
                        ["sourceEntityId"] = source.Id.Value,
                        ["playerVisible"] = seed.PlayerVisible,
                        ["consequenceVisibility"] = WorldConsequenceVisibility.Hidden,
                    }));
                deltas.AddRange(claimBound.Deltas);
                if (!claimBound.Applied)
                {
                    RollBackClaimSeedTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        source,
                        trigger,
                        index,
                        claimBound.Deltas,
                        claimBound.Error ?? "claim_status_rejected");
                    continue;
                }
            }

            transaction.Commit();
        }

        return deltas;
    }

    private static void RollBackClaimSeedTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        Entity source,
        string trigger,
        int claimSeedIndex,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        deltas.Add(new StateDelta(
            "claimSeedSkipped",
            source.Id.Value,
            $"Claim seed rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["sourceEntityId"] = source.Id.Value,
                ["sourceTrigger"] = trigger,
                ["claimSeedIndex"] = claimSeedIndex,
                ["failure"] = failure,
                ["rejectedCount"] = diagnostics.Count,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private string DialogueLine(Entity target, List<StateDelta> deltas)
    {
        if (_engine.IsHostile(target, State.ControlledEntity))
        {
            return $"{target.Name} answers with trained imperial silence.";
        }

        if (target.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("prisoner", StringComparer.OrdinalIgnoreCase))
        {
            var targetSoulId = SoulIdFor(target);
            var playerSoulId = PlayerSoulId();
            var bond = State.Bonds.TryGet(targetSoulId, playerSoulId, out var existingBond)
                ? existingBond
                : NeutralBond(targetSoulId, playerSoulId);
            var legend = PlayerLegendSummary();
            return string.IsNullOrWhiteSpace(legend)
                ? $"{target.Name} whispers, \"Get me out of here and you'll have friends in Hollowmere who pay their debts. I'll make sure they know your name.\" {BondMoodLine(target, bond)}"
                : $"{target.Name} whispers, \"People already trade stories about you - {legend}. Get me out, and I'll see the right ones in Hollowmere hear it from me.\" {BondMoodLine(target, bond)}";
        }

        if (target.TryGet<ProfileComponent>(out var profile))
        {
            return $"{profile.PublicName}: {profile.Appearance}";
        }

        return $"{target.Name} has nothing urgent to say.";
    }

    private WorldConsequenceApplyResult ApplyBondUpdate(
        Entity subject,
        string source,
        int loyalty = 0,
        int fear = 0,
        int admiration = 0,
        int resentment = 0,
        string? posture = null,
        string operation = "updateBond",
        int maxDelta = 2) =>
        _engine.ApplyConsequence(WorldConsequence.UpdateBond(
            source,
            subject.Id.Value,
            PlayerSoulId(),
            loyalty,
            fear,
            admiration,
            resentment,
            posture,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: source,
            operation: operation,
            maxDelta: maxDelta,
            details: new Dictionary<string, object?>
            {
                ["subjectSoulId"] = SoulIdFor(subject),
                ["targetSoulId"] = PlayerSoulId(),
                ["interaction"] = source,
            }));

    private string PlayerSoulId() => SoulIdFor(State.ControlledEntity);

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static string? FindInventoryKey(InventoryComponent inventory, string item) =>
        inventory.Items.Keys
            .OrderBy(key => key)
            .FirstOrDefault(key =>
                key.Equals(item.Trim(), StringComparison.OrdinalIgnoreCase)
                || key.Contains(item.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string BondMoodLine(Entity target, BondRecord bond) =>
        $"{target.Name}'s posture is {BondSummary(bond)}.";

    private static BondRecord NeutralBond(string subjectSoulId, string targetSoulId) =>
        new(subjectSoulId, targetSoulId, Loyalty: 0, Fear: 0, Admiration: 0, Resentment: 0, Posture: "neutral");

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

    private static string? WantSummary(WantComponent want)
    {
        if (!want.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(want.Text))
        {
            return null;
        }

        var stakes = string.IsNullOrWhiteSpace(want.Stakes) ? "" : $" Stakes: {want.Stakes}";
        return $"{want.Text} (salience {want.Salience}).{stakes}";
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

    private void ResolveDoorConsequences(
        Entity door,
        List<string> messages,
        List<StateDelta> deltas,
        List<string> alreadyPersistedMessages)
    {
        deltas.AddRange(_promiseRealizationSystem.RealizeAnchoredPromises(door, "open", messages, alreadyPersistedMessages));
    }

    private static IReadOnlyList<StateDelta> FailureDiagnostics(IReadOnlyList<StateDelta> deltas) =>
        deltas
            .Where(delta => delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void RemoveRangeFrom<T>(List<T> values, int start)
    {
        if (values.Count > start)
        {
            values.RemoveRange(start, values.Count - start);
        }
    }

    private static bool RemovePersistedMessage(List<string> alreadyPersistedMessages, string message)
    {
        var index = alreadyPersistedMessages.FindIndex(item => string.Equals(item, message, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        alreadyPersistedMessages.RemoveAt(index);
        return true;
    }

    private IReadOnlyList<StateDelta> PersistUnwrittenMessages(
        IEnumerable<string> messages,
        List<string> alreadyPersistedMessages,
        string source,
        string operation,
        string targetEntityId,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var deltas = new List<StateDelta>();
        foreach (var message in messages)
        {
            if (RemovePersistedMessage(alreadyPersistedMessages, message))
            {
                continue;
            }

            var payload = details is null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : details.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            payload["fallback"] = true;
            payload["playerVisible"] = true;

            var applied = _engine.ApplyConsequence(WorldConsequence.Message(
                source,
                message,
                targetEntityId: targetEntityId,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: message,
                reason: "An interaction system message was not already persisted by a narrower typed consequence.",
                operation: operation,
                details: payload));
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
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

    private static string NormalizeToken(string value, string fallback) =>
        NormalizeId(value, fallback);

    private static int InteractionDistance(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
