using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

/// <summary>Actor-agnostic, consequence-backed ways to close an encounter without damage.</summary>
public sealed partial class GameSession
{
    private const int SocialActionReach = 3;

    private ActionResult BeginBargain(string? targetText)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var target = ResolveNearbySocialTarget(targetText);
        if (target is null)
        {
            return ActionResult.Simple("bargain", false, false, turnBefore, state.Turn, "No living actor in reach can bargain with you.");
        }

        var existing = SettleablePromiseFor(target, state.PromiseLedger);
        if (existing?.BargainOffer is not null)
        {
            return ListBargains(existing.Id);
        }

        var snapshot = GameStateSnapshot.Capture(state);
        var deltas = new List<StateDelta>();
        var promise = existing;
        if (promise is null)
        {
            var created = Engine.ApplyConsequence(WorldConsequence.CreatePromise(
                "bargain",
                "bargain",
                $"Reach terms with {target.Name}.",
                anchorEntityId: target.Id.Value,
                triggerHint: "settle a typed option",
                visibility: WorldConsequenceVisibility.Journal,
                sourceEntityId: state.ControlledEntityId.Value,
                evidence: "The player opened an explicit negotiation.",
                reason: "Ordinary encounters can create typed social terms without bespoke quest flags.",
                operation: "beginBargainPromise",
                playerVisible: true,
                salience: 3,
                subject: SoulIdFor(state.ControlledEntity),
                realizationKind: "agreement",
                emitMessage: false));
            if (!created.Applied)
            {
                snapshot.Restore(state);
                return ActionResult.Simple("bargain", false, false, turnBefore, state.Turn, created.Error ?? "The negotiation could not take shape.");
            }
            deltas.AddRange(created.Deltas);
            promise = state.PromiseLedger.Promises.FirstOrDefault(candidate => candidate.Id == created.TargetId);
        }

        if (promise is null)
        {
            snapshot.Restore(state);
            return ActionResult.Simple("bargain", false, false, turnBefore, state.Turn, "The negotiation has no durable promise anchor.");
        }

        var threat = target.Get<ActorComponent>();
        var price = Math.Clamp(2 + (threat.MaxHitPoints / 3) + threat.Attack, 3, 20);
        var options = new List<BargainOption>
        {
            new(
                "pay_gold",
                $"pay {price} gold",
                new[] { new BargainTerm("gold", BargainTermKinds.Currency, $"Pay {price} gold now.", price, "gold") }),
        };
        if (state.ControlledEntity.TryGet<InventoryComponent>(out var inventory)
            && inventory.Items.Keys.FirstOrDefault(item =>
                !item.Equals("gold", StringComparison.OrdinalIgnoreCase)
                && !inventory.TreasuredItems.Contains(item)) is { } carriedItem)
        {
            options.Add(new BargainOption(
                "give_item",
                $"give {carriedItem}",
                new[] { new BargainTerm("item", BargainTermKinds.Item, $"Give {carriedItem} now.", 1, carriedItem) }));
        }

        if (target.TryGet<WantComponent>(out var want) && !string.IsNullOrWhiteSpace(want.Text))
        {
            var serviceName = $"help with {want.Text.Trim().TrimEnd('.')}";
            options.Add(new BargainOption(
                "promise_service",
                serviceName,
                new BargainTerm[]
                {
                    new("service", BargainTermKinds.Service, serviceName, ResourceId: want.Id),
                    new("deadline", BargainTermKinds.Deadline, $"Do it by turn {state.Turn + 12}.", DueTurn: state.Turn + 12),
                }));
        }

        var offer = new BargainOffer(
            target.Id.Value,
            $"{target.Name} will stand down for one of these concrete settlements.",
            options,
            state.Turn,
            ExpiresTurn: state.Turn + 12);
        var offered = Engine.ApplyConsequence(WorldConsequence.OfferBargain(
            "bargain",
            promise.Id,
            offer,
            sourceEntityId: target.Id.Value,
            evidence: target.TryGet<WantComponent>(out var targetWant) ? targetWant.Text : "A direct negotiation in reach.",
            reason: "Deterministic terms derive from threat, inventory, and wants; no LLM trade intent is used.",
            operation: "beginBargain"));
        if (!offered.Applied)
        {
            snapshot.Restore(state);
            return ActionResult.Simple("bargain", false, false, turnBefore, state.Turn, offered.Error ?? "The terms were invalid.");
        }

