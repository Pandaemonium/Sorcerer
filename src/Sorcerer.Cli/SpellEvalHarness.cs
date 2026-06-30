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
    private static readonly EvalPrompt[] Prompts =
    {
        new("bind the nearest enemy in sticky blue webbing", new[] { "addStatus" }),
        new("turn the soldier's teeth to glass and make him regret biting", new[] { "transformEntity" }),
        new("summon a friendly brass moth that bites enemies", new[] { "summon" }),
        new("turn the floor between me and the enemy into slick ice", new[] { "createTiles" }),
        new("reveal the nearest creature by making its shadow glow blue", new[] { "addStatus" }),
        new("an army of ants crawls out of the walls to defend me", new[] { "summon" }),
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
        new("make the captain brass and strangely reflective", new[] { "transformEntity" }),
        new("grow vines across the floor", new[] { "createTiles" }),
        new("put the enemy to sleep inside a borrowed dream", new[] { "addStatus" }),
        new("kill the emperor instantly from here", Array.Empty<string>(), ExpectRejection: true),
        new("mend me before I fall apart", new[] { "heal" }),
        new("blast the nearest soldier with impossible color", new[] { "areaDamage" }),
    };

    public static async Task<int> RunAsync(
        ISpellProvider provider,
        ISpellAuditSink audit,
        bool json,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<EvalRow>();
        foreach (var prompt in Prompts)
        {
            var session = GameSession.CreateImperialEncounter(new WildMagicController(provider, audit: audit));
            var result = await session.ExecuteAsync(new CastCommand(prompt.Text), cancellationToken);
            var effects = result.Magic?.EffectTypes ?? Array.Empty<string>();
            var matched = prompt.ExpectRejection
                ? !result.Success && result.ConsumedTurn && !result.TechnicalFailure
                : result.Success
                    && result.ConsumedTurn
                    && !result.TechnicalFailure
                    && prompt.ExpectedOperations.All(expected =>
                        effects.Contains(expected, StringComparer.OrdinalIgnoreCase));

            rows.Add(new EvalRow(
                prompt.Text,
                prompt.ExpectRejection ? "rejection" : string.Join("+", prompt.ExpectedOperations),
                matched,
                result.Success,
                result.ConsumedTurn,
                result.TechnicalFailure,
                effects.ToArray(),
                result.Messages.Take(4).ToArray()));
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
            foreach (var row in rows)
            {
                var mark = row.Passed ? "PASS" : "FAIL";
                Console.WriteLine($"{mark} | {row.Expected} | {row.Prompt}");
                if (!row.Passed)
                {
                    Console.WriteLine($"  effects: {string.Join(", ", row.Effects)}");
                    Console.WriteLine($"  messages: {string.Join(" / ", row.Messages)}");
                }
            }
        }

        return passed == rows.Count ? 0 : 1;
    }

    private sealed record EvalPrompt(
        string Text,
        IReadOnlyList<string> ExpectedOperations,
        bool ExpectRejection = false);

    private sealed record EvalSummary(
        string Provider,
        int Passed,
        int Total,
        IReadOnlyList<EvalRow> Rows);

    private sealed record EvalRow(
        string Prompt,
        string Expected,
        bool Passed,
        bool Success,
        bool ConsumedTurn,
        bool TechnicalFailure,
        IReadOnlyList<string> Effects,
        IReadOnlyList<string> Messages);
}
