using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
using Sorcerer.Llm.Auditing;
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
        var provider = SpellProviderFactory.Create(options.Provider, options.Host, options.Model);
        var audit = new JsonlSpellAuditSink(Path.Combine("logs", "wild_magic_audit.jsonl"));
        if (options.Eval)
        {
            return await SpellEvalHarness.RunAsync(provider, audit, options.Json);
        }

        if (options.Episode)
        {
            return await EpisodeRunner.RunAsync(
                provider,
                audit,
                new EpisodeRunnerOptions(
                    options.Episodes,
                    options.MaxTurns,
                    options.Seed,
                    options.EpisodeLogPath),
                options.Json);
        }

        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider, audit: audit));
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

        return result.ShouldQuit;
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

    private static GameCommand ParseCommand(string text)
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
                "move" => new MoveCommand(ParseDirection(ReadString(root, "direction", "east"))),
                "wait" => new WaitCommand(),
                "inspect" => new InspectCommand(),
                "map" => new MapCommand(ReadInt(root, "radius", 8)),
                "cast" => ReadBool(root, "await", true)
                    ? new CastCommand(ReadString(root, "text", ""), CastPerformance.Neutral)
                    : new BeginCastCommand(ReadString(root, "text", ""), CastPerformance.Neutral),
                "begin_cast" or "submit_cast" or "start_cast" => new BeginCastCommand(ReadString(root, "text", ""), CastPerformance.Neutral),
                "await_cast" or "resolve_cast" => new AwaitCastCommand(),
                "cancel_cast" => new CancelCastCommand(),
                "target" => new TargetCommand(new GridPoint(ReadInt(root, "x", 0), ReadInt(root, "y", 0))),
                "untarget" => new ClearTargetCommand(),
                "pickup" => new PickupCommand(ReadNullableString(root, "target")),
                "drop" => new DropCommand(ReadString(root, "item", "")),
                "use" => new UseItemCommand(ReadString(root, "item", "")),
                "equip" => new EquipCommand(ReadString(root, "item", "")),
                "unequip" => new UnequipCommand(ReadString(root, "item", "")),
                "focus" => new FocusCommand(ReadString(root, "item", "")),
                "unfocus" => new UnfocusCommand(ReadNullableString(root, "item")),
                "protect" => new ProtectItemCommand(ReadString(root, "item", "")),
                "unprotect" => new UnprotectItemCommand(ReadString(root, "item", "")),
                "reagents" => new ReagentsCommand(),
                "journal" => new JournalCommand(),
                "talk" => new TalkCommand(ReadString(root, "text", "")),
                "read" => new ReadCommand(ReadNullableString(root, "target")),
                "examine" => new ExamineCommand(ReadNullableString(root, "target")),
                "open" => new OpenCommand(ReadNullableString(root, "target")),
                "possess" => new PossessCommand(ReadNullableString(root, "target")),
                "standing" => new StandingCommand(),
                "followers" => new FollowersCommand(),
                "jobs" => new JobsCommand(),
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
    int Episodes,
    int MaxTurns,
    int Seed,
    string? EpisodeLogPath,
    string? ScriptPath,
    string? TranscriptPath,
    IReadOnlyList<string> Commands)
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
        var episodes = 1;
        var maxTurns = 40;
        var seed = 7;
        string? episodeLogPath = null;
        string? scriptPath = null;
        string? transcriptPath = null;
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
                case "--command" when index + 1 < args.Length:
                    commands.Add(args[++index]);
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
            episodes,
            maxTurns,
            seed,
            episodeLogPath,
            scriptPath,
            transcriptPath,
            commands);
    }

    private static int ReadPositiveInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
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
            options.ScriptPath,
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
        string? ScriptPath,
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
