using Sorcerer.Core.Magic;

namespace Sorcerer.Core.Characters;

/// <summary>
/// The rules of character creation, shared by the GUI screen, the CLI flags, and tests.
/// Spend is additive-only on top of the origin baseline (a build can never drop below the
/// origin's stats), capped per stat and by a shared point pool. Validation here is
/// prevention-by-construction: <see cref="Sanitize"/> never throws, so no caller can
/// produce an illegal character even from hostile input.
/// </summary>
public static class CreationRules
{
    /// <summary>Points spendable on top of the origin baseline.</summary>
    public const int PointPool = 3;

    /// <summary>Per-stat ceiling. Origins ship 2–5; the resolver treats ≥5 as the top band,
    /// so 6 lets a specialist push one notch past it (docs/CHARACTER_AND_STATS.md).</summary>
    public const int StatCap = 6;

    /// <summary>A zero-spend build of the given origin: stats at baseline, texts inherited.</summary>
    public static CharacterBuild FromOrigin(OriginDefinition origin) =>
        new(origin.Id, origin.BodyVigor, origin.SoulAttunement, origin.SoulComposure);

    public static int PointsSpent(OriginDefinition origin, CharacterBuild build) =>
        Math.Max(0, build.Vigor - origin.BodyVigor)
        + Math.Max(0, build.Attunement - origin.SoulAttunement)
        + Math.Max(0, build.Composure - origin.SoulComposure);

    public static bool IsLegal(OriginDefinition origin, CharacterBuild build) =>
        build.Vigor >= origin.BodyVigor && build.Vigor <= StatCap
        && build.Attunement >= origin.SoulAttunement && build.Attunement <= StatCap
        && build.Composure >= origin.SoulComposure && build.Composure <= StatCap
        && PointsSpent(origin, build) <= PointPool;

    /// <summary>
    /// Coerce any build into a legal one: unknown origin falls back to the catalog default,
    /// stats clamp into [baseline, cap], excess spend is stripped deterministically
    /// (vigor first, then attunement, then composure), unknown bonus spells are dropped,
    /// and blank text fields normalize to null so origin defaults show through.
    /// </summary>
    public static CharacterBuild Sanitize(CharacterBuild build, OriginCatalog catalog, CharterSpellbook spellbook)
    {
        var origin = catalog.Resolve(build.OriginId);
        var vigor = Math.Clamp(build.Vigor, origin.BodyVigor, StatCap);
        var attunement = Math.Clamp(build.Attunement, origin.SoulAttunement, StatCap);
        var composure = Math.Clamp(build.Composure, origin.SoulComposure, StatCap);

        var excess = Math.Max(0, vigor - origin.BodyVigor)
            + Math.Max(0, attunement - origin.SoulAttunement)
            + Math.Max(0, composure - origin.SoulComposure)
            - PointPool;
        while (excess > 0)
        {
            if (vigor > origin.BodyVigor)
            {
                vigor--;
            }
            else if (attunement > origin.SoulAttunement)
            {
                attunement--;
            }
            else
            {
                composure--;
            }

            excess--;
        }

        var bonusSpell = spellbook.Find(build.BonusCharterSpellId)?.Id;
        return new CharacterBuild(
            origin.Id,
            vigor,
            attunement,
            composure,
            NonBlank(build.Name),
            NonBlank(build.Appearance),
            NonBlank(build.Backstory),
            NonBlank(build.MagicalSignature),
            bonusSpell,
            NonBlank(build.PortraitPath));
    }

    /// <summary>
    /// The single seam through which a build changes the game: fold it into the origin
    /// definition, and everything downstream (soul, actor, profile, inventory) derives
    /// from the result unchanged. Null build means the origin passes through untouched.
    /// </summary>
    public static OriginDefinition EffectiveOrigin(OriginDefinition origin, CharacterBuild? build)
    {
        if (build is null)
        {
            return origin;
        }

        return origin with
        {
            BodyVigor = build.Vigor,
            SoulAttunement = build.Attunement,
            SoulComposure = build.Composure,
            PublicName = NonBlank(build.Name) ?? origin.PublicName,
            Appearance = NonBlank(build.Appearance) ?? origin.Appearance,
            MagicalSignature = NonBlank(build.MagicalSignature) ?? origin.MagicalSignature,
            Backstory = NonBlank(build.Backstory) ?? origin.Backstory,
            StartingCharterSpells = WithBonusSpell(origin.StartingCharterSpells, build.BonusCharterSpellId),
        };
    }

    /// <summary>Random origin plus a random legal point spend; texts stay null so the
    /// origin's own prose shows through. Seedable via the supplied rng.</summary>
    public static CharacterBuild RandomBuild(OriginCatalog catalog, Random rng)
    {
        var origins = catalog.Origins.OrderBy(origin => origin.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var origin = origins[rng.Next(origins.Length)];
        var stats = new[] { origin.BodyVigor, origin.SoulAttunement, origin.SoulComposure };
        for (var point = 0; point < PointPool; point++)
        {
            var open = Enumerable.Range(0, stats.Length).Where(i => stats[i] < StatCap).ToArray();
            if (open.Length == 0)
            {
                break;
            }

            stats[open[rng.Next(open.Length)]]++;
        }

        return new CharacterBuild(origin.Id, stats[0], stats[1], stats[2]);
    }

    private static IReadOnlyList<string> WithBonusSpell(IReadOnlyList<string>? seeded, string? bonusId)
    {
        var spells = new List<string>(seeded ?? Array.Empty<string>());
        if (!string.IsNullOrWhiteSpace(bonusId)
            && !spells.Contains(bonusId, StringComparer.OrdinalIgnoreCase))
        {
            spells.Add(bonusId);
        }

        return spells;
    }

    private static string? NonBlank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
