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
        var provider = SpellProviderFactory.Create(options.Provider, options.Host, options.Model);
        var audit = new JsonlSpellAuditSink(Path.Combine("logs", "wild_magic_audit.jsonl"));
        if (options.Eval)
        {
            return await SpellEvalHarness.RunAsync(provider, audit, options.Json);
        }

        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider, audit: audit));

        if (options.Commands.Count > 0)
        {
            foreach (var commandText in options.Commands)
            {
                var shouldQuit = await ExecuteAndWriteAsync(session, commandText, options);
                if (shouldQuit)
                {
                    break;
                }
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
            var shouldQuit = await ExecuteAndWriteAsync(session, line, options);
            if (shouldQuit)
            {
                break;
            }
        }

        return 0;
    }

    private static async Task<bool> ExecuteAndWriteAsync(
        GameSession session,
        string commandText,
        CliOptions options)
    {
        var command = ParseCommand(commandText);
        var result = await session.ExecuteAsync(command);
        var observation = session.Observation(options.DebugState);
        await WriteEnvelopeAsync(new CommandEnvelope(result, observation), options);
        return result.ShouldQuit;
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
                case "--command" when index + 1 < args.Length:
                    commands.Add(args[++index]);
                    break;
            }
        }

        return new CliOptions(provider, host, model, json, debugState, eval, commands);
    }
}
