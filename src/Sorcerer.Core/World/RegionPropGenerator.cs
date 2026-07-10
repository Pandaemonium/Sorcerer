using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record GeneratedRegionProp(
    string Name,
    char Glyph,
    string FixtureType,
    string Material,
    IReadOnlyList<string> Tags,
    bool BlocksMovement,
    bool BlocksSight,
    bool CanAnchorMagic,
    string Description,
    string? ReadableTitle = null,
    string? ReadableText = null,
    string? EnsembleId = null,
    int OffsetX = 0,
    int OffsetY = 0);

public sealed record RegionPropBatch(
    IReadOnlyList<GeneratedRegionProp> Props,
    bool Dense,
    string? EnsembleId);

public static class RegionPropGenerator
{
    public static RegionPropBatch Generate(
        RegionDefinition region,
        RealmProfile realm,
        int worldSeed,
        string zoneId)
    {
        if (region.Props is null)
        {
            return new RegionPropBatch(Array.Empty<GeneratedRegionProp>(), Dense: false, EnsembleId: null);
        }

        var grammar = region.Props;
        var rng = new DeterministicRng(WorldRoll.StableSeed(worldSeed, zoneId, region.Id, "props"));
        if (RollPercent(rng, grammar.EmptyChancePercent))
        {
            return new RegionPropBatch(Array.Empty<GeneratedRegionProp>(), Dense: false, EnsembleId: null);
        }

        var dense = RollPercent(rng, grammar.DenseChancePercent);
        var targetCount = grammar.MinProps == grammar.MaxProps
            ? grammar.MinProps
            : rng.NextInt(grammar.MinProps, grammar.MaxProps + 1);
        if (dense)
        {
            targetCount = Math.Min(16, targetCount + grammar.DenseBonus);
        }

        var props = new List<GeneratedRegionProp>(targetCount);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? ensembleId = null;
        var ensembles = grammar.Ensembles ?? Array.Empty<RegionPropEnsembleDefinition>();
        if (targetCount > 0 && ensembles.Count > 0 && RollPercent(rng, grammar.EnsembleChancePercent))
        {
            var ensemble = PickWeighted(ensembles, item => item.Weight, rng);
            ensembleId = ensemble.Id;
            foreach (var member in ensemble.Members.Take(targetCount))
            {
                var prop = ComposeEnsembleMember(region, realm, grammar, ensemble, member, rng);
                if (prop is null || !usedNames.Add(prop.Name))
                {
                    continue;
                }

                props.Add(prop);
            }
        }

        var attempts = 0;
        while (props.Count < targetCount && attempts++ < targetCount * 20)
        {
            var prop = Compose(
                region,
                realm,
                PickWeighted(grammar.Bases, item => item.Weight, rng),
                PickWeighted(grammar.Materials, item => item.Weight, rng),
                PickWeighted(grammar.Conditions, item => item.Weight, rng),
                grammar,
                rng);
            if (usedNames.Add(prop.Name))
            {
                props.Add(prop);
            }
        }

        return new RegionPropBatch(props, dense, ensembleId);
    }

    private static GeneratedRegionProp? ComposeEnsembleMember(
        RegionDefinition region,
        RealmProfile realm,
        RegionPropGrammarDefinition grammar,
        RegionPropEnsembleDefinition ensemble,
        RegionPropEnsembleMemberDefinition member,
        IRng rng)
    {
        var propBase = grammar.Bases.FirstOrDefault(item => item.Id.Equals(member.BaseId, StringComparison.OrdinalIgnoreCase));
        var material = grammar.Materials.FirstOrDefault(item => item.Id.Equals(member.MaterialId, StringComparison.OrdinalIgnoreCase));
        var condition = grammar.Conditions.FirstOrDefault(item => item.Id.Equals(member.ConditionId, StringComparison.OrdinalIgnoreCase));
        return propBase is null || material is null || condition is null
            ? null
            : Compose(region, realm, propBase, material, condition, grammar, rng) with
            {
                EnsembleId = ensemble.Id,
                OffsetX = member.OffsetX,
                OffsetY = member.OffsetY,
            };
    }

    private static GeneratedRegionProp Compose(
        RegionDefinition region,
        RealmProfile realm,
        RegionPropBaseDefinition propBase,
        RegionPropPartDefinition material,
        RegionPropPartDefinition condition,
        RegionPropGrammarDefinition grammar,
        IRng rng)
    {
        var name = string.Join(
            " ",
            new[] { condition.Text, material.Text, propBase.Name }
                .Where(part => !string.IsNullOrWhiteSpace(part)))
            .Trim();
        var description = string.Join(
            " ",
            new[] { propBase.Description, material.Description, condition.Description }
                .Where(part => !string.IsNullOrWhiteSpace(part)))
            .Trim();
        var tags = region.TerrainTags
            .Concat(region.VoiceTags)
            .Concat(propBase.Tags)
            .Concat(material.Tags)
            .Concat(condition.Tags)
            .Concat(new[] { "generated", "semantic_prop", "scenery", region.Id, region.RealmId, region.TraditionId })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var materialId = string.IsNullOrWhiteSpace(material.Material) ? material.Id : material.Material;
        var canAnchorMagic = false;
        string? readableTitle = null;
        string? readableText = null;
        var hooks = grammar.Hooks ?? Array.Empty<RegionPropHookDefinition>();
        if (hooks.Count > 0 && RollPercent(rng, grammar.HookChancePercent))
        {
            var hook = PickWeighted(hooks, item => item.Weight, rng);
            tags.Add("context_hook");
            tags.AddRange(hook.Tags ?? Array.Empty<string>());
            if (hook.Kind.Equals("anchor", StringComparison.OrdinalIgnoreCase))
            {
                canAnchorMagic = true;
                tags.Add("magic_anchor");
            }
            else if (hook.Kind.Equals("readable", StringComparison.OrdinalIgnoreCase))
            {
                readableTitle = RenderTemplate(hook.Title ?? name, name, region, realm);
                readableText = RenderTemplate(
                    hook.Text ?? $"{readableTitle}: local hands left a record here.",
                    name,
                    region,
                    realm);
                tags.Add("readable");
            }
        }

        return new GeneratedRegionProp(
            name,
            propBase.Glyph,
            propBase.FixtureType,
            materialId!,
            tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            propBase.BlocksMovement,
            propBase.BlocksSight,
            canAnchorMagic,
            description,
            readableTitle,
            readableText);
    }

    private static string RenderTemplate(
        string template,
        string name,
        RegionDefinition region,
        RealmProfile realm) =>
        template
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{region}", region.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{realm}", realm.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{ruler}", realm.Ruler, StringComparison.OrdinalIgnoreCase)
            .Replace("{status}", realm.Status, StringComparison.OrdinalIgnoreCase);

    private static bool RollPercent(IRng rng, int chance) =>
        chance > 0 && rng.NextInt(0, 100) < Math.Clamp(chance, 0, 100);

    private static T PickWeighted<T>(IReadOnlyList<T> values, Func<T, int> weight, IRng rng)
    {
        var total = values.Sum(item => Math.Max(1, weight(item)));
        var roll = rng.NextInt(0, total);
        foreach (var item in values)
        {
            roll -= Math.Max(1, weight(item));
            if (roll < 0)
            {
                return item;
            }
        }

        return values[^1];
    }
}
