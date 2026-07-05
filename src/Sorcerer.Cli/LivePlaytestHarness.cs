using System.Diagnostics;
using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Views;
using Sorcerer.Magic;
using Sorcerer.Magic.Auditing;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Cli;

public sealed record LivePlaytestOptions(
    int Episodes,
    int Seed,
    int MinSpells,
    int MinDialogues,
    int MinNpcs,
    int MaxSteps,
    int BudgetSeconds,
    string? CheckpointPath,
    string? LogPath);

/// <summary>
/// Drives many wild spells and dialogue rounds through the live providers to stress-test the game
/// and gather feel data. It targets >=MinSpells casts and >=MinDialogues dialogue rounds across
/// >=MinNpcs distinct speakers per episode, travelling between zones to spawn resident NPCs, and
/// collects every invariant issue instead of stopping at the first.
///
/// Because a live episode outlasts a single foreground window, the run is checkpoint/resumable: it
/// works until <see cref="LivePlaytestOptions.BudgetSeconds"/> of wall-clock elapses, saves the game
/// (via the engine's own SaveCommand) plus its counters, and exits with code 2 ("paused"). Re-running
/// the same command resumes exactly where it left off. Per-step progress streams to stderr so a
/// foreground caller can watch along.
/// </summary>
public static class LivePlaytestHarness
{
    private static readonly string[] SpellBank =
    {
        "raise a wall of ice between me and the nearest enemy",
        "strike the nearest enemy with blue fire",
        "bind the nearest enemy in sticky blue webbing",
        "summon a friendly brass moth to fight beside me",
        "turn the floor beneath the nearest enemy into slick ice",
        "heal my wounds with warm green light",
        "push the nearest enemy away with a rude wind",
        "pull the nearest enemy toward me with a hook of force",
        "teleport one step sideways through a folded room",
        "curse me with an echoing wild debt",
        "grow thorned vines across the floor around me",
        "conjure a shard of singing glass from nothing",
        "make the nearest enemy vulnerable to fire",
        "harden my skin against the coming blow",
        "reveal the nearest hidden thing by making its shadow glow blue",
        "put the nearest enemy to sleep inside a borrowed dream",
        "promise that this door will remember my name",
        "in three turns a debt collector arrives because I stole tomorrow",
        "wreath my hand in lightning and strike the nearest foe",
        "turn the nearest soldier's teeth to glass and make him regret it",
        "call up a swarm of biting ants from the floor",
        "raise a jagged stone wall to the north",
        "mark the nearest enemy as my quarry so I can track it",
        "drain the warmth from the nearest foe until it slows",
        "call down a small crackling storm on all nearby enemies",
        "whisper a small mote of light into the dark",
        "wither the nearest enemy to grey dust with a fistful of grave salt",
        "charm the nearest soldier into helping me",
    };

    private static readonly string[] DialogueLines =
    {
        "what do you know about this place and the people who live here?",
        "is there a road, town, or hidden way somewhere near here?",
        "who rules here, and do they fear the empire's censors?",
        "have you anything to sell, or a service you can offer me?",
        "tell me a rumor worth knowing about these parts.",
        "what do you know of the old crystal tradition of Stalnaz?",
        "have you ever heard of Hollowmere and its reed memory?",
        "is there someone nearby who could sell me a fine blade?",
        "what should a stranger fear most on the roads around here?",
        "will you remember me kindly if I do right by you?",
    };

