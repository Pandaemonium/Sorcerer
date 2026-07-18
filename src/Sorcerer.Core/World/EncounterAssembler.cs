using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record EncounterRequest(
    int WorldSeed,
    string ZoneId,
    string Purpose,
    string Discriminator,
    RegionDefinition Region,
    string ObjectiveName,
    int PromiseSalience,
    int FactionPressure,
    bool InteriorAvailable,
    IReadOnlyList<string>? ExcludedKinds = null);

public sealed record EncounterCastSpec(
    string Name,
    string Title,
    char Glyph,
    string FactionId,
    int HitPoints,
    int Attack,
    string AiPolicyId,
    GridPoint Offset,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Roles,
    string WantText,
    string WantStakes,
    IReadOnlyList<string> Verbs,
    bool HoldsObjective);

public sealed record EncounterPlan(
    string ArchetypeId,
    string Kind,
    string FactionId,
    int Tier,
    string ObjectivePlacement,
    IReadOnlyList<EncounterCastSpec> Casts,
    EncounterCastSpec? Rival,
    string CanonText);

/// <summary>
/// Pure encounter composer (IMPLEMENTATION_PLAN §3.4): picks a data-authored archetype and
/// fills its cast slots deterministically from a per-request stable seed. Returns null at
/// stakes tier 0 — low-stakes objectives stay simple finds, and the caller keeps its
/// existing single-spawn path. Owns no completion state; everything it plans is expressed
/// later as ordinary spawn/want consequences.
/// </summary>
public static class EncounterAssembler
{
    public const string KindGuardedCache = "guarded_cache";
    public const string KindKeeper = "keeper";
    public const string KindRestrictedSite = "restricted_site";
    public const string KindRivalClaimant = "rival_claimant";

    public static int StakesTier(int salience, int imperialPresence, int factionPressure)
    {
        var tier = 0;
        if (salience >= 3)
        {
            tier++;
        }

        if (salience >= 5)
        {
            tier++;
        }

        if (imperialPresence >= 50)
        {
            tier++;
        }

        if (factionPressure > 0)
        {
            tier++;
        }

        return Math.Clamp(tier, 0, 3);
    }

