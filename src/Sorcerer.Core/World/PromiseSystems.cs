using Sorcerer.Core.Consequences;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;

namespace Sorcerer.Core.World;

public sealed record PromiseCapture(
    string Kind,
    string Subject,
    string Text,
    string Source,
    int Salience,
    IReadOnlyList<string> Tags);

public sealed record PromiseBinding(
    string PromiseId,
    string BlueprintId,
    string? ClaimedPlace,
    string? BoundPlace,
    int CapacityCost = 1);

public sealed record PromiseReservation(
    string PromiseId,
    string ZoneKey,
    string BlueprintId,
    int CapacityCost);

public sealed class PromiseSystem
{
    public WorldPromise Capture(
        GameState state,
        PromiseCapture capture,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        ValidateCapture(capture);

        var snapshot = GameStateSnapshot.Capture(state);
        var applied = Apply(state, applyConsequence, WorldConsequence.CreatePromise(
            capture.Source,
            capture.Kind,
            capture.Text,
            playerVisible: true,
            salience: capture.Salience,
            subject: capture.Subject,
            emitMessage: false,
            details: new Dictionary<string, object?>
            {
                ["tags"] = capture.Tags.ToArray(),
            }));
        var promiseId = applied.TargetId ?? applied.Deltas.FirstOrDefault()?.Target;
        var promise = state.PromiseLedger.Promises.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(promiseId)
            && item.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase));
        if (!applied.Applied || promise is null)
        {
            snapshot.Restore(state);
            throw new InvalidOperationException(applied.Error ?? "Promise capture did not create a promise.");
        }

        var memory = Apply(state, applyConsequence, WorldConsequence.RecordMemory(
            capture.Source,
            capture.Subject,
            capture.Text,
            capture.Source,
            capture.Salience,
            shareable: true,
            evidence: capture.Text,
            reason: "Promise capture records its source memory.",
            operation: "promiseCaptureMemory",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["tags"] = capture.Tags.ToArray(),
            }));
        if (!memory.Applied)
        {
            snapshot.Restore(state);
            throw new InvalidOperationException(
                memory.Error is null
                    ? "Promise capture did not record its source memory."
                    : $"Promise capture did not record its source memory: {memory.Error}");
        }

        return promise;
    }

    public PromiseBinding? TryBind(WorldPromise promise)
    {
        if (promise.Kind is "prophecy" or "quest" or "threat" or "debt")
        {
            return new PromiseBinding(
                promise.Id,
                BlueprintId: promise.Kind,
                ClaimedPlace: null,
                BoundPlace: promise.BoundPlace);
        }

        return null;
    }

    public PromiseReservation? TryReserve(PromiseBinding binding, string zoneKey) =>
        new(binding.PromiseId, zoneKey, binding.BlueprintId, binding.CapacityCost);

    private static WorldConsequenceApplyResult Apply(
        GameState state,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence,
        WorldConsequence consequence) =>
        applyConsequence is not null
            ? applyConsequence(consequence)
            : WorldConsequenceGuard.ApplyWithNewApplier(state, consequence);

    private static void ValidateCapture(PromiseCapture capture)
    {
        if (string.IsNullOrWhiteSpace(capture.Kind))
        {
            throw new ArgumentException("Promise capture requires a kind.", nameof(capture));
        }

        if (string.IsNullOrWhiteSpace(capture.Subject))
        {
            throw new ArgumentException("Promise capture requires a subject.", nameof(capture));
        }

        if (string.IsNullOrWhiteSpace(capture.Text))
        {
            throw new ArgumentException("Promise capture requires text.", nameof(capture));
        }

        if (string.IsNullOrWhiteSpace(capture.Source))
        {
            throw new ArgumentException("Promise capture requires a source.", nameof(capture));
        }
    }
}
