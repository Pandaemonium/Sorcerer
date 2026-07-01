using Sorcerer.Core.Entities;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed record NarrationLine(
    string Kind,
    string Text,
    IReadOnlyDictionary<string, object?> Details);

public static class NarrationSystem
{
    public static IReadOnlyList<NarrationLine> ZoneEntryRumors(
        GameState state,
        RegionDefinition region,
        RealmProfile realm)
    {
        var playerSoulId = SoulIdFor(state.ControlledEntity);
        var legend = state.Legend.Tags
            .Where(tag => tag.ActorSoulId.Equals(playerSoulId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Tag)
            .Select(group => new
            {
                Tag = group.Key,
                Weight = group.Sum(tag => tag.Weight),
            })
            .OrderByDescending(item => item.Weight)
            .ThenBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var imperialThreat = state.Factions.FactionsByRole("empire_bloc").Sum(faction =>
            Standing(faction, "imperial-threat") + Standing(faction, "notoriety") + Standing(faction, "suspicion"));
        var localStanding = state.Factions.StandingValue(region.RealmId, "gratitude")
            + state.Factions.StandingValue(region.RealmId, "support")
            - state.Factions.StandingValue(region.RealmId, "fear");

        if (legend.Length == 0 && imperialThreat <= 0 && localStanding <= 0)
        {
            return Array.Empty<NarrationLine>();
        }

        var lines = new List<NarrationLine>();
        if (legend.Length > 0)
        {
            var top = legend[0];
            var text = localStanding > 0
                ? $"In {region.Name}, a local rumor improves your name before you arrive: {top.Tag}, but maybe useful."
                : imperialThreat >= 3
                    ? $"A Censorate rumor reaches {region.Name} ahead of you: the {top.Tag} sorcerer is becoming a file with teeth."
                    : $"A rumor in {region.Name} tries on your newest name: {top.Tag}.";
            lines.Add(new NarrationLine(
                "zone_entry_rumor",
                text,
                new Dictionary<string, object?>
                {
                    ["regionId"] = region.Id,
                    ["realmId"] = region.RealmId,
                    ["realmStatus"] = realm.Status,
                    ["legendTag"] = top.Tag,
                    ["legendWeight"] = top.Weight,
                    ["imperialPressure"] = imperialThreat,
                    ["localStanding"] = localStanding,
                }));
        }
        else if (imperialThreat > 0)
        {
            lines.Add(new NarrationLine(
                "zone_entry_rumor",
                $"A clerkly rumor has reached {region.Name}: someone bright and unnamed is making the marble offices nervous.",
                new Dictionary<string, object?>
                {
                    ["regionId"] = region.Id,
                    ["realmId"] = region.RealmId,
                    ["realmStatus"] = realm.Status,
                    ["imperialPressure"] = imperialThreat,
                    ["localStanding"] = localStanding,
                }));
        }

        return lines;
    }

    private static int Standing(FactionRecord faction, string axis) =>
        faction.Standing.TryGetValue(axis, out var value) ? value : 0;

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;
}