        deltas.AddRange(offered.Deltas);
        var turnDeltas = Engine.AdvanceTurn();
        deltas.AddRange(turnDeltas);
        return new ActionResult
        {
            Action = "bargain",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = state.Turn,
            Messages = offered.Messages.Concat(new[] { $"Use 'bargains {promise.Id}' to inspect them, then 'settle {target.Name} with <option>'." }).Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas,
        };
    }

    private ActionResult OfferToActor(string text)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var marker = text.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return ActionResult.Simple("offer", false, false, turnBefore, state.Turn, "Use: offer <item> to <actor>.");
        }

        var offeredText = text[..marker].Trim();
        var target = ResolveNearbySocialTarget(text[(marker + 4)..]);
        if (target is null || !TryParseQuantityItem(offeredText, out var quantity, out var item))
        {
            return ActionResult.Simple("offer", false, false, turnBefore, state.Turn, "That offer has no reachable recipient or concrete item.");
        }

        var anchored = SettleablePromiseFor(target, state.PromiseLedger);
        var matchingOption = anchored?.BargainOffer?.Options.FirstOrDefault(option => OptionMatchesOffer(option, item, quantity));
        if (anchored is not null && matchingOption is not null)
        {
            var accepted = Engine.ApplyConsequence(WorldConsequence.AcceptBargain(
                "offer",
                anchored.Id,
                matchingOption.Id,
                state.ControlledEntityId.Value,
                evidence: $"The player offered {quantity} {item} matching {matchingOption.Id}.",
                reason: "A direct offer exactly matched one typed settlement option.",
                operation: "offerAcceptsBargain"));
            return FinishSocialAction("offer", turnBefore, accepted);
        }

        var snapshot = GameStateSnapshot.Capture(state);
        var transfer = Engine.ApplyConsequence(WorldConsequence.TransferItem(
            "offer",
            state.ControlledEntityId.Value,
            "give",
            item,
            quantity,
            recipientEntityId: target.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            evidence: $"The player explicitly offered {quantity} {item}.",
            reason: "A nonviolent offer transfers actual inventory before affecting the relationship.",
            operation: "offerItem",
            emitMessage: true));
        if (!transfer.Applied)
        {
            snapshot.Restore(state);
            return ActionResult.Simple("offer", false, false, turnBefore, state.Turn, transfer.Error ?? "The offer could not be transferred.");
        }

        var definition = ItemCatalog.LoadDefault().Find(item);
        var offeredValue = item.Equals("gold", StringComparison.OrdinalIgnoreCase)
            ? quantity
            : Math.Max(1, definition?.Value ?? 2) * quantity;
        var stats = target.Get<ActorComponent>();
        var threshold = Math.Clamp(2 + (stats.MaxHitPoints / 3) + stats.Attack, 3, 20);
        var closesEncounter = offeredValue >= threshold;
        var bond = Engine.ApplyConsequence(WorldConsequence.UpdateBond(
            "offer",
            target.Id.Value,
            SoulIdFor(state.ControlledEntity),
            loyaltyDelta: closesEncounter ? 2 : 1,
            fearDelta: 0,
            admirationDelta: closesEncounter ? 1 : 0,
            resentmentDelta: closesEncounter ? -2 : 0,
            posture: closesEncounter ? "bargained" : null,
            sourceEntityId: state.ControlledEntityId.Value,
            evidence: $"Offer value {offeredValue}; target threshold {threshold}.",
            reason: "An offer only closes hostility when its actual value meets the target's stakes.",
            operation: "offerBond",
            maxDelta: 5));
        if (!bond.Applied)
        {
            snapshot.Restore(state);
            return ActionResult.Simple("offer", false, false, turnBefore, state.Turn, bond.Error ?? "The relationship rejected the offer.");
        }

        var receipt = Engine.ApplyConsequence(WorldConsequence.Message(
            "offer",
            closesEncounter
                ? $"{target.Name} accepts the offer and stands down."
                : $"{target.Name} takes the offer, but it is not enough to close the conflict.",
            targetEntityId: target.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: state.ControlledEntityId.Value,
            evidence: $"Offer value {offeredValue}; threshold {threshold}.",
            operation: "offerOutcome"));
        var combined = new WorldConsequenceApplyResult(
            true, target.Id.Value, null,
            transfer.Messages.Concat(receipt.Messages).ToArray(),
            transfer.Deltas.Concat(bond.Deltas).Concat(receipt.Deltas).ToArray(),
            new Dictionary<string, object?> { ["closedEncounter"] = closesEncounter, ["value"] = offeredValue, ["threshold"] = threshold });
        return FinishSocialAction("offer", turnBefore, combined);
    }

    private ActionResult ConcedeToActor(string? targetText)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var target = ResolveNearbySocialTarget(StripLeadingTo(targetText));
        if (target is null)
        {
            return ActionResult.Simple("concede", false, false, turnBefore, state.Turn, "No living actor in reach can receive your concession.");
        }

        var snapshot = GameStateSnapshot.Capture(state);
        var factionId = target.TryGet<FactionComponent>(out var faction) ? faction.FactionId : target.Get<ActorComponent>().Faction;
        var applied = new List<WorldConsequenceApplyResult>
        {
            Engine.ApplyConsequence(WorldConsequence.AdjustFactionStanding(
                "concede", factionId, "freedom", -1,
                sourceEntityId: state.ControlledEntityId.Value,
                evidence: $"The sorcerer publicly yielded ground to {target.Name}.",
                reason: "Conceding has a durable political cost.",
                operation: "concedeStanding")),
            Engine.ApplyConsequence(WorldConsequence.UpdateBond(
                "concede", target.Id.Value, SoulIdFor(state.ControlledEntity),
                loyaltyDelta: 0, fearDelta: 0, admirationDelta: 0, resentmentDelta: -1,
                posture: "conceded", sourceEntityId: state.ControlledEntityId.Value,
                evidence: "The player yielded the immediate dispute.",
                reason: "A receiver honors the concession and closes immediate hostility.",
                operation: "concedeBond", maxDelta: 5)),
            Engine.ApplyConsequence(WorldConsequence.AddCanon(
                "concede", "concession", target.Id.Value,
                $"The sorcerer yielded the immediate ground to {target.Name} at {state.CurrentZoneId}.",
                $"You concede the immediate dispute to {target.Name}; the encounter closes, but the concession becomes public fact.",
                new[] { "concession", "nonviolent", factionId },
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: state.ControlledEntityId.Value,
                evidence: "The concession was explicit and witnessed by its receiver.",
                operation: "concedeCanon")),
        };
        if (applied.Any(result => !result.Applied))
        {
            var error = applied.First(result => !result.Applied).Error;
            snapshot.Restore(state);
            return ActionResult.Simple("concede", false, false, turnBefore, state.Turn, error ?? "The concession could not hold.");
        }

        return FinishSocialAction("concede", turnBefore, Combine(applied, target.Id.Value));
    }

    private ActionResult IntimidateActor(string? targetText)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var target = ResolveNearbySocialTarget(targetText);
        if (target is null)
        {
            return ActionResult.Simple("intimidate", false, false, turnBefore, state.Turn, "No living actor in reach can be intimidated.");
        }

        var player = state.ControlledEntity;
        var body = player.TryGet<BodyStatsComponent>(out var bodyStats) ? bodyStats.Vigor : 0;
        var soul = player.TryGet<SoulStatsComponent>(out var soulStats) ? soulStats.Attunement + soulStats.Composure : 0;
        var equipment = player.TryGet<EquipmentEffectComponent>(out var effect) ? effect.Attack + effect.Defense : 0;
        var targetStats = target.Get<ActorComponent>();
        var targetComposure = target.TryGet<SoulStatsComponent>(out var targetSoul) ? targetSoul.Composure : 2;
        var roll = state.Rng.NextInt(-2, 3);
        var score = body + soul + equipment + roll;
        var difficulty = 4 + targetComposure + targetStats.Defense + (targetStats.HitPoints / 3);
        var success = score >= difficulty;
        var bond = Engine.ApplyConsequence(WorldConsequence.UpdateBond(
            "intimidate",
            target.Id.Value,
            SoulIdFor(player),
            loyaltyDelta: 0,
            fearDelta: success ? 5 : 1,
            admirationDelta: 0,
            resentmentDelta: success ? 1 : 3,
            posture: success ? "intimidated" : null,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: player.Id.Value,
            evidence: $"Intimidation score {score} against difficulty {difficulty}.",
            reason: "Threat resolution reads body, soul, equipment, target composure, and a deterministic roll.",
            operation: "intimidateBond",
            maxDelta: 5));
        if (!bond.Applied)
        {
            return ActionResult.Simple("intimidate", false, false, turnBefore, state.Turn, bond.Error ?? "The threat had no social target.");
        }

        var deltas = bond.Deltas.ToList();
        if (success)
        {
            deltas.AddRange(Engine.ApplyConsequence(WorldConsequence.SetBehavior(
                "intimidate", target.Id.Value, "coward", duration: 8,
                sourceEntityId: player.Id.Value,
                evidence: $"Intimidation score {score} beat {difficulty}.",
                reason: "A successful threat creates bounded flight behavior.",
                operation: "intimidateFlight")).Deltas);
        }

        var message = Engine.ApplyConsequence(WorldConsequence.Message(
            "intimidate",
            success ? $"{target.Name} believes the threat and breaks off." : $"{target.Name} sees through the threat and grows angrier.",
            targetEntityId: target.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: player.Id.Value,
            evidence: $"Score {score}; difficulty {difficulty}.",
            operation: "intimidateOutcome"));
        deltas.AddRange(message.Deltas);
        return FinishSocialAction(
            "intimidate",
            turnBefore,
            new WorldConsequenceApplyResult(true, target.Id.Value, null, message.Messages, deltas, new Dictionary<string, object?>
            {
                ["success"] = success,
                ["score"] = score,
                ["difficulty"] = difficulty,
            }));
    }

    private ActionResult ExchangeWithActor(string text)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var forMarker = text.IndexOf(" for ", StringComparison.OrdinalIgnoreCase);
        var withMarker = text.LastIndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (forMarker < 1 || withMarker <= forMarker + 5)
        {
            return ActionResult.Simple("exchange", false, false, turnBefore, state.Turn, "Use: exchange <your item> for <their item> with <actor>.");
        }

        var target = ResolveNearbySocialTarget(text[(withMarker + 6)..]);
        if (target is null
            || !TryParseQuantityItem(text[..forMarker].Trim(), out var giveQuantity, out var giveItem)
            || !TryParseQuantityItem(text[(forMarker + 5)..withMarker].Trim(), out var receiveQuantity, out var receiveItem))
        {
            return ActionResult.Simple("exchange", false, false, turnBefore, state.Turn, "That exchange needs a reachable partner and two concrete item quantities.");
        }

        var catalog = ItemCatalog.LoadDefault();
        var offeredValue = ExchangeValue(catalog, giveItem, giveQuantity);
        var requestedValue = ExchangeValue(catalog, receiveItem, receiveQuantity);
        var playerSoulId = SoulIdFor(state.ControlledEntity);
        var trusted = state.Bonds.TryGet(SoulIdFor(target), playerSoulId, out var existingBond)
            && existingBond.Loyalty >= 3;
        var minimumOffer = Math.Max(1, (int)Math.Ceiling(requestedValue * (trusted ? 0.6 : 0.8)));
        if (offeredValue < minimumOffer)
        {
            return ActionResult.Simple(
                "exchange", false, false, turnBefore, state.Turn,
                $"{target.Name} refuses an exchange worth {offeredValue} for goods worth {requestedValue}; offer at least {minimumOffer} value or earn their trust.");
        }

        var snapshot = GameStateSnapshot.Capture(state);
        var give = Engine.ApplyConsequence(WorldConsequence.TransferItem(
            "exchange", state.ControlledEntityId.Value, "give", giveItem,
            giveQuantity,
            recipientEntityId: target.Id.Value,
            sourceEntityId: state.ControlledEntityId.Value,
            evidence: text,
            reason: "First half of an atomic explicit exchange.",
            operation: "exchangeGive"));
        if (!give.Applied)
        {
            snapshot.Restore(state);
            return ActionResult.Simple("exchange", false, false, turnBefore, state.Turn, give.Error ?? "You cannot supply your half of the exchange.");
        }

        var receive = Engine.ApplyConsequence(WorldConsequence.TransferItem(
            "exchange", target.Id.Value, "give", receiveItem,
            receiveQuantity,
            recipientEntityId: state.ControlledEntityId.Value,
            sourceEntityId: target.Id.Value,
            evidence: text,
            reason: "Second half of an atomic explicit exchange.",
            operation: "exchangeReceive"));
        if (!receive.Applied)
        {
            snapshot.Restore(state);
            return ActionResult.Simple("exchange", false, false, turnBefore, state.Turn, receive.Error ?? $"{target.Name} cannot supply their half of the exchange.");
        }

        var bond = Engine.ApplyConsequence(WorldConsequence.UpdateBond(
            "exchange", target.Id.Value, SoulIdFor(state.ControlledEntity),
            loyaltyDelta: 1, fearDelta: 0, admirationDelta: 0, resentmentDelta: -1,
            posture: Engine.IsHostile(target, state.ControlledEntity) ? "bargained" : null,
            sourceEntityId: state.ControlledEntityId.Value,
            evidence: text,
            reason: "A completed reciprocal exchange can close an immediate dispute.",
            operation: "exchangeBond",
            maxDelta: 2));
        var receipt = Engine.ApplyConsequence(WorldConsequence.Message(
            "exchange",
            $"You exchange {giveQuantity} {giveItem} for {receiveQuantity} {receiveItem} with {target.Name}.",
            targetEntityId: target.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: state.ControlledEntityId.Value,
            evidence: text,
            operation: "exchangeOutcome"));
        if (!bond.Applied || !receipt.Applied)
        {
            var error = !bond.Applied ? bond.Error : receipt.Error;
            snapshot.Restore(state);
            return ActionResult.Simple("exchange", false, false, turnBefore, state.Turn, error ?? "The reciprocal exchange could not complete.");
        }

        return FinishSocialAction("exchange", turnBefore, Combine(new[] { give, receive, bond, receipt }, target.Id.Value));
    }

    private Entity? ResolveNearbySocialTarget(string? text)
    {
        var state = Engine.State;
        var player = state.ControlledEntity;
        if (!player.TryGet<PositionComponent>(out var origin))
        {
            return null;
        }

        var token = NormalizeToken(text ?? "", "");
        var candidates = state.Entities.Values
            .Where(entity => entity.Id != player.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.StepDistance(origin.Position, position.Position) <= SocialActionReach)
            .OrderByDescending(entity => Engine.IsHostile(entity, player))
            .ThenBy(entity => GameEngine.StepDistance(origin.Position, entity.Get<PositionComponent>().Position))
            .ThenBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(token))
        {
            return candidates.FirstOrDefault();
        }

        return candidates.FirstOrDefault(entity =>
            NormalizeToken(entity.Id.Value, "").Equals(token, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(entity.Name, "").Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private ActionResult FinishSocialAction(string action, int turnBefore, WorldConsequenceApplyResult applied)
    {
        if (!applied.Applied)
        {
            return ActionResult.Simple(action, false, false, turnBefore, Engine.State.Turn, applied.Error ?? "The social action was rejected.");
        }

        var turnDeltas = Engine.AdvanceTurn();
        return new ActionResult
        {
            Action = action,
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = Engine.State.Turn,
            Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = applied.Deltas.Concat(turnDeltas).ToArray(),
        };
    }

    private static WorldConsequenceApplyResult Combine(IEnumerable<WorldConsequenceApplyResult> results, string targetId)
    {
        var all = results.ToArray();
        var failure = all.FirstOrDefault(result => !result.Applied);
        return new WorldConsequenceApplyResult(
            failure is null,
            targetId,
            failure?.Error,
            all.SelectMany(result => result.Messages).ToArray(),
            all.SelectMany(result => result.Deltas).ToArray(),
            new Dictionary<string, object?>());
    }

    private static bool TryParseQuantityItem(string text, out int quantity, out string item)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var parsed))
        {
            quantity = Math.Clamp(parsed, 1, 99);
            item = parts[1];
            return !string.IsNullOrWhiteSpace(item);
        }

        quantity = 1;
        item = text.Trim();
        return !string.IsNullOrWhiteSpace(item);
    }

    private static bool OptionMatchesOffer(BargainOption option, string item, int quantity)
    {
        var substantive = option.Terms.Where(term => !term.Kind.Equals(BargainTermKinds.Deadline, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (substantive.Length != 1)
        {
            return false;
        }

        var term = substantive[0];
        return term.Quantity == quantity
            && (term.Kind.Equals(BargainTermKinds.Currency, StringComparison.OrdinalIgnoreCase)
                && item.Equals("gold", StringComparison.OrdinalIgnoreCase)
                || term.Kind.Equals(BargainTermKinds.Item, StringComparison.OrdinalIgnoreCase)
                && string.Equals(term.ResourceId, item, StringComparison.OrdinalIgnoreCase));
    }

    private static int ExchangeValue(ItemCatalog catalog, string item, int quantity) =>
        (item.Equals("gold", StringComparison.OrdinalIgnoreCase)
            ? 1
            : Math.Max(1, catalog.Find(item)?.Value ?? 1)) * quantity;

    private static string? StripLeadingTo(string? text) =>
        text?.StartsWith("to ", StringComparison.OrdinalIgnoreCase) == true ? text[3..].Trim() : text;
}
