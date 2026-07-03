using Sorcerer.Core.Entities;

namespace Sorcerer.Core.World;

public static class SpawnedWantFactory
{
    public static WantComponent? Create(
        string entityId,
        string entityName,
        string factionRole,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> interactableVerbs,
        bool summoned,
        bool includeMemory,
        string? aiPolicyId,
        IReadOnlyList<string> promiseIds,
        string? explicitText,
        string? explicitId,
        string explicitStatus,
        string explicitStakes,
        int explicitSalience,
        IReadOnlyList<string> explicitTags,
        bool autoWant = true)
    {
        if (!string.IsNullOrWhiteSpace(explicitText))
        {
            return new WantComponent(
                FirstNonBlank(explicitId, $"want_{NormalizeToken(entityId, "entity")}")!,
                explicitText,
                Math.Clamp(explicitSalience, 1, 5),
                FirstNonBlank(explicitStatus, "active")!,
                explicitStakes ?? "",
                explicitTags);
        }

        if (!autoWant || !ShouldGenerate(tags, roles, interactableVerbs, summoned, includeMemory, promiseIds))
        {
            return null;
        }

        var profile = ProfileFor(entityName, factionRole, tags, roles, aiPolicyId, summoned);
        return new WantComponent(
            $"want_{NormalizeToken(entityId, "entity")}",
            profile.Text,
            profile.Salience,
            "active",
            profile.Stakes,
            profile.Tags);
    }

    private static bool ShouldGenerate(
        IReadOnlyList<string> tags,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> interactableVerbs,
        bool summoned,
        bool includeMemory,
        IReadOnlyList<string> promiseIds) =>
        includeMemory
        || promiseIds.Count > 0
        || HasAny(tags, "npc", "resident", "merchant", "service_provider", "folk_magic", "prisoner", "witness", "functionary")
        || HasAny(roles, "resident", "merchant", "service_provider", "witness", "prisoner")
        || HasAny(interactableVerbs, "talk", "give", "recruit");

    private static GeneratedWantProfile ProfileFor(
        string entityName,
        string factionRole,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> roles,
        string? aiPolicyId,
        bool summoned)
    {
        if (HasAny(tags, "merchant") || HasAny(roles, "merchant"))
        {
            return new GeneratedWantProfile(
                "Complete a useful exchange without drawing dangerous attention.",
                3,
                "Fair trade can become trust, gossip, or a concrete lead; coercion can make the merchant vanish or talk.",
                Tags("merchant", "trade", "promise_source"));
        }

        if (HasAny(tags, "service_provider", "folk_magic") || HasAny(roles, "service_provider"))
        {
            return new GeneratedWantProfile(
                "Help carefully enough that folk-magic stays useful and unnamed.",
                4,
                "The right question can reveal a hush-hush service; loud exposure can put a practitioner in mortal danger.",
                Tags("service", "folk_magic", "promise_source"));
        }

        if (HasAny(tags, "threat", "hostile") || HasAny(roles, "threat") || IsHostilePolicy(aiPolicyId))
        {
            return new GeneratedWantProfile(
                "Turn the sorcerer, or the promise that called them here, into leverage.",
                4,
                "Threats may disclose motives, debts, employers, or escape routes if that creates advantage.",
                Tags("threat", "leverage"));
        }

        if (HasAny(tags, "empire", "imperial", "censorate", "patrol")
            || HasAny(roles, "empire", "censorate", "military", "patrol")
            || IsRole(factionRole, "empire_bloc"))
        {
            return new GeneratedWantProfile(
                "Contain disorder and make the incident read as proper procedure.",
                4,
                "Pressure, fear, or procedural curiosity can reveal routes, ledgers, warrants, doors, or schedules.",
                Tags("empire", "procedure", "promise_source"));
        }

        if (HasAny(tags, "hollowmere", "refuge") || IsRole(factionRole, "resistance"))
        {
            return new GeneratedWantProfile(
                "Keep local shelters safe while deciding whether this sorcerer is worth the risk.",
                4,
                "Trust, gifts, or shared enemies can draw out refuge, route, healer, kinship, or landmark leads.",
                Tags("hollowmere", "refuge", "promise_source"));
        }

        if (summoned)
        {
            return new GeneratedWantProfile(
                "Carry out the purpose that called them before the magic thins.",
                2,
                "Clear purpose can make the summoned being useful; neglect can leave it strange or aimless.",
                Tags("summoned", "purpose"));
        }

        if (HasAny(tags, "resident") || HasAny(roles, "resident"))
        {
            return new GeneratedWantProfile(
                "Keep their place intact while deciding whether the stranger is danger or opportunity.",
                3,
                "Respect, gifts, or useful trouble can draw out local names, routes, stock, or landmarks.",
                Tags("resident", "local", "promise_source"));
        }

        return new GeneratedWantProfile(
            $"Survive meeting {entityName} and learn whether the sorcerer is danger, help, or leverage.",
            2,
            "A meaningful exchange can turn this person toward fear, trust, trade, or a lead.",
            Tags("survival", "local", "promise_source"));
    }

    private static bool HasAny(IReadOnlyList<string> values, params string[] needles) =>
        values.Any(value => needles.Any(needle => value.Equals(needle, StringComparison.OrdinalIgnoreCase)));

    private static bool IsRole(string factionRole, string expected) =>
        factionRole.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsHostilePolicy(string? aiPolicyId) =>
        !string.IsNullOrWhiteSpace(aiPolicyId)
        && aiPolicyId.Contains("hostile", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Tags(params string[] tags) =>
        tags.Append("generated_want").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeToken(string text, string fallback)
    {
        var chars = (text ?? "")
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join(
            "_",
            new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private sealed record GeneratedWantProfile(
        string Text,
        int Salience,
        string Stakes,
        IReadOnlyList<string> Tags);
}
