using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

public sealed partial class GameSession
{
    private const int ObligationSettlementReach = 2;

    /// <summary>
    /// Bargain terms may crystallize in the asynchronous post-speech parser. An explicit terms
    /// query is the synchronization affordance for both CLI and GUI: it waits for that already
    /// running parse, applies its typed proposals, then lists authoritative offers.
    /// </summary>
    private async Task<ActionResult> ListBargainsAfterPendingDialogueAsync(string? targetText)
    {
        if (_pendingClaimExtractions.Count == 0)
        {
            return ListBargains(targetText);
        }

        var turn = Engine.State.Turn;
        var synchronized = await FlushPendingClaimExtractionsAsync(new ActionResult
        {
            Action = "bargains",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
        });
        var listed = ListBargains(targetText);
        return listed with
        {
            Messages = synchronized.Messages.Concat(listed.Messages).ToArray(),
            Deltas = synchronized.Deltas.Concat(listed.Deltas).ToArray(),
            DialogueClaimExtractions = synchronized.DialogueClaimExtractions,
            DialogueParses = synchronized.DialogueParses,
            TechnicalFailure = synchronized.TechnicalFailure || listed.TechnicalFailure,
        };
    }

    /// <summary>Accept one engine-verifiable option. Speech alone never clears the claim.</summary>
    private ActionResult SettleObligation(string? targetText)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var (target, requestedOption) = SplitSettlementSelection(targetText);
        var player = state.ControlledEntity;
        if (!player.TryGet<PositionComponent>(out var playerPosition))
        {
            return ActionResult.Simple("settle", false, false, turnBefore, state.Turn,
                "You are in no position to settle an obligation.");
        }