    private static readonly Direction[] TravelCycle =
    {
        Direction.East, Direction.South, Direction.West, Direction.North,
        Direction.East, Direction.North, Direction.West, Direction.South,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static async Task<int> RunAsync(
        ISpellProvider provider,
        IDialogueProvider dialogueProvider,
        IDialogueRouter dialogueRouter,
        IDialogueParserRouter dialogueParserRouter,
        IDialogueParser dialogueParser,
        IDialogueAuditSink dialogueAudit,
        ISpellAuditSink audit,
        IBackgroundTextGenerator? backgroundTextGenerator,
        LivePlaytestOptions options,
        bool json,
        CancellationToken cancellationToken = default)
    {
        _ = dialogueRouter;
        _ = dialogueParserRouter;
        var stopwatch = Stopwatch.StartNew();
        var budget = options.BudgetSeconds > 0 ? options.BudgetSeconds : int.MaxValue;
        var episodes = Math.Max(1, options.Episodes);

        var checkpoint = LoadCheckpoint(options.CheckpointPath);
        var completed = checkpoint?.Completed ?? new List<PlaytestResult>();
        var startEpisode = checkpoint?.Current?.Episode ?? checkpoint?.NextEpisode ?? 1;

        StreamWriter? writer = OpenLog(options.LogPath, append: checkpoint is not null);

        try
        {
            for (var episode = startEpisode; episode <= episodes; episode++)
            {
                var seed = options.Seed + episode - 1;
                var session = GameSession.CreateImperialEncounter(
                    new WildMagicController(provider, audit: audit),
                    seed: seed,
                    dialogueRouter: null,
                    dialogueParserRouter: null,
                    dialogueParser: dialogueParser,
                    dialogueProvider: dialogueProvider,
                    dialogueAudit: dialogueAudit,
                    backgroundTextGenerator: backgroundTextGenerator);
                Program.ApplyQuickstart(session, "social");

                var resume = checkpoint?.Current?.Episode == episode ? checkpoint.Current : null;
                if (resume is not null)
                {
                    await session.ExecuteAsync(new LoadCommand(resume.GameSavePath), cancellationToken);
                    Console.Error.WriteLine($"[resume] episode {episode}: spells {resume.Spells}, dialogues {resume.Dialogues}, npcs {resume.NpcSouls.Count}");
                }

                var outcome = await RunEpisodeLoop(episode, seed, session, options, stopwatch, budget, resume, writer, cancellationToken);

                if (outcome.Paused)
                {
                    SaveCheckpoint(options.CheckpointPath, new CheckpointState(episode, completed, outcome.Pending));
                    Console.Error.WriteLine(
                        $"[paused] episode {episode} after {stopwatch.Elapsed.TotalSeconds:F0}s: "
                        + $"spells {outcome.Pending!.Spells}/{options.MinSpells} dialogues {outcome.Pending.Dialogues}/{options.MinDialogues} "
                        + $"npcs {outcome.Pending.NpcSouls.Count}/{options.MinNpcs}. Rerun the same command to continue.");
                    return 2;
                }

                completed.Add(outcome.Result!);
                SaveCheckpoint(options.CheckpointPath, new CheckpointState(episode + 1, completed, null));
                Console.Error.WriteLine(
                    $"[done] episode {episode}: spells {outcome.Result!.Spells} dialogues {outcome.Result.Dialogues} "
                    + $"npcs {outcome.Result.DistinctNpcs} steps {outcome.Result.Steps} issues {outcome.Result.Issues.Count} met {outcome.Result.MetTargets}");
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }

        var report = new PlaytestReport(
            provider.Name,
            completed.Count,
            completed.Count(r => r.MetTargets),
            completed.Sum(r => r.Spells),
            completed.Sum(r => r.Dialogues),
            completed.SelectMany(r => r.Issues).Count(),
            completed);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        DeleteCheckpoint(options.CheckpointPath);
        return report.Episodes == completed.Count(r => r.MetTargets) ? 0 : 1;
    }

    private static async Task<EpisodeOutcome> RunEpisodeLoop(
        int episode,
        int seed,
        GameSession session,
        LivePlaytestOptions options,
        Stopwatch stopwatch,
        int budgetSeconds,
        CurrentEpisodeState? resume,
        StreamWriter? writer,
        CancellationToken cancellationToken)
    {
        var issues = new List<string>(resume?.Issues ?? new List<string>());
        var feel = new List<string>(resume?.Feel ?? new List<string>());
        var npcSouls = new HashSet<string>(resume?.NpcSouls ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var roundsByNpc = new Dictionary<string, int>(resume?.Rounds ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
        var spells = resume?.Spells ?? 0;
        var dialogues = resume?.Dialogues ?? 0;
        var spellIdx = resume?.SpellIdx ?? seed % SpellBank.Length;
        var lineIdx = resume?.LineIdx ?? seed % DialogueLines.Length;
        var travelIdx = resume?.TravelIdx ?? 0;
        var startStep = resume?.StepsTaken ?? 0;
        var gameSavePath = resume?.GameSavePath ?? $"{options.CheckpointPath ?? Path.Combine("runs", "playtest_ckpt")}.game.json";
        var lastMessages = new Queue<string>();
        var stepsTaken = startStep;
        var lastWasTravel = false;
        var lastProgressStep = startStep;
        var progressSignature = (spells, dialogues, npcSouls.Count);

        for (var step = startStep; step < options.MaxSteps; step++)
        {
            var met = spells >= options.MinSpells && dialogues >= options.MinDialogues && npcSouls.Count >= options.MinNpcs;
            if (met)
            {
                break;
            }

            if (stopwatch.Elapsed.TotalSeconds >= budgetSeconds)
            {
                await session.ExecuteAsync(new SaveCommand(gameSavePath), cancellationToken);
                var pending = new CurrentEpisodeState(
                    episode, seed, spells, dialogues, stepsTaken, spellIdx, lineIdx, travelIdx,
                    npcSouls.ToList(), new Dictionary<string, int>(roundsByNpc), issues, feel, gameSavePath);
                return new EpisodeOutcome(true, null, pending);
            }

            var view = session.View();
            var player = PlayerCard(view);
            if (player is null)
            {
                break;
            }

            var needNewNpc = npcSouls.Count < options.MinNpcs;
            var needRounds = dialogues < options.MinDialogues;
            var needSocial = needNewNpc || needRounds;
            var needSpells = spells < options.MinSpells;

            GameCommand command;
            EntityCard? talkTarget = null;
            var adjacent = AdjacentTalker(view, player);
            var talkThisStep = needSocial
                && adjacent is not null
                && (!npcSouls.Contains(SoulOf(session, adjacent.Id)) || needRounds)
                && RoundsWith(roundsByNpc, SoulOf(session, adjacent.Id)) < 3
                && (!needSpells || step % 2 == 1 || !needNewNpc);

            if (talkThisStep && adjacent is not null)
            {
                talkTarget = adjacent;
                command = new TalkCommand($"{adjacent.Name}, {DialogueLines[lineIdx++ % DialogueLines.Length]}");
            }
            else if (needSpells && (step % 2 == 0 || !needSocial))
            {
                command = new CastCommand(SpellBank[spellIdx++ % SpellBank.Length], CastPerformance.Neutral);
            }
            else if (needSocial)
            {
                var visible = VisibleTalker(view, player, needNewNpc ? npcSouls : null, session);
                if (visible is not null && Distance(player, visible) > 1)
                {
                    command = new MoveCommand(DirectionToward(player, visible));
                }
                else if (lastWasTravel)
                {
                    // A resident spawns on travel but is not perceived until we look; refresh
                    // perception before travelling onward so newly-arrived NPCs are actually found.
                    command = new InspectCommand();
                }
                else
                {
                    command = new TravelCommand(TravelCycle[travelIdx++ % TravelCycle.Length]);
                }
            }
            else
            {
                command = new CastCommand(SpellBank[spellIdx++ % SpellBank.Length], CastPerformance.Neutral);
            }

            ActionResult result;
            try
            {
                result = await session.ExecuteAsync(command, cancellationToken);
                if (session.Observation().PendingCast is not null)
                {
                    result = await session.ExecuteAsync(new AwaitCastCommand(), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                issues.Add($"step {step} {command.GetType().Name} threw {ex.GetType().Name}: {ex.Message}");
                stepsTaken = step + 1;
                continue;
            }

            if (command is CastCommand cast)
            {
                spells++;
                if (result.Success
                    && !result.Deltas.Any(d => d.Operation.StartsWith("cost:", StringComparison.OrdinalIgnoreCase))
                    && !result.Messages.Any(m => m.Contains("Cost: nothing", StringComparison.OrdinalIgnoreCase)))
                {
                    feel.Add($"free cast: {cast.Text}");
                }
            }

            if (command is TalkCommand && talkTarget is not null && result.Dialogue is not null)
            {
                dialogues++;
                var soul = SoulOf(session, talkTarget.Id);
                npcSouls.Add(soul);
                roundsByNpc[soul] = RoundsWith(roundsByNpc, soul) + 1;
            }

            issues.AddRange(CheckInvariants(step, result, session.Engine.ValidateState()));

            foreach (var message in result.Messages.Take(2))
            {
                if (lastMessages.Contains(message) && message.Length > 25)
                {
                    feel.Add($"repeated message: {message}");
                }

                lastMessages.Enqueue(message);
                while (lastMessages.Count > 8)
                {
                    lastMessages.Dequeue();
                }
            }

            Console.Error.WriteLine(
                $"[ep{episode} s{step} {stopwatch.Elapsed.TotalSeconds:F0}s sp{spells} dl{dialogues} np{npcSouls.Count}] "
                + $"{command.GetType().Name.Replace("Command", string.Empty)} {(result.Success ? "ok" : "FAIL")}"
                + $"{(result.Dialogue is not null ? " DLG" : string.Empty)} | {(result.Messages.FirstOrDefault() ?? string.Empty).Replace('\n', ' ')[..Math.Min(64, (result.Messages.FirstOrDefault() ?? string.Empty).Length)]}");

            await WriteStepAsync(writer, episode, seed, step, command, result);
            stepsTaken = step + 1;
            lastWasTravel = command is TravelCommand;

            var signature = (spells, dialogues, npcSouls.Count);
            if (signature != progressSignature)
            {
                progressSignature = signature;
                lastProgressStep = step;
            }
            else if (step - lastProgressStep > 80)
            {
                issues.Add($"no-progress breaker fired at step {step}: spells {spells}, dialogues {dialogues}, npcs {npcSouls.Count}");
                break;
            }
        }

        issues.AddRange(SaveLoadInvariant(session));
        var metFinal = spells >= options.MinSpells && dialogues >= options.MinDialogues && npcSouls.Count >= options.MinNpcs;
        var resultRecord = new PlaytestResult(
            episode, seed, spells, dialogues, npcSouls.Count, metFinal, stepsTaken,
            issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            feel.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray());
        return new EpisodeOutcome(false, resultRecord, null);
    }

    private static CheckpointState? LoadCheckpoint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CheckpointState>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCheckpoint(string? path, CheckpointState state)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static void DeleteCheckpoint(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static StreamWriter? OpenLog(string? path, bool append)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new StreamWriter(path, append);
    }

    private static IReadOnlyList<string> CheckInvariants(int step, ActionResult result, Core.Validation.StateValidationReport validation)
    {
        var issues = new List<string>();
        if (!validation.IsValid)
        {
            issues.AddRange(validation.Issues.Select(issue => $"invalid state after step {step}: {issue.Code}:{issue.EntityId ?? "state"}:{issue.Message}"));
        }

        if (result.ConsumedTurn && result.TurnAfter <= result.TurnBefore)
        {
            issues.Add($"step {step} consumed {result.Action} did not advance the turn");
        }

        if (!result.ConsumedTurn && result.TurnAfter != result.TurnBefore)
        {
            issues.Add($"step {step} free {result.Action} changed the turn");
        }

        if (result.Success && result.Messages.Count == 0)
        {
            issues.Add($"step {step} successful {result.Action} returned no messages");
        }

        return issues;
    }

    private static IReadOnlyList<string> SaveLoadInvariant(GameSession session)
    {
        var issues = new List<string>();
        try
        {
            var savedAt = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
            var before = Sorcerer.Core.Persistence.GameSaveService.Serialize(session.Engine.State, savedAt: savedAt);
            var loaded = Sorcerer.Core.Persistence.GameSaveService.Deserialize(before);
            var validation = Core.Validation.StateValidator.Validate(loaded.State);
            if (!validation.IsValid)
            {
                issues.AddRange(validation.Issues.Select(issue => $"loaded invalid: {issue.Code}:{issue.Message}"));
            }

            var after = Sorcerer.Core.Persistence.GameSaveService.Serialize(loaded.State, loaded.PendingCast, loaded.PendingCastSerial, savedAt);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                issues.Add("save/load/save changed serialized state");
            }
        }
        catch (Exception ex)
        {
            issues.Add($"save/load threw {ex.GetType().Name}: {ex.Message}");
        }

        return issues;
    }

    private static async Task WriteStepAsync(StreamWriter? writer, int episode, int seed, int step, GameCommand command, ActionResult result)
    {
        if (writer is null)
        {
            return;
        }

        var record = new
        {
            episode,
            seed,
            step,
            command = command.GetType().Name,
            action = result.Action,
            success = result.Success,
            consumedTurn = result.ConsumedTurn,
            technicalFailure = result.TechnicalFailure,
            magic = result.Magic?.EffectTypes ?? Array.Empty<string>(),
            dialogue = result.Dialogue is not null,
            messages = result.Messages.Take(4).ToArray(),
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions));
        await writer.FlushAsync();
    }

    private static EntityCard? AdjacentTalker(GameView view, EntityCard player) =>
        FriendlyTalkers(view, player)
            .Where(entity => Distance(player, entity) <= 1)
            .OrderBy(entity => Distance(player, entity))
            .ThenBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static EntityCard? VisibleTalker(GameView view, EntityCard player, HashSet<string>? excludeSouls, GameSession session) =>
        FriendlyTalkers(view, player)
            .Where(entity => excludeSouls is null || !excludeSouls.Contains(SoulOf(session, entity.Id)))
            .OrderBy(entity => Distance(player, entity))
            .ThenBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static IEnumerable<EntityCard> FriendlyTalkers(GameView view, EntityCard player) =>
        view.Entities
            .Where(entity => entity.Id != view.ControlledEntityId)
            .Where(entity => entity.HitPoints is null or > 0)
            .Where(entity => TagsContain(entity, "npc") || TagsContain(entity, "resident")
                || TagsContain(entity, "prisoner") || TagsContain(entity, "merchant") || TagsContain(entity, "guide"))
            .Where(entity => !string.Equals(entity.Faction, "empire", StringComparison.OrdinalIgnoreCase));

    private static string SoulOf(GameSession session, string entityId) =>
        session.Engine.EntityById(entityId) is { } entity && entity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : entityId;

    private static int RoundsWith(IReadOnlyDictionary<string, int> rounds, string soul) =>
        rounds.TryGetValue(soul, out var count) ? count : 0;

    private static EntityCard? PlayerCard(GameView view) =>
        view.Entities.FirstOrDefault(entity => entity.Id == view.ControlledEntityId);

    private static bool TagsContain(EntityCard entity, string tag) =>
        entity.Tags.Any(candidate => candidate.Equals(tag, StringComparison.OrdinalIgnoreCase));

    private static int Distance(EntityCard a, EntityCard b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static Direction DirectionToward(EntityCard from, EntityCard to)
    {
        var dx = Math.Sign(to.X - from.X);
        var dy = Math.Sign(to.Y - from.Y);
        return (dx, dy) switch
        {
            (0, -1) => Direction.North,
            (0, 1) => Direction.South,
            (1, 0) => Direction.East,
            (-1, 0) => Direction.West,
            (1, -1) => Direction.NorthEast,
            (-1, -1) => Direction.NorthWest,
            (1, 1) => Direction.SouthEast,
            (-1, 1) => Direction.SouthWest,
            _ => Direction.East,
        };
    }

    private sealed record EpisodeOutcome(bool Paused, PlaytestResult? Result, CurrentEpisodeState? Pending);

    public sealed record CheckpointState(int NextEpisode, List<PlaytestResult> Completed, CurrentEpisodeState? Current);

    public sealed record CurrentEpisodeState(
        int Episode,
        int Seed,
        int Spells,
        int Dialogues,
        int StepsTaken,
        int SpellIdx,
        int LineIdx,
        int TravelIdx,
        List<string> NpcSouls,
        Dictionary<string, int> Rounds,
        List<string> Issues,
        List<string> Feel,
        string GameSavePath);

    public sealed record PlaytestReport(
        string Provider,
        int Episodes,
        int MetTargets,
        int TotalSpells,
        int TotalDialogues,
        int TotalIssues,
        IReadOnlyList<PlaytestResult> Results);

    public sealed record PlaytestResult(
        int Episode,
        int Seed,
        int Spells,
        int Dialogues,
        int DistinctNpcs,
        bool MetTargets,
        int Steps,
        IReadOnlyList<string> Issues,
        IReadOnlyList<string> FeelNotes);
}
