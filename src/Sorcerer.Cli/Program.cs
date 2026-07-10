using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
using Sorcerer.Llm.Auditing;
using Sorcerer.Llm.Configuration;
using Sorcerer.Magic;

namespace Sorcerer.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        options = options with
        {
            Commands = LoadScriptCommands(options.ScriptPath).Concat(options.Commands).ToArray(),
        };
        var configuration = BuildLlmConfiguration(options);
        var provider = SpellProviderFactory.Create(configuration, LlmPurpose.Wild);
        var router = SpellRouterFactory.Create(configuration, LlmPurpose.Router);
        var dialogueProvider = DialogueProviderFactory.Create(configuration, LlmPurpose.Dialogue);
        var dialogueRouter = DialogueRouterFactory.Create(configuration, LlmPurpose.DialogueRouter);
        var dialogueParserRouter = DialogueParserRouterFactory.Create(configuration, LlmPurpose.DialogueParserRouter);
        var dialogueParser = DialogueParserFactory.Create(configuration, LlmPurpose.DialogueParser);
        var backgroundAudit = new JsonlBackgroundTextAuditSink(Path.Combine("logs", "background_audit.jsonl"));
        var backgroundTextGenerator = BackgroundTextGeneratorFactory.Create(
            configuration,
            LlmPurpose.Background,
            backgroundAudit);
        var audit = new JsonlSpellAuditSink(Path.Combine("logs", "wild_magic_audit.jsonl"));
        var dialogueAudit = new JsonlDialogueAuditSink(Path.Combine("logs", "dialogue_audit.jsonl"));
        if (options.Eval)
        {
            return await SpellEvalHarness.RunAsync(provider, audit, options.Json);
        }

        if (options.LivePlaytest)
        {
            return await LivePlaytestHarness.RunAsync(
                provider,
                dialogueProvider,
                dialogueRouter,
                dialogueParserRouter,
                dialogueParser,
                dialogueAudit,
                audit,
                backgroundTextGenerator,
                new LivePlaytestOptions(
                    options.Episodes,
                    options.Seed,
                    MinSpells: 20,
                    MinDialogues: 12,
                    MinNpcs: 5,
                    MaxSteps: 800,
                    options.BudgetSeconds,
                    options.CheckpointPath,
                    options.EpisodeLogPath),
                options.Json);
        }

        if (options.Episode)
        {
            return await EpisodeRunner.RunAsync(
                provider,
                dialogueProvider,
                dialogueRouter,
                dialogueParserRouter,
                dialogueParser,
                dialogueAudit,
                audit,
                backgroundTextGenerator,
                new EpisodeRunnerOptions(
                    options.Episodes,
                    options.MaxTurns,
                    options.Seed,
                    options.EpisodeLogPath,
                    options.QuickstartScene),
                options.Json);
        }

        if (!string.IsNullOrWhiteSpace(options.ReplayPath))
        {
            return await TranscriptReplayRunner.RunAsync(
                options.ReplayPath,
                options.ReplayAssertFinal,
                options.Json);
        }

        if (!string.IsNullOrWhiteSpace(options.ReparseAuditPath))
        {
            return await AuditReparseHarness.RunAsync(options.ReparseAuditPath, options.Json);
        }

        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(provider, audit: audit, router: router),
            options.OriginId,
            options.Seed,
            CrossRunMemorialStore.LoadDefault(),
            dialogueProvider: dialogueProvider,
            dialogueRouter: dialogueRouter,
            dialogueAudit: dialogueAudit,
            backgroundTextGenerator: backgroundTextGenerator,
            dialogueParser: dialogueParser,
            dialogueParserRouter: dialogueParserRouter);
        ApplyBackgroundOptions(session, options);
        if (options.Echoes)
        {
            session.Engine.State.WorldFlags[GameSession.EchoesEnabledFlag] = true;
        }

        ApplyQuickstart(session, options.QuickstartScene);
        await using var transcript = TranscriptWriter.Open(options.TranscriptPath);
        if (transcript is not null)
        {
            await transcript.WriteStartAsync(options, session.Observation(debug: true));
        }

        if (options.Commands.Count > 0)
        {
            foreach (var commandText in options.Commands)
            {
                var shouldQuit = await ExecuteAndWriteAsync(session, commandText, options, transcript);
                if (shouldQuit)
                {
                    break;
                }
            }

            if (transcript is not null)
            {
                await transcript.WriteFinalAsync(session.Observation(debug: true));
            }

            return 0;
        }

        if (options.Json)
        {
            await WriteEnvelopeAsync(
                new CommandEnvelope(
                    null,
                    session.Observation(options.DebugState)),
                options);
        }
        else
        {
            Console.WriteLine("Sorcerer CLI. Type JSON commands or text commands. Type quit to exit.");
        }

        while (Console.ReadLine() is { } line)
        {
            var shouldQuit = await ExecuteAndWriteAsync(session, line, options, transcript);
            if (shouldQuit)
            {
                break;
            }
        }

        if (transcript is not null)
        {
            await transcript.WriteFinalAsync(session.Observation(debug: true));
        }

        return 0;
    }

    private static LlmConfiguration BuildLlmConfiguration(CliOptions options)
    {
        var configuration = LlmConfiguration.FromEnvironment()
            .WithPurposeOverride(
                LlmPurpose.Wild,
                options.Provider,
                options.Host,
                options.Model)
            .WithPurposeOverride(
                LlmPurpose.Dialogue,
                options.Provider,
                options.Host,
                options.Model)
            .WithPurposeOverride(
                LlmPurpose.DialogueRouter,
                PurposeEnvironmentOverride("DIALOGUE_ROUTER", "PROVIDER") ? null : options.Provider,
                PurposeEnvironmentOverride("DIALOGUE_ROUTER", "HOST") ? null : options.Host,
                PurposeEnvironmentOverride("DIALOGUE_ROUTER", "MODEL") ? null : options.Model)
            .WithPurposeOverride(
                LlmPurpose.DialogueParser,
                ParserEnvironmentOverride("PROVIDER") ? null : options.Provider,
                ParserEnvironmentOverride("HOST") ? null : options.Host,
                ParserEnvironmentOverride("MODEL") ? null : options.Model);
        configuration = configuration.WithPurposeOverride(
            LlmPurpose.DialogueParserRouter,
            ParserRouterEnvironmentOverride("PROVIDER") ? null : options.Provider,
            ParserRouterEnvironmentOverride("HOST") ? null : options.Host,
            ParserRouterEnvironmentOverride("MODEL") ? null : options.Model);
        if (options.BackgroundProvider is not null
            || options.BackgroundHost is not null
            || options.BackgroundModel is not null
            || options.BackgroundEnabled is not null)
        {
            var providerSpecified = options.BackgroundProvider is not null
                || options.BackgroundHost is not null
                || options.BackgroundModel is not null;
            configuration = configuration.WithPurposeOverride(
                LlmPurpose.Background,
                options.BackgroundProvider,
                options.BackgroundHost,
                options.BackgroundModel,
                maxConcurrentCalls: options.BackgroundConcurrency,
                enabled: options.BackgroundEnabled ?? providerSpecified);
        }

        return configuration;
    }

    private static bool ParserEnvironmentOverride(string suffix) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"SORCERER_DIALOGUE_PARSER_{suffix}"));

    private static bool ParserRouterEnvironmentOverride(string suffix) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"SORCERER_DIALOGUE_PARSER_ROUTER_{suffix}"));

    private static bool PurposeEnvironmentOverride(string purpose, string suffix) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"SORCERER_{purpose}_{suffix}"));

    private static void ApplyBackgroundOptions(GameSession session, CliOptions options)
    {
        var current = session.Engine.State.BackgroundSettings;
        session.Engine.State.BackgroundSettings = new BackgroundJobSettings(
            options.BackgroundEnabled ?? current.Enabled,
            options.MaxBackgroundJobs,
            options.BackgroundJobsPerTurn);
    }

    private static async Task<bool> ExecuteAndWriteAsync(
        GameSession session,
        string commandText,
        CliOptions options,
        TranscriptWriter? transcript = null)
    {
        var command = ParseCommand(commandText);
        var result = await session.ExecuteAsync(command);
        var observation = session.Observation(options.DebugState);
        await WriteEnvelopeAsync(new CommandEnvelope(result, observation), options);
        if (transcript is not null)
        {
            await transcript.WriteStepAsync(commandText, result, session.Observation(debug: true));
        }

        if (result.Deltas.Any(delta => delta.Operation.Equals("runComplete", StringComparison.OrdinalIgnoreCase)))
        {
            CrossRunMemorialStore.AppendLatestChronicle(session.Engine.State);
        }

        return result.ShouldQuit;
    }

    internal static void ApplyQuickstart(GameSession session, string? quickstart)
    {
        if (!string.Equals(quickstart, "social", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var state = session.Engine.State;
        session.Engine.ApplyConsequence(WorldConsequence.OpenOrUnlock(
            "cli_quickstart",
            "cell_door_1",
            unlock: true,
            open: true,
            operation: "quickstartOpenDoor"));
        session.Engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "cli_quickstart",
            state.ControlledEntityId.Value,
            13,
            5,
            operation: "quickstartMove",
            emitMessage: false));

        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            if (session.Engine.EntityById(id) is { } soldier)
            {
                session.Engine.ApplyConsequence(WorldConsequence.UpdateControl(
                    "cli_quickstart",
                    soldier.Id.Value,
                    "none",
                    removeAi: true,
                    operation: "quickstartDisableAi"));
            }
        }

        session.Engine.ApplyConsequence(WorldConsequence.Message(
            "cli_quickstart",
            "Social quickstart: the cell is open enough for conversation.",
            targetEntityId: state.ControlledEntityId.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: state.ControlledEntityId.Value,
            operation: "quickstartMessage",
            details: new Dictionary<string, object?>
            {
                ["quickstart"] = "social",
            }));
    }

    private static IReadOnlyList<string> LoadScriptCommands(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return Array.Empty<string>();
        }

        return File.ReadLines(scriptPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    private static Task WriteEnvelopeAsync(CommandEnvelope envelope, CliOptions options)
    {
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
            return Task.CompletedTask;
        }

        if (envelope.Result is null)
        {
            Console.WriteLine($"Turn {envelope.Observation.View.Turn}.");
            return Task.CompletedTask;
        }

        foreach (var message in envelope.Result.Messages)
        {
            Console.WriteLine(message);
        }

        Console.WriteLine($"Turn {envelope.Observation.View.Turn}.");
        return Task.CompletedTask;
    }

    internal static GameCommand ParseCommand(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{'))
        {
            return ParseJsonCommand(trimmed);
        }

        return TextCommandParser.Parse(trimmed);
    }

    private static GameCommand ParseJsonCommand(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new UnknownCommand(json);
        }

        using (document)
        {
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? ""
                : "";

            return type.Trim().ToLowerInvariant() switch
            {
                "n" or "north" => new MoveCommand(Direction.North),
                "s" or "south" => new MoveCommand(Direction.South),
                "e" or "east" => new MoveCommand(Direction.East),
                "w" or "west" => new MoveCommand(Direction.West),
                "ne" or "northeast" => new MoveCommand(Direction.NorthEast),
                "nw" or "northwest" => new MoveCommand(Direction.NorthWest),
                "se" or "southeast" => new MoveCommand(Direction.SouthEast),
                "sw" or "southwest" => new MoveCommand(Direction.SouthWest),
                "move" => new MoveCommand(ParseDirection(ReadString(root, "direction", "east"))),
                "wait" => new WaitCommand(),
                "inspect" or "look" => new InspectCommand(),
                "map" => new MapCommand(ReadInt(root, "radius", 8)),
                "cast" => ReadBool(root, "await", true)
                    ? new CastCommand(ReadString(root, "text", ""), CastPerformance.Neutral)
                    : new BeginCastCommand(ReadString(root, "text", ""), CastPerformance.Neutral),
                "begin_cast" or "submit_cast" or "start_cast" => new BeginCastCommand(ReadString(root, "text", ""), CastPerformance.Neutral),
                "await_cast" or "resolve_cast" => new AwaitCastCommand(ReadPerformance(root)),
                "cancel_cast" => new CancelCastCommand(),
                "target" => new TargetCommand(new GridPoint(ReadInt(root, "x", 0), ReadInt(root, "y", 0))),
                "untarget" or "cleartarget" => new ClearTargetCommand(),
                "travel" or "go" => new TravelCommand(ParseDirection(ReadString(root, "direction", "east"))),
                "atlas" or "world" => new AtlasCommand(),
                "pickup" or "get" => new PickupCommand(ReadNullableString(root, "target")),
                "drop" => new DropCommand(ReadString(root, "item", "")),
                "use" => new UseItemCommand(ReadString(root, "item", "")),
                "equip" => new EquipCommand(ReadString(root, "item", "")),
                "unequip" => new UnequipCommand(ReadString(root, "item", "")),
                "focus" => new FocusCommand(ReadString(root, "item", "")),
                "unfocus" => new UnfocusCommand(ReadNullableString(root, "item")),
                "protect" => new ProtectItemCommand(ReadString(root, "item", "")),
                "unprotect" => new UnprotectItemCommand(ReadString(root, "item", "")),
                "reagents" => new ReagentsCommand(),
                "wares" or "browse" => new WaresCommand(ReadNullableString(root, "target")),
                "buy" => new BuyCommand(ReadString(root, "item", ""), ReadNullableString(root, "target")),
                "sell" => new SellCommand(ReadString(root, "item", ""), ReadNullableString(root, "target")),
                "services" => new ServicesCommand(ReadNullableString(root, "target")),
                "request" or "service" => new RequestServiceCommand(
                    ReadString(root, "service", ReadString(root, "name", "")),
                    ReadNullableString(root, "target")),
                "journal" or "promises" => new JournalCommand(),
                "rumors" or "gossip" => new RumorsCommand(),
                "character" or "sheet" or "profile" => new CharacterCommand(),
                "talk" or "say" or "speak" => new TalkCommand(ReadString(root, "text", "")),
                "give" or "gift" => new GiveCommand(ReadString(root, "item", ""), ReadNullableString(root, "target")),
                "recruit" => new RecruitCommand(ReadNullableString(root, "target")),
                "bonds" or "bond" => new BondsCommand(ReadNullableString(root, "target")),
                "read" => new ReadCommand(ReadNullableString(root, "target")),
                "examine" or "study" => new ExamineCommand(ReadNullableString(root, "target")),
                "open" => new OpenCommand(ReadNullableString(root, "target")),
                "enter" => new EnterCommand(ReadNullableString(root, "target")),
                "leave" or "exit_interior" => new LeaveCommand(),
                "possess" => new PossessCommand(ReadNullableString(root, "target")),
                "standing" => new StandingCommand(),
                "followers" => new FollowersCommand(),
                "jobs" => new JobsCommand(),
                "save" => new SaveCommand(DefaultSavePath(ReadString(root, "path", ""))),
                "load" => new LoadCommand(DefaultSavePath(ReadString(root, "path", ""))),
                "help" => new HelpCommand(),
                "quit" => new QuitCommand(),
                _ => new UnknownCommand(json),
            };
        }
    }

    private static Direction ParseDirection(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "north" or "n" => Direction.North,
            "south" or "s" => Direction.South,
            "east" or "e" => Direction.East,
            "west" or "w" => Direction.West,
            "northeast" or "ne" => Direction.NorthEast,
            "northwest" or "nw" => Direction.NorthWest,
            "southeast" or "se" => Direction.SouthEast,
            "southwest" or "sw" => Direction.SouthWest,
            _ => Direction.East,
        };

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string? ReadNullableString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(JsonElement root, string property, bool fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    // Debug/agent injection of a fixed casting performance:
    // { "type": "await_cast", "performance": { "power": 1.2, "control": 0.8, "wildness": 1.3 } }.
    // Absent or malformed performance keeps whatever the pending cast carries.
    private static CastPerformance? ReadPerformance(JsonElement root)
    {
        if (!root.TryGetProperty("performance", out var performance)
            || performance.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CastPerformance(
            Played: true,
            PowerModifier: ReadFloat(performance, "power", 1f),
            ControlModifier: ReadFloat(performance, "control", 1f),
            WildnessModifier: ReadFloat(performance, "wildness", 1f),
            Source: ReadString(performance, "source", "debug_fixed"));
    }

    private static float ReadFloat(JsonElement root, string property, float fallback) =>
        root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
            ? (float)parsed
            : fallback;

    private static string DefaultSavePath(string value) =>
        string.IsNullOrWhiteSpace(value) ? Path.Combine("runs", "quicksave.json") : value.Trim();
}

public sealed record CommandEnvelope(
    ActionResult? Result,
    AgentObservation Observation);

public sealed record CliOptions(
    string Provider,
    string? Host,
    string? Model,
    bool Json,
    bool DebugState,
    bool Eval,
    bool Episode,
    bool LivePlaytest,
    int Episodes,
    int MaxTurns,
    int Seed,
    string? EpisodeLogPath,
    string? ScriptPath,
    string? TranscriptPath,
    string? ReplayPath,
    bool ReplayAssertFinal,
    string? ReparseAuditPath,
    string? OriginId,
    string? BackgroundProvider,
    string? BackgroundHost,
    string? BackgroundModel,
    bool? BackgroundEnabled,
    int MaxBackgroundJobs,
    int BackgroundJobsPerTurn,
    int BackgroundConcurrency,
    bool Quickstart,
    string? QuickstartScene,
    int BudgetSeconds,
    string? CheckpointPath,
    IReadOnlyList<string> Commands,
    bool Echoes = false)
{
    public static CliOptions Parse(string[] args)
    {
        var provider = "mock";
        string? host = null;
        string? model = null;
        var json = false;
        var debugState = false;
        var eval = false;
        var episode = false;
        var livePlaytest = false;
        var echoes = false;
        var episodes = 1;
        var maxTurns = 40;
        var seed = 7;
        string? episodeLogPath = null;
        string? scriptPath = null;
        string? transcriptPath = null;
        string? replayPath = null;
        var replayAssertFinal = false;
        string? reparseAuditPath = null;
        string? originId = null;
        string? backgroundProvider = null;
        string? backgroundHost = null;
        string? backgroundModel = null;
        bool? backgroundEnabled = null;
        var maxBackgroundJobs = 12;
        var backgroundJobsPerTurn = 1;
        var backgroundConcurrency = 1;
        var quickstart = true;
        string? quickstartScene = null;
        var budgetSeconds = 500;
        string? checkpointPath = null;
        var commands = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--provider" when index + 1 < args.Length:
                    provider = args[++index];
                    break;
                case "--host" when index + 1 < args.Length:
                    host = args[++index];
                    break;
                case "--model" when index + 1 < args.Length:
                    model = args[++index];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--debug-state":
                    debugState = true;
                    break;
                case "--eval":
                case "--eval-spells":
                    eval = true;
                    break;
                case "--episode":
                case "--playtest":
                    episode = true;
                    break;
                case "--live-playtest":
                    livePlaytest = true;
                    break;
                case "--episodes" when index + 1 < args.Length:
                    episodes = Math.Max(1, ReadPositiveInt(args[++index], episodes));
                    break;
                case "--max-turns" when index + 1 < args.Length:
                    maxTurns = Math.Max(1, ReadPositiveInt(args[++index], maxTurns));
                    break;
                case "--seed" when index + 1 < args.Length:
                    seed = Math.Max(1, ReadPositiveInt(args[++index], seed));
                    break;
                case "--episode-log" when index + 1 < args.Length:
                case "--record" when index + 1 < args.Length:
                    episodeLogPath = args[++index];
                    break;
                case "--script" when index + 1 < args.Length:
                    scriptPath = args[++index];
                    break;
                case "--transcript" when index + 1 < args.Length:
                case "--command-log" when index + 1 < args.Length:
                    transcriptPath = args[++index];
                    break;
                case "--replay" when index + 1 < args.Length:
                    replayPath = args[++index];
                    break;
                case "--replay-assert-final":
                    replayAssertFinal = true;
                    break;
                case "--reparse-audit" when index + 1 < args.Length:
                    reparseAuditPath = args[++index];
                    break;
                case "--origin" when index + 1 < args.Length:
                    originId = args[++index];
                    break;
                case "--background-provider" when index + 1 < args.Length:
                    backgroundProvider = args[++index];
                    break;
                case "--background-host" when index + 1 < args.Length:
                    backgroundHost = args[++index];
                    break;
                case "--background-model" when index + 1 < args.Length:
                    backgroundModel = args[++index];
                    break;
                case "--echoes":
                    echoes = true;
                    break;
                case "--enable-background":
                    backgroundEnabled = true;
                    break;
                case "--disable-background":
                case "--no-background":
                    backgroundEnabled = false;
                    break;
                case "--max-background-jobs" when index + 1 < args.Length:
                    maxBackgroundJobs = Math.Max(0, ReadNonNegativeInt(args[++index], maxBackgroundJobs));
                    break;
                case "--background-jobs-per-turn" when index + 1 < args.Length:
                    backgroundJobsPerTurn = Math.Max(0, ReadNonNegativeInt(args[++index], backgroundJobsPerTurn));
                    break;
                case "--background-concurrency" when index + 1 < args.Length:
                    backgroundConcurrency = Math.Max(1, ReadPositiveInt(args[++index], backgroundConcurrency));
                    break;
                case "--quickstart":
                    quickstart = true;
                    if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        quickstartScene = args[++index];
                    }
                    break;
                case "--command" when index + 1 < args.Length:
                    commands.Add(args[++index]);
                    break;
                case "--budget-seconds" when index + 1 < args.Length:
                    budgetSeconds = Math.Max(30, ReadPositiveInt(args[++index], budgetSeconds));
                    break;
                case "--checkpoint" when index + 1 < args.Length:
                    checkpointPath = args[++index];
                    break;
            }
        }

        return new CliOptions(
            provider,
            host,
            model,
            json,
            debugState,
            eval,
            episode,
            livePlaytest,
            episodes,
            maxTurns,
            seed,
            episodeLogPath,
            scriptPath,
            transcriptPath,
            replayPath,
            replayAssertFinal,
            reparseAuditPath,
            originId,
            backgroundProvider,
            backgroundHost,
            backgroundModel,
            backgroundEnabled,
            maxBackgroundJobs,
            backgroundJobsPerTurn,
            backgroundConcurrency,
            quickstart,
            quickstartScene,
            budgetSeconds,
            checkpointPath,
            commands,
            echoes);
    }

    private static int ReadPositiveInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int ReadNonNegativeInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
}

