using Sorcerer.Core.Entities;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Runtime;

public sealed record RunChronicleRecord(
    string Id,
    int Seed,
    string Status,
    string Conclusion,
    string SoulId,
    int Turn,
    string ZoneId,
    IReadOnlyList<string> Legend,
    IReadOnlyList<string> KeyDeeds,
    string Text,
    string Mode = "classic",
    string Treatment = "none",
    int Restorations = 0);

public static class RunChronicle
{
    public static RunChronicleRecord Build(GameState state)
    {
        var soulId = state.ControlledEntity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : state.ControlledEntityId.Value;
        var legend = state.Legend.Tags
            .Where(tag => tag.ActorSoulId.Equals(soulId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Tag)
            .OrderByDescending(group => group.Sum(tag => tag.Weight))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Sum(tag => tag.Weight)}")
            .Take(6)
            .ToArray();
        var deeds = state.Deeds.Records
            .Where(deed => deed.ActorSoulId.Equals(soulId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(deed => deed.Turn)
            .ThenBy(deed => deed.Id, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(deed => $"{deed.Kind} at {deed.PlaceKey} on turn {deed.Turn}")
            .Reverse()
            .ToArray();
        var conclusion = string.IsNullOrWhiteSpace(state.RunConclusion)
            ? state.RunStatus
            : state.RunConclusion!;
        var legendText = legend.Length == 0 ? "unfiled" : string.Join(", ", legend);
        var deedText = deeds.Length == 0 ? "few official marks" : string.Join("; ", deeds);
        var text = state.RunStatus.Equals("victory", StringComparison.OrdinalIgnoreCase)
            ? $"Chronicle of a sorcerer who ended at turn {state.Turn}: {conclusion} The legend reads {legendText}; the ledgers remember {deedText}."
            : $"Chronicle of a sorcerer who ended at turn {state.Turn}: {conclusion}. The legend reads {legendText}; the ledgers remember {deedText}.";
        return new RunChronicleRecord(
            $"chronicle_{state.Seed}_{state.Turn}_{state.Canon.Records.Count + 1}",
            state.Seed,
            state.RunStatus,
            conclusion,
            soulId,
            state.Turn,
            state.CurrentZoneId,
            legend,
            deeds,
            text,
            string.IsNullOrWhiteSpace(state.RunMode) ? "classic" : state.RunMode,
            DeriveDeathTreatment(state),
            state.RestorationCount);
    }

    // Phase 2.6: on defeat, the body is disposed of in the killer's register -- an imperial hand
    // files it as a Censorate incident, wild magic transforms it, ordinary force just ends it.
    // A run that did not end in defeat has no treatment ("none").
    private static string DeriveDeathTreatment(GameState state) =>
        state.RunStatus.Equals("defeat", StringComparison.OrdinalIgnoreCase)
            ? DeathTreatment.ForDefeat(state.LastControlledDamageProvenance)
            : DeathTreatment.None;
}