        var candidates = state.Entities.Values
            .Where(entity => entity.Id != player.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.StepDistance(playerPosition.Position, position.Position) <= ObligationSettlementReach)
            .Select(entity => new
            {
                Entity = entity,
                Promise = SettleablePromiseFor(entity, state.PromiseLedger),
            })
            .Where(candidate => candidate.Promise is not null)
            .OrderBy(candidate => candidate.Entity.TryGet<PositionComponent>(out var position)
                ? GameEngine.StepDistance(playerPosition.Position, position.Position)
                : int.MaxValue)
            .ThenBy(candidate => candidate.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(target))
        {
            var wanted = NormalizeToken(target, "");
            candidates = candidates.Where(candidate =>
                    candidate.Entity.Id.Value.Equals(target, StringComparison.OrdinalIgnoreCase)
                    || candidate.Promise!.Id.Equals(target, StringComparison.OrdinalIgnoreCase)
                    || NormalizeToken(candidate.Entity.Name, "").Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (candidates.Length == 0)
        {
            return ActionResult.Simple("settle", false, false, turnBefore, state.Turn,
                "No nearby person anchors a debt you can settle here.");
        }

        if (candidates.Length > 1 && string.IsNullOrWhiteSpace(target))
        {
            return ActionResult.Simple("settle", false, false, turnBefore, state.Turn,
                $"Name whose terms to accept: {string.Join(", ", candidates.Select(candidate => candidate.Entity.Name))}.");
        }

        var claimant = candidates[0].Entity;
        var promise = candidates[0].Promise!;
        if (promise.BargainOffer is not { } offer)
        {
            return ActionResult.Simple("settle", false, false, turnBefore, state.Turn,
                $"{claimant.Name} has not stated engine-verifiable terms yet. Ask what currency, item, service, standing, concession, or deadline would settle the claim.");
        }

        BargainOption? option = null;
        if (!string.IsNullOrWhiteSpace(requestedOption))
        {
            var token = NormalizeToken(requestedOption, "");
            option = offer.Options.FirstOrDefault(candidate =>
                candidate.Id.Equals(token, StringComparison.OrdinalIgnoreCase)
                || NormalizeToken(candidate.Label, "").Contains(token, StringComparison.OrdinalIgnoreCase));
        }
        else if (offer.Options.Count == 1)
        {
            option = offer.Options[0];
        }

        if (option is null)
        {
            return ActionResult.Simple(
                "settle", false, false, turnBefore, state.Turn,
                $"Choose one option with 'settle {claimant.Name} with <option>': {string.Join(" | ", offer.Options.Select(DescribeOption))}");
        }

        var applied = Engine.ApplyConsequence(WorldConsequence.AcceptBargain(
            "settle_obligation",
            promise.Id,
            option.Id,
            player.Id.Value,
            evidence: $"The player selected {option.Label} from a typed offer.",
            reason: "Bargain acceptance applies every immediate term transactionally and retains pending services."));
        if (!applied.Applied)
        {
            return ActionResult.Simple("settle", false, false, turnBefore, state.Turn,
                $"Those terms cannot be fulfilled: {applied.Error ?? "the world rejected the transaction"}.");
        }

        var turnDeltas = Engine.AdvanceTurn();
        var deltas = applied.Deltas.Concat(turnDeltas).ToArray();
        return new ActionResult
        {
            Action = "settle",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = state.Turn,
            Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas,
        };
    }

    private ActionResult ListBargains(string? targetText)
    {
        var state = Engine.State;
        var token = NormalizeToken(targetText ?? "", "");
        var rows = state.PromiseLedger.Promises
            .Where(promise => promise.PlayerVisible && (promise.BargainOffer is not null || promise.BargainAgreement is not null))
            .Where(promise => string.IsNullOrWhiteSpace(token)
                || promise.Id.Contains(token, StringComparison.OrdinalIgnoreCase)
                || (promise.BoundTargetId?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false)
                || BargainClaimantMatches(promise, token))
            .SelectMany(promise => promise.BargainOffer is { } offer
                ? new[] { $"{promise.Id} OFFER from {Engine.EntityById(offer.ClaimantEntityId)?.Name ?? offer.ClaimantEntityId}: {string.Join(" | ", offer.Options.Select(DescribeOption))}" }
                : new[] { $"{promise.Id} AGREEMENT: {string.Join("; ", promise.BargainAgreement!.Terms.Select(term => $"{term.Id} [{term.Status}] {term.Text}"))}" })
            .ToArray();
        return ActionResult.Simple(
            "bargains", true, false, state.Turn, state.Turn,
            rows.Length == 0 ? "No typed bargain offers or agreements are recorded." : string.Join(Environment.NewLine, rows));
    }

    private bool BargainClaimantMatches(WorldPromise promise, string normalizedTarget)
    {
        var claimantId = promise.BargainOffer?.ClaimantEntityId ?? promise.BargainAgreement?.ClaimantEntityId;
        if (string.IsNullOrWhiteSpace(claimantId))
        {
            return false;
        }

        if (NormalizeToken(claimantId, "").Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var claimant = Engine.EntityById(claimantId);
        return claimant is not null
            && NormalizeToken(claimant.Name, "").Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase);
    }

    private ActionResult FulfillAgreement(string text)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var agreements = state.PromiseLedger.Promises
            .Where(promise => promise.BargainAgreement?.Status.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var promise = tokens.Length > 0
            ? agreements.FirstOrDefault(candidate => candidate.Id.Equals(tokens[0], StringComparison.OrdinalIgnoreCase))
            : agreements.Length == 1 ? agreements[0] : null;
        var termToken = tokens.Length > 1 ? tokens[1] : null;
        if (promise is null && agreements.Length == 1)
        {
            promise = agreements[0];
            termToken = tokens.FirstOrDefault();
        }

        var pending = promise?.BargainAgreement?.Terms
            .Where(term => term.Kind.Equals(BargainTermKinds.Service, StringComparison.OrdinalIgnoreCase)
                && term.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? Array.Empty<BargainTerm>();
        var term = string.IsNullOrWhiteSpace(termToken)
            ? pending.Length == 1 ? pending[0] : null
            : pending.FirstOrDefault(candidate => candidate.Id.Equals(termToken, StringComparison.OrdinalIgnoreCase)
                || NormalizeToken(candidate.Text, "").Contains(NormalizeToken(termToken, ""), StringComparison.OrdinalIgnoreCase));
        if (promise is null || term is null)
        {
            return ActionResult.Simple(
                "fulfill", false, false, turnBefore, state.Turn,
                agreements.Length == 0
                    ? "No active agreement has a pending service term."
                    : "Name an agreement and pending service term: fulfill <promise_id> <term_id>.");
        }

        var applied = Engine.ApplyConsequence(WorldConsequence.FulfillBargain(
            "perform_agreement",
            promise.Id,
            term.Id,
            state.ControlledEntityId.Value,
            evidence: $"The controlled actor spent a turn performing {term.Text} in the claimant's presence.",
            reason: "A deferred service term is only fulfilled by an explicit in-world action near its claimant."));
        if (!applied.Applied)
        {
            return ActionResult.Simple("fulfill", false, false, turnBefore, state.Turn, applied.Error ?? "The service could not be performed.");
        }

        var turnDeltas = Engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "fulfill",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = state.Turn,
            Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = applied.Deltas.Concat(turnDeltas).ToArray(),
        };
    }

    private static WorldPromise? SettleablePromiseFor(Entity entity, PromiseLedger ledger)
    {
        if (!entity.TryGet<PromiseAnchorComponent>(out var anchor))
        {
            return null;
        }

        return anchor.PromiseIds
            .Select(id => ledger.Promises.FirstOrDefault(promise =>
                promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(promise => promise is not null
                && (promise.Kind.Equals("debt", StringComparison.OrdinalIgnoreCase)
                    || promise.Kind.Equals("bargain", StringComparison.OrdinalIgnoreCase))
                && promise.Status is not "cleared" and not "fulfilled" and not "breached");
    }

    private static (string? Target, string? Option) SplitSettlementSelection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var marker = text.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        return marker < 0
            ? (text.Trim(), null)
            : (text[..marker].Trim(), text[(marker + 6)..].Trim());
    }

    private static string DescribeOption(BargainOption option) =>
        $"{option.Id} ({option.Label}: {string.Join(", ", option.Terms.Select(term => term.Text))})";
}
