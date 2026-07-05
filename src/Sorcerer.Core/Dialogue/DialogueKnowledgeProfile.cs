using Sorcerer.Core.Entities;

namespace Sorcerer.Core.Dialogue;

public static class DialogueKnowledgeProfile
{
    public static KnowledgeComponent For(
        Entity entity,
        string regionId,
        IReadOnlyDictionary<string, int>? overrides = null)
    {
        var topics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["zone.current"] = 1,
            ["current_zone"] = 1,
            ["zone.npcs"] = 1,
            ["scene.object"] = 1,
            ["npc.relationship"] = 1,
            ["claims"] = 1,
            ["services"] = 1,
            ["region.travel"] = 1,
            ["promise.hooks"] = 1,
        };

        var tags = TagsFor(entity);
        var roles = entity.TryGet<FactionComponent>(out var faction)
            ? faction.Roles
            : Array.Empty<string>();
        var factionId = entity.TryGet<ActorComponent>(out var actor)
            ? actor.Faction
            : faction?.FactionId ?? "";
        var profileOrigin = entity.TryGet<ProfileComponent>(out var profile)
            ? profile.Origin
            : "";

        if (HasAny(tags, roles, "resident", "prisoner", "witness", "hollowmere")
            || profileOrigin.Equals("Hollowmere", StringComparison.OrdinalIgnoreCase)
            || regionId.Contains("hollowmere", StringComparison.OrdinalIgnoreCase))
        {
            SetAtLeast(topics, "rumors", 2);
            SetAtLeast(topics, "npc.knowledge.region", 2);
            SetAtLeast(topics, "hollowmere", 2);
            SetAtLeast(topics, "people.hollowmere", 2);
            SetAtLeast(topics, "folk_magic.water", 1);
            SetAtLeast(topics, "magic.water.hollowmere", 1);
            SetAtLeast(topics, "vigovia.public_law", 1);
            SetAtLeast(topics, "services.folk_magic", 1);
            SetAtLeast(topics, "promises.oaths", 2);
            SetAtLeast(topics, "region.travel", 2);
            SetAtLeast(topics, "promise.hooks", 2);
            SetAtLeast(topics, "recent.magic_deeds", 1);
        }

        if (HasAny(tags, roles, "imperial", "law", "soldier", "empire", "military", "containment")
            || factionId.Equals("empire", StringComparison.OrdinalIgnoreCase)
            || profileOrigin.Equals("Vigovia", StringComparison.OrdinalIgnoreCase))
        {
            SetAtLeast(topics, "faction.law", 2);
            SetAtLeast(topics, "vigovia.public_law", 2);
            SetAtLeast(topics, "vigovia.procedure", 2);
            SetAtLeast(topics, "npc.knowledge.region", 1);
            SetAtLeast(topics, "rumors", 1);
            SetAtLeast(topics, "recent.magic_deeds", 1);
        }

        if (HasAny(tags, roles, "captain", "officer", "ward-captain", "functionary"))
        {
            SetAtLeast(topics, "faction.law", 3);
            SetAtLeast(topics, "vigovia.procedure", 3);
        }

        if (HasAny(tags, roles, "merchant", "trader", "seller") || entity.Has<MerchantComponent>())
        {
            SetAtLeast(topics, "services", 2);
            SetAtLeast(topics, "services.market", 2);
            SetAtLeast(topics, "claims", 2);
            SetAtLeast(topics, "region.travel", 2);
        }

        if (entity.Has<ServiceComponent>())
        {
            SetAtLeast(topics, "services", 2);
        }

        if (entity.TryGet<WantComponent>(out var want)
            && want.Tags.Contains("promise_source", StringComparer.OrdinalIgnoreCase))
        {
            SetAtLeast(topics, "promise.hooks", 2);
        }

        foreach (var pair in overrides ?? new Dictionary<string, int>())
        {
            topics[pair.Key] = Math.Max(0, pair.Value);
        }

        return new KnowledgeComponent(topics);
    }

    private static IReadOnlyList<string> TagsFor(Entity entity) =>
        entity.TryGet<TagsComponent>(out var tags) ? tags.Tags : Array.Empty<string>();

    private static bool HasAny(
        IReadOnlyList<string> tags,
        IReadOnlyList<string> roles,
        params string[] values) =>
        values.Any(value =>
            tags.Contains(value, StringComparer.OrdinalIgnoreCase)
            || roles.Contains(value, StringComparer.OrdinalIgnoreCase));

    private static void SetAtLeast(Dictionary<string, int> topics, string topic, int tier)
    {
        if (!topics.TryGetValue(topic, out var existing) || existing < tier)
        {
            topics[topic] = tier;
        }
    }
}