public sealed class TranscriptWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly StreamWriter _writer;
    private int _step;

    private TranscriptWriter(StreamWriter writer)
    {
        _writer = writer;
    }

    public static TranscriptWriter? Open(string? path)
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

        return new TranscriptWriter(new StreamWriter(path, append: false));
    }

    public Task WriteStartAsync(CliOptions options, AgentObservation observation) =>
        WriteAsync(new TranscriptStartRecord(
            "transcript_start",
            DateTimeOffset.UtcNow,
            options.Provider,
            options.Model,
            options.Seed,
            options.ScriptPath,
            options.OriginId,
            options.BackgroundEnabled,
            options.MaxBackgroundJobs,
            options.BackgroundJobsPerTurn,
            options.Quickstart,
            options.QuickstartScene,
            options.Commands,
            observation));

    public Task WriteStepAsync(string commandText, ActionResult result, AgentObservation observation) =>
        WriteAsync(new TranscriptStepRecord(
            "transcript_step",
            DateTimeOffset.UtcNow,
            _step++,
            commandText,
            result,
            observation));

    public Task WriteFinalAsync(AgentObservation observation) =>
        WriteAsync(new TranscriptFinalRecord(
            "transcript_final",
            DateTimeOffset.UtcNow,
            _step,
            observation));

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
    }

    private async Task WriteAsync(object record)
    {
        await _writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions));
        await _writer.FlushAsync();
    }

    private sealed record TranscriptStartRecord(
        string RecordType,
        DateTimeOffset Timestamp,
        string Provider,
        string? Model,
        int Seed,
        string? ScriptPath,
        string? OriginId,
        bool? BackgroundEnabled,
        int MaxBackgroundJobs,
        int BackgroundJobsPerTurn,
        bool Quickstart,
        string? QuickstartScene,
        IReadOnlyList<string> Commands,
        AgentObservation InitialObservation);

    private sealed record TranscriptStepRecord(
        string RecordType,
        DateTimeOffset Timestamp,
        int Step,
        string Command,
        ActionResult Result,
        AgentObservation Observation);

    private sealed record TranscriptFinalRecord(
        string RecordType,
        DateTimeOffset Timestamp,
        int Steps,
        AgentObservation FinalObservation);
}
