using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;

namespace Sorcerer.Core;

public sealed partial class GameSession
{
    private ActionResult CleanseCost(string? reference)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var actor = state.ControlledEntity;
        var inventory = actor.TryGet<InventoryComponent>(out var carried) ? carried : InventoryComponent.Empty();
        var item = actor.TryGet<ItemAlterationComponent>(out var alterations)
            ? alterations.Profiles.Keys.FirstOrDefault(key => string.IsNullOrWhiteSpace(reference)
                ? false
                : key.Contains(reference, StringComparison.OrdinalIgnoreCase))
            : null;
        var category = item is null ? "curse" : "altered_item";
        var profileId = item is null
            ? state.PromiseLedger.Promises
                .Where(promise => promise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase)
                    && promise.Status is not "cleared" and not "fulfilled")
                .Where(promise => string.IsNullOrWhiteSpace(reference)
                    || promise.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)
                    || promise.CostProfileId?.Contains(reference, StringComparison.OrdinalIgnoreCase) == true
                    || promise.Subject.Contains(reference, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(promise => promise.Salience)
                .Select(promise => promise.CostProfileId)
                .FirstOrDefault()
            : alterations!.Profiles[item];
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return ActionResult.Simple("cleanse", false, false, turnBefore, state.Turn,
                "No matching active curse or altered carried item can be cleansed.");
        }

        var reagent = inventory.Items.Keys.FirstOrDefault(key =>
            key.Contains("quicklime", StringComparison.OrdinalIgnoreCase)
            || key.Replace('_', ' ').Equals("grave salt", StringComparison.OrdinalIgnoreCase));
        if (reagent is null)
        {
            return ActionResult.Simple("cleanse", false, false, turnBefore, state.Turn,
                "A mundane cleansing needs quicklime or grave salt; a learned cleansing charter can substitute authority.");
        }

        var snapshot = GameStateSnapshot.Capture(state);
        var spend = Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "cleanse",
            actor.Id.Value,
            reagent,
            op: "consume",
            amount: 1,
            sourceEntityId: actor.Id.Value,
            evidence: $"{reagent} was committed to cleansing {profileId}.",
            reason: "A folk cleansing route consumes a real component.",
            operation: "cleanseComponent"));
        var resolved = spend.Applied
            ? Engine.ApplyConsequence(WorldConsequence.ResolveCost(
                "cleanse",
                actor.Id.Value,
                profileId,
                item,
                category,
                sourceEntityId: actor.Id.Value,
                evidence: $"{reagent} was used according to an authored clearing route.",
                reason: "A concrete counterplay route resolved the active cost.",
                operation: "cleanseCost"))
            : WorldConsequenceApplyResult.Empty(spend.Error ?? "The cleansing component could not be spent.");
        if (!spend.Applied || !resolved.Applied)
        {
            snapshot.Restore(state);
            return ActionResult.Simple("cleanse", false, false, turnBefore, state.Turn,
                resolved.Error ?? spend.Error ?? "The cleansing did not take.");
        }

        var turnDeltas = Engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "cleanse",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = state.Turn,
            Messages = resolved.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = spend.Deltas.Concat(resolved.Deltas).Concat(turnDeltas).ToArray(),
        };
    }
}
