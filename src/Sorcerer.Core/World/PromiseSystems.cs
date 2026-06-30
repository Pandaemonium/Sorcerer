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
    public WorldPromise Capture(GameState state, PromiseCapture capture)
    {
        var promise = state.PromiseLedger.Add(capture.Kind, capture.Text, playerVisible: true);
        state.Memories.Append(
            capture.Subject,
            capture.Text,
            capture.Source,
            capture.Salience,
            shareable: true);
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
}
