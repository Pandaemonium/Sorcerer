using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Views;
using Sorcerer.Magic;
using Sorcerer.Magic.Auditing;
using Sorcerer.Magic.Replay;

namespace Sorcerer.Cli;

public static class TranscriptReplayRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string path,
        bool assertFinal,
        bool json)
    {
        var transcript = Load(path);
        var provider = new ReplaySpellProvider(transcript.ResolvedMagicJson);
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(provider, audit: NullSpellAuditSink.Instance),
            transcript.OriginId,
            transcript.Seed);
        session.Engine.State.BackgroundSettings = new BackgroundJobSettings(
            transcript.BackgroundEnabled ?? session.Engine.State.BackgroundSettings.Enabled,
            transcript.MaxBackgroundJobs ?? session.Engine.State.BackgroundSettings.MaxQueuedJobs,
            transcript.BackgroundJobsPerTurn ?? session.Engine.State.BackgroundSettings.JobsPerTurn);

        var steps = new List<ReplayStepSummary>();
        var failed = false;
        foreach (var step in transcript.Steps)
        {
            var result = await session.ExecuteAsync(Program.ParseCommand(step.Command));
            steps.Add(new ReplayStepSummary(
                step.Step,
                step.Command,
                result.Action,
                result.Success,
                result.TechnicalFailure,
                result.TurnAfter));
            failed = failed || result.TechnicalFailure;
        }

        var actualFinal = ReplayObservationSummary.FromObservation(session.Observation(debug: true));
        string? assertionError = null;
        if (assertFinal && transcript.FinalSummary is not null && transcript.FinalSummary != actualFinal)
        {
            assertionError = $"Final replay summary differed. Expected {transcript.FinalSummary}; got {actualFinal}.";
            failed = true;
        }

        var summary = new ReplayRunSummary(
            path,
            transcript.Seed,
            transcript.OriginId,
            transcript.Steps.Count,
            transcript.ResolvedMagicJson.Count,
            actualFinal,
            assertFinal,
            assertionError,
            steps);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
        }
        else
        {
            Console.WriteLine($"Replayed {summary.StepCount} commands from {path}.");
            Console.WriteLine($"Materialized spell resolutions: {summary.MaterializedSpellCount}.");
            Console.WriteLine($"Final: turn {actualFinal.Turn}, zone {actualFinal.ZoneId}, status {actualFinal.RunStatus}, entities {actualFinal.EntityCount}, validation issues {actualFinal.ValidationIssues}.");
            if (assertionError is not null)
            {
                Console.WriteLine(assertionError);
            }
        }

        return failed ? 1 : 0;
    }

    private static ReplayTranscript Load(string path)
    {
        var steps = new List<ReplayCommandStep>();
        var resolvedMagicJson = new List<string>();
        ReplayObservationSummary? final = null;
        string? originId = null;
        var seed = 7;
        bool? backgroundEnabled = null;
        int? maxBackgroundJobs = null;
        int? backgroundJobsPerTurn = null;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var recordType = ReadString(root, "recordType");
            if (recordType.Equals("transcript_start", StringComparison.OrdinalIgnoreCase))
            {
                seed = ReadInt(root, "seed", seed);
                originId = ReadNullableString(root, "originId");
                backgroundEnabled = ReadNullableBool(root, "backgroundEnabled");
                maxBackgroundJobs = ReadNullableInt(root, "maxBackgroundJobs");
                backgroundJobsPerTurn = ReadNullableInt(root, "backgroundJobsPerTurn");
                continue;
            }

            if (recordType.Equals("transcript_step", StringComparison.OrdinalIgnoreCase))
            {
                var command = ReadString(root, "command");
                var step = ReadInt(root, "step", steps.Count);
                steps.Add(new ReplayCommandStep(step, command));
                var magicJson = ReadMagicJson(root);
                if (!string.IsNullOrWhiteSpace(magicJson))
                {
                    resolvedMagicJson.Add(magicJson);
                }

                continue;
            }

            if (recordType.Equals("transcript_final", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("finalObservation", out var finalObservation))
            {
                final = ReplayObservationSummary.FromJson(finalObservation);
            }
        }

        return new ReplayTranscript(
            seed,
            originId,
            backgroundEnabled,
            maxBackgroundJobs,
            backgroundJobsPerTurn,
            steps,
            resolvedMagicJson,
            final);
    }

    private static string? ReadMagicJson(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("magic", out var magic)
            || magic.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return magic.TryGetProperty("resolvedMagicJson", out var resolved)
            && resolved.ValueKind == JsonValueKind.String
                ? resolved.GetString()
                : null;
    }

    private static string ReadString(JsonElement root, string property, string fallback = "") =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string? ReadNullableString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string property, int fallback = 0) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static int? ReadNullableInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool? ReadNullableBool(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private sealed record ReplayTranscript(
        int Seed,
        string? OriginId,
        bool? BackgroundEnabled,
        int? MaxBackgroundJobs,
        int? BackgroundJobsPerTurn,
        IReadOnlyList<ReplayCommandStep> Steps,
        IReadOnlyList<string> ResolvedMagicJson,
        ReplayObservationSummary? FinalSummary);

    private sealed record ReplayCommandStep(int Step, string Command);

    private sealed record ReplayStepSummary(
        int Step,
        string Command,
        string Action,
        bool Success,
        bool TechnicalFailure,
        int TurnAfter);

    private sealed record ReplayRunSummary(
        string TranscriptPath,
        int Seed,
        string? OriginId,
        int StepCount,
        int MaterializedSpellCount,
        ReplayObservationSummary FinalSummary,
        bool AssertFinal,
        string? AssertionError,
        IReadOnlyList<ReplayStepSummary> Steps);

    private sealed record ReplayObservationSummary(
        int Turn,
        string ControlledEntityId,
        string? ZoneId,
        int EntityCount,
        int ValidationIssues,
        int CanonRecords,
        int BackgroundJobs,
        string RunStatus,
        string? RunConclusion)
    {
        public static ReplayObservationSummary FromObservation(AgentObservation observation) =>
            new(
                observation.View.Turn,
                observation.View.ControlledEntityId,
                observation.View.World?.CurrentZoneId,
                observation.Debug?.EntityCount ?? 0,
                observation.Debug?.ValidationIssues?.Count ?? 0,
                observation.Debug?.Ledgers?.CanonRecords ?? 0,
                observation.Debug?.BackgroundJobs?.Count ?? 0,
                observation.Debug?.RunStatus ?? "running",
                observation.Debug?.RunConclusion);

        public static ReplayObservationSummary FromJson(JsonElement observation)
        {
            var view = observation.TryGetProperty("view", out var viewElement) ? viewElement : default;
            var debug = observation.TryGetProperty("debug", out var debugElement) ? debugElement : default;
            var ledgers = debug.ValueKind != JsonValueKind.Undefined && debug.TryGetProperty("ledgers", out var ledgersElement)
                ? ledgersElement
                : default;
            var world = view.ValueKind != JsonValueKind.Undefined && view.TryGetProperty("world", out var worldElement)
                ? worldElement
                : default;
            return new ReplayObservationSummary(
                ReadInt(view, "turn"),
                ReadString(view, "controlledEntityId"),
                ReadNullableString(world, "currentZoneId"),
                ReadInt(debug, "entityCount"),
                debug.ValueKind != JsonValueKind.Undefined && debug.TryGetProperty("validationIssues", out var issues) && issues.ValueKind == JsonValueKind.Array
                    ? issues.GetArrayLength()
                    : 0,
                ReadInt(ledgers, "canonRecords"),
                debug.ValueKind != JsonValueKind.Undefined && debug.TryGetProperty("backgroundJobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array
                    ? jobs.GetArrayLength()
                    : 0,
                ReadString(debug, "runStatus", "running"),
                ReadNullableString(debug, "runConclusion"));
        }
    }
}
