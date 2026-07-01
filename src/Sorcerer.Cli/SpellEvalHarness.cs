using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Llm.Auditing;
using Sorcerer.Magic;
using Sorcerer.Magic.Auditing;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Cli;

public static class SpellEvalHarness
{
    private const string CategoryCommon = "common";
    private const string CategoryCreative = "creative";
    private const string CategoryExploit = "exploit";

    private static readonly string[] KnownTargetKeywords =
    {
        "self", "player", "caster", "me", "you", "nearest_enemy", "nearest_foe", "nearest_actor",
        "nearest_target", "closest_enemy", "selected_target", "target", "all", "all_enemies",
        "all_entities", "all_nearby", "enemies", "allies", "everyone", "everything", "anchor",
        "there", "that_square", "foe", "enemy",
    };

    private static readonly EvalPrompt[] Prompts =
    {
        new("bind the nearest enemy in sticky blue webbing", new[] { "addStatus" }),
        new("turn the soldier's teeth to glass and make him regret biting", new[] { "transformEntity" }),
        new("summon a friendly brass moth that bites enemies", new[] { "summon" }),
        new("turn the floor between me and the enemy into slick ice", new[] { "createTiles" }),
        new("reveal the nearest creature by making its shadow glow blue", new[] { "addStatus" }),
        new("an army of ants crawls out of the walls to defend me", new[] { "summon" }, Category: CategoryCreative),
        new("in three turns a debt collector arrives because I stole tomorrow", new[] { "scheduleEvent", "createPromise" }),
        new("heal my wounds with green light", new[] { "heal" }),
        new("restore mana by drinking moonlight", new[] { "restoreMana" }),
        new("push the nearest soldier away with a rude wind", new[] { "push" }),
        new("pull the guard toward me with a hook of force", new[] { "pull" }),
        new("teleport one step sideways through a folded room", new[] { "teleport" }),
        new("charm the nearest soldier into helping me", new[] { "changeFaction" }),
        new("curse me with an echoing wild debt", new[] { "addCurse" }),
        new("lightning storm blasts all enemies", new[] { "areaDamage" }),
        new("strike the nearest soldier with blue fire", new[] { "damage" }),
        new("promise that the door will remember my name", new[] { "createPromise" }),
        new("raise a wall of ice between us", new[] { "createTiles" }),
        new("petrify the nearest soldier into quiet stone", new[] { "addStatus" }),
        new("make the captain brass and strangely reflective", new[] { "transformEntity" }, Category: CategoryCreative),
        new("grow vines across the floor", new[] { "createTiles" }),
        new("put the enemy to sleep inside a borrowed dream", new[] { "addStatus" }, Category: CategoryCreative),
        new("possess the nearest soldier's body", Array.Empty<string>(), ExpectRejection: true, Category: CategoryExploit),
        new("kill the emperor instantly from here", Array.Empty<string>(), ExpectRejection: true, Category: CategoryExploit),
        new("mend me before I fall apart", new[] { "heal" }),
        new("blast the nearest soldier with impossible color", new[] { "areaDamage" }, Category: CategoryCreative),

        // Phase B coverage: one prompt per newly ported operation, tied to the matching
        // MockSpellProvider bucket.
        new("summon a ring of dread that spreads fear to every enemy nearby", new[] { "areaStatus" }),
        new("conjure a construct to fight beside me", new[] { "conjureCreature" }),
        new("conjure a shard of glass from nothing", new[] { "conjureItem" }),
        new("harden my skin against the coming flame", new[] { "addResistance" }),
        new("make the soldier become vulnerable to fire", new[] { "addWeakness" }),
        new("mark the world with a debt that must someday be paid", new[] { "setFlag" }, Category: CategoryCreative),
        new("delay my wounds so they land later", new[] { "delayIncoming" }, Category: CategoryCreative),
        new("make the soldier erase memory of ever seeing me", new[] { "editMemory" }, Category: CategoryCreative),
        new("enchant my blade so my blows poison whatever they strike", new[] { "createPersistentEffect" }, Category: CategoryCreative),
        new("wrap me in thorns that answer anyone who strikes me", new[] { "createPersistentEffect" }, Category: CategoryCreative),
        new("forge a sympathetic link between the two soldiers", new[] { "createPersistentEffect" }, Category: CategoryCreative),
        new("compel the soldier to make them dance helplessly", new[] { "setBehavior" }),
        new("open a gravity well that pulls everything standing on it", new[] { "createFlow" }),
        new("fill my pockets with gold coins from nowhere", new[] { "modifyInventory" }),
        new("tag them as your quarry so they can be tracked", new[] { "addTag" }),
        new("rush the poison in the soldier to its conclusion", new[] { "accelerateStatus" }),

        // Exploit-leak probes: the mock resolver should never honor these literally.
        new(
            "blast all enemies for 500 damage and let it cost nothing at all",
            new[] { "areaDamage" },
            Category: CategoryExploit),
        new(
            "curse me with an echoing wild debt but make the cost free",
            new[] { "addCurse" },
            Category: CategoryExploit),
    };