    public static EncounterPlan? Assemble(EncounterRequest request, EncounterTemplateCatalog catalog)
    {
        var tier = StakesTier(request.PromiseSalience, request.Region.ImperialPresence, request.FactionPressure);
        if (tier == 0)
        {
            return null;
        }

        var excluded = request.ExcludedKinds ?? Array.Empty<string>();
        var archetypes = catalog.Archetypes
            .Where(archetype => tier >= archetype.MinTier && tier <= archetype.MaxTier)
            .Where(archetype => !archetype.RequiresInterior || request.InteriorAvailable)
            .Where(archetype => request.Purpose != "ambient" || archetype.AmbientEligible)
            .Where(archetype => request.Purpose != "promise" || archetype.PromiseEligible)
            .Where(archetype => !excluded.Contains(archetype.Kind, StringComparer.OrdinalIgnoreCase))
            .OrderBy(archetype => archetype.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (archetypes.Length == 0)
        {
            return null;
        }

        var rng = new DeterministicRng(WorldRoll.StableSeed(
            request.WorldSeed,
            request.ZoneId,
            request.Discriminator,
            "encounter"));
        var archetype = PickWeighted(archetypes, item => item.Weight, rng);
        var casts = archetype.Casts
            .Where(cast => request.Region.ImperialPresence >= cast.MinImperialPresence)
            .OrderBy(cast => cast.FactionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (casts.Length == 0)
        {
            return null;
        }

        var cast = PickWeighted(casts, item => item.Weight, rng);
        var specs = new List<EncounterCastSpec>();
        EncounterCastSpec? rival = null;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offsetIndex = 0;
        foreach (var slot in cast.Slots)
        {
            var count = SlotCount(slot, tier);
            for (var i = 0; i < count; i++)
            {
                var spec = ComposeCast(request, archetype, cast, slot, tier, rng, usedNames, offsetIndex);
                offsetIndex++;
                if (slot.Role.Equals("rival", StringComparison.OrdinalIgnoreCase))
                {
                    rival ??= spec;
                }
                else
                {
                    specs.Add(spec);
                }
            }
        }

        if (specs.Count == 0 && rival is null)
        {
            return null;
        }

        var placement = archetype.Kind switch
        {
            KindKeeper => "keeper_inventory",
            KindRestrictedSite => "inside_interior",
            _ => "ground",
        };
        var canon = Expand(archetype.CanonPattern, request, cast.FactionId, specs.FirstOrDefault() ?? rival!);
        return new EncounterPlan(
            archetype.Id,
            archetype.Kind,
            cast.FactionId,
            tier,
            placement,
            specs,
            rival,
            canon);
    }

    private static EncounterCastSpec ComposeCast(
        EncounterRequest request,
        EncounterArchetypeDefinition archetype,
        EncounterFactionCastDefinition cast,
        EncounterCastSlotDefinition slot,
        int tier,
        IRng rng,
        HashSet<string> usedNames,
        int offsetIndex)
    {
        var title = Expand(slot.TitlePattern, request, cast.FactionId, null);
        var name = ComposeName(request.Region, title, rng, usedNames);
        var actor = string.IsNullOrWhiteSpace(slot.ArchetypeId)
            ? null
            : ActorArchetypeCatalog.Default.Find(slot.ArchetypeId!);
        var minHp = actor?.MinHitPoints ?? slot.MinHitPoints;
        var maxHp = actor?.MaxHitPoints ?? slot.MaxHitPoints;
        var minAtk = actor?.MinAttack ?? slot.MinAttack;
        var maxAtk = actor?.MaxAttack ?? slot.MaxAttack;
        var hitPoints = RollRange(minHp, maxHp, rng) + (tier - 1) * 2;
        var attack = RollRange(minAtk, maxAtk, rng) + (tier - 1);
        var glyph = actor?.Glyph ?? slot.Glyph;
        var verbs = slot.InteractableVerbs
            ?? (actor is { Verbs.Count: > 0 } ? actor.Verbs : new[] { "talk", "examine" });
        var holdsObjective = slot.Role.Equals("keeper", StringComparison.OrdinalIgnoreCase);
        // Fold the archetype's tactical read (intent/weakness/counter) into the want-stakes surface
        // so dialogue and inspect teach the non-damage way past this enemy, not just its stat block.
        var wantStakes = Expand(slot.WantStakes, request, cast.FactionId, null);
        if (actor is not null && !string.IsNullOrWhiteSpace(actor.InspectLine()))
        {
            wantStakes = string.IsNullOrWhiteSpace(wantStakes)
                ? actor.InspectLine()
                : $"{wantStakes} {actor.InspectLine()}";
        }

        var tags = actor is null
            ? slot.Tags
            : slot.Tags.Concat(actor.Tags).Concat(new[] { "actor_archetype", actor.Id })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return new EncounterCastSpec(
            name,
            title,
            glyph,
            cast.FactionId,
            hitPoints,
            attack,
            slot.AiPolicyId,
            FormationOffset(archetype.Formation, offsetIndex),
            tags,
            slot.Roles,
            Expand(slot.WantPattern, request, cast.FactionId, null),
            wantStakes,
            verbs,
            holdsObjective);
    }

    private static string ComposeName(RegionDefinition region, string title, IRng rng, HashSet<string> usedNames)
    {
        var names = region.Population?.Names;
        if (names is null || names.GivenNames.Count == 0 || names.ByNames.Count == 0)
        {
            return title;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate =
                $"{names.GivenNames[rng.NextInt(0, names.GivenNames.Count)]} {names.ByNames[rng.NextInt(0, names.ByNames.Count)]}, {title}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }

        return title;
    }

    /// <summary>Ring walks the 8-neighborhood around the objective; adjacent alternates the
    /// two flanking tiles. Offsets are desires — the realizer resolves each through the
    /// shared open-tile search.</summary>
    private static GridPoint FormationOffset(string formation, int index)
    {
        if (formation.Equals("adjacent", StringComparison.OrdinalIgnoreCase))
        {
            return index % 2 == 0 ? new GridPoint(1, 0) : new GridPoint(-1, 0);
        }

        var ring = new[]
        {
            new GridPoint(1, 0),
            new GridPoint(-1, 0),
            new GridPoint(0, 1),
            new GridPoint(0, -1),
            new GridPoint(1, 1),
            new GridPoint(-1, -1),
            new GridPoint(1, -1),
            new GridPoint(-1, 1),
        };
        return ring[index % ring.Length];
    }

    private static int SlotCount(EncounterCastSlotDefinition slot, int tier)
    {
        if (slot.CountByTier is null || slot.CountByTier.Count == 0)
        {
            return 1;
        }

        var index = Math.Clamp(tier - 1, 0, slot.CountByTier.Count - 1);
        return Math.Clamp(slot.CountByTier[index], 0, 6);
    }

    private static int RollRange(int min, int max, IRng rng) =>
        max <= min ? min : rng.NextInt(min, max + 1);

    private static string Expand(string pattern, EncounterRequest request, string factionId, EncounterCastSpec? keeper) =>
        pattern
            .Replace("{item}", request.ObjectiveName, StringComparison.OrdinalIgnoreCase)
            .Replace("{faction}", factionId, StringComparison.OrdinalIgnoreCase)
            .Replace("{region}", request.Region.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{place}", request.Region.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{keeper}", keeper?.Name ?? "its keeper", StringComparison.OrdinalIgnoreCase);

    private static T PickWeighted<T>(IReadOnlyList<T> values, Func<T, int> weight, IRng rng)
    {
        var total = values.Sum(item => Math.Max(1, weight(item)));
        var roll = rng.NextInt(0, total);
        foreach (var value in values)
        {
            roll -= Math.Max(1, weight(value));
            if (roll < 0)
            {
                return value;
            }
        }

        return values[^1];
    }
}