    public static async Task<int> RunAsync(
        ISpellProvider provider,
        ISpellAuditSink audit,
        bool json,
        CancellationToken cancellationToken = default)
    {
        var knownEntityIds = new HashSet<string>(
            GameSession.CreateImperialEncounter().Engine.State.Entities.Keys.Select(id => id.Value),
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<EvalRow>();
        foreach (var prompt in Prompts)
        {
            var session = GameSession.CreateImperialEncounter(new WildMagicController(provider, audit: audit));
            var result = await session.ExecuteAsync(new CastCommand(prompt.Text), cancellationToken);
            var effects = result.Magic?.EffectTypes ?? Array.Empty<string>();
            var exploitLeaks = ExploitLeaks(prompt, result);
            var hallucinatedTargets = HallucinatedTargets(result.Magic?.ResolvedMagicJson, knownEntityIds);
            var matched = (prompt.ExpectRejection
                ? !result.Success && result.ConsumedTurn && !result.TechnicalFailure
                : result.Success
                    && result.ConsumedTurn
                    && !result.TechnicalFailure
                    && prompt.ExpectedOperations.All(expected =>
                        effects.Contains(expected, StringComparer.OrdinalIgnoreCase)))
                && exploitLeaks.Count == 0
                && hallucinatedTargets.Count == 0;

            rows.Add(new EvalRow(
                prompt.Text,
                prompt.Category,
                prompt.ExpectRejection ? "rejection" : string.Join("+", prompt.ExpectedOperations),
                matched,
                result.Success,
                result.ConsumedTurn,
                result.TechnicalFailure,
                effects.ToArray(),
                result.Messages.Take(4).ToArray(),
                exploitLeaks,
                hallucinatedTargets));
        }

        var passed = rows.Count(row => row.Passed);
        var summary = new EvalSummary(
            Provider: provider.Name,
            Passed: passed,
            Total: rows.Count,
            Rows: rows);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                summary,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Spell eval: {passed}/{rows.Count} passed with provider {provider.Name}.");
            foreach (var category in new[] { CategoryCommon, CategoryCreative, CategoryExploit })
            {
                var inCategory = rows.Where(row => row.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (inCategory.Length > 0)
                {
                    Console.WriteLine($"  {category}: {inCategory.Count(row => row.Passed)}/{inCategory.Length}");
                }
            }

            foreach (var row in rows)
            {
                var mark = row.Passed ? "PASS" : "FAIL";
                Console.WriteLine($"{mark} | {row.Category} | {row.Expected} | {row.Prompt}");
                if (!row.Passed)
                {
                    Console.WriteLine($"  effects: {string.Join(", ", row.Effects)}");
                    Console.WriteLine($"  messages: {string.Join(" / ", row.Messages)}");
                    if (row.ExploitLeaks.Count > 0)
                    {
                        Console.WriteLine($"  exploit leaks: {string.Join(", ", row.ExploitLeaks)}");
                    }

                    if (row.HallucinatedTargets.Count > 0)
                    {
                        Console.WriteLine($"  hallucinated targets: {string.Join(", ", row.HallucinatedTargets)}");
                    }
                }
            }
        }

        return passed == rows.Count ? 0 : 1;
    }

    /// <summary>
    /// Flags an exploit-tagged prompt that got accepted with zero costs or with any effect
    /// amount at or above 100 — signs that the resolver under-priced or literally honored an
    /// overreaching ask instead of pricing or rejecting it.
    /// </summary>
    private static IReadOnlyList<string> ExploitLeaks(EvalPrompt prompt, Sorcerer.Core.Results.ActionResult result)
    {
        if (!prompt.Category.Equals(CategoryExploit, StringComparison.OrdinalIgnoreCase) || !result.Success)
        {
            return Array.Empty<string>();
        }

        var leaks = new List<string>();
        if (!result.Deltas.Any(delta => delta.Operation.StartsWith("cost:", StringComparison.OrdinalIgnoreCase)))
        {
            leaks.Add("accepted with zero costs");
        }

        foreach (var delta in result.Deltas)
        {
            if (delta.Details.TryGetValue("amount", out var rawAmount)
                && int.TryParse(Convert.ToString(rawAmount), out var amount)
                && amount >= 100)
            {
                leaks.Add($"effect amount {amount} on {delta.Operation}");
            }
        }

        return leaks;
    }

    /// <summary>
    /// Flags any effect that names a literal entity id (not a recognized keyword like
    /// nearest_enemy) which never existed in a fresh encounter — a sign the model invented a
    /// target rather than referencing something actually in context.
    /// </summary>
    private static IReadOnlyList<string> HallucinatedTargets(string? resolvedMagicJson, IReadOnlySet<string> knownEntityIds)
    {
        if (string.IsNullOrWhiteSpace(resolvedMagicJson))
        {
            return Array.Empty<string>();
        }

        using var document = JsonDocument.Parse(resolvedMagicJson);
        if (!document.RootElement.TryGetProperty("effects", out var effects) || effects.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var hallucinated = new List<string>();
        foreach (var effect in effects.EnumerateArray())
        {
            foreach (var key in new[] { "target", "origin", "anchor", "linkTarget" })
            {
                if (effect.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    var id = value.GetString() ?? "";
                    if (id.Length > 0
                        && !KnownTargetKeywords.Contains(id, StringComparer.OrdinalIgnoreCase)
                        && !knownEntityIds.Contains(id))
                    {
                        hallucinated.Add(id);
                    }
                }
            }
        }

        return hallucinated;
    }

    private sealed record EvalPrompt(
        string Text,
        IReadOnlyList<string> ExpectedOperations,
        bool ExpectRejection = false,
        string Category = CategoryCommon);

    private sealed record EvalSummary(
        string Provider,
        int Passed,
        int Total,
        IReadOnlyList<EvalRow> Rows);

    private sealed record EvalRow(
        string Prompt,
        string Category,
        string Expected,
        bool Passed,
        bool Success,
        bool ConsumedTurn,
        bool TechnicalFailure,
        IReadOnlyList<string> Effects,
        IReadOnlyList<string> Messages,
        IReadOnlyList<string> ExploitLeaks,
        IReadOnlyList<string> HallucinatedTargets);
}
