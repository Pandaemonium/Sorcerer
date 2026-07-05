using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Results;
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
        // A log can hold multiple episodes (--episode --episodes N writes them to one file).
        // Each gets its own fresh session; folding them into one session would run episode 2's
        // commands and materialized resolutions against episode 1's world under one seed.
        var transcripts = LoadAll(path);
        var summaries = new List<ReplayRunSummary>();
        foreach (var transcript in transcripts)
        {
            summaries.Add(await ReplayOne(path, transcript, assertFinal));
        }

        var failed = summaries.Any(summary => summary.Failed);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                summaries.Count == 1 ? (object)summaries[0] : summaries,
                JsonOptions));
        }
        else if (summaries.Count == 1)
        {
            PrintSummary(path, summaries[0]);
        }
        else
        {
            for (var i = 0; i < summaries.Count; i++)
            {
                Console.WriteLine($"--- Episode {i + 1}/{summaries.Count} ---");
                PrintSummary(path, summaries[i]);
            }

            Console.WriteLine(
                $"Replayed {summaries.Count} episodes from {path}: "
                + $"{summaries.Count(summary => !summary.Failed)} passed, "
                + $"{summaries.Count(summary => summary.Failed)} failed.");
        }

        return failed ? 1 : 0;
    }

    private static async Task<ReplayRunSummary> ReplayOne(
        string path,
        ReplayTranscript transcript,
        bool assertFinal)
    {
        var provider = new ReplaySpellProvider(transcript.ResolvedMagicJson);
        var dialogueRouter = transcript.DialogueRouteRecords.Count == 0
            ? null
            : new ReplayDialogueRouter(transcript.DialogueRouteRecords);
        var dialogueProvider = transcript.DialogueRecords.Count == 0
            ? null
            : new ReplayDialogueProvider(transcript.DialogueRecords);
        var dialogueParser = transcript.DialogueParseRecords.Count == 0
            ? null
            : new ReplayDialogueParser(transcript.DialogueParseRecords);
        var dialogueParserRouter = transcript.DialogueParserRouteRecords.Count == 0
            ? null
            : new ReplayDialogueParserRouter(transcript.DialogueParserRouteRecords);
        var claimExtractor = dialogueParser is not null || transcript.ClaimExtractionRecords.Count == 0
            ? null
            : new ReplayDialogueClaimExtractor(transcript.ClaimExtractionRecords);
        var backgroundTextGenerator = transcript.BackgroundTextsByJobId.Count == 0
            ? null
            : new ReplayBackgroundTextGenerator(transcript.BackgroundTextsByJobId);
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(provider, audit: NullSpellAuditSink.Instance),
            transcript.OriginId,
            transcript.Seed,
            claimExtractor: claimExtractor,
            dialogueRouter: dialogueRouter,
            dialogueProvider: dialogueProvider,
            dialogueAudit: NullDialogueAuditSink.Instance,
            backgroundTextGenerator: backgroundTextGenerator,
            dialogueParser: dialogueParser,
            dialogueParserRouter: dialogueParserRouter);
        session.Engine.State.BackgroundSettings = new BackgroundJobSettings(
            transcript.BackgroundEnabled ?? session.Engine.State.BackgroundSettings.Enabled,
            transcript.MaxBackgroundJobs ?? session.Engine.State.BackgroundSettings.MaxQueuedJobs,
            transcript.BackgroundJobsPerTurn ?? session.Engine.State.BackgroundSettings.JobsPerTurn);
        if (transcript.Quickstart)
        {
            Program.ApplyQuickstart(session, transcript.QuickstartScene);
        }

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

        return new ReplayRunSummary(
            path,
            transcript.Seed,
            transcript.OriginId,
            transcript.Steps.Count,
            transcript.ResolvedMagicJson.Count,
            transcript.DialogueRouteRecords.Count,
            transcript.DialogueRecords.Count,
            transcript.ClaimExtractionRecords.Count,
            transcript.DialogueParseRecords.Count,
            transcript.BackgroundTextsByJobId.Count,
            actualFinal,
            assertFinal,
            assertionError,
            steps,
            failed);
    }

    private static void PrintSummary(string path, ReplayRunSummary summary)
    {
        Console.WriteLine($"Replayed {summary.StepCount} commands from {path}.");
        Console.WriteLine($"Materialized spell resolutions: {summary.MaterializedSpellCount}.");
        Console.WriteLine($"Materialized dialogue routes: {summary.MaterializedDialogueRouteCount}.");
        Console.WriteLine($"Materialized dialogue responses: {summary.MaterializedDialogueCount}.");
        Console.WriteLine($"Materialized claim extractions: {summary.MaterializedClaimExtractionCount}.");
        Console.WriteLine($"Materialized dialogue parses: {summary.MaterializedDialogueParseCount}.");
        Console.WriteLine($"Materialized background texts: {summary.MaterializedBackgroundTextCount}.");
        var actualFinal = summary.FinalSummary;
        Console.WriteLine($"Final: turn {actualFinal.Turn}, zone {actualFinal.ZoneId}, status {actualFinal.RunStatus}, entities {actualFinal.EntityCount}, promises {actualFinal.Promises}, claims {actualFinal.Claims}, rumors {actualFinal.Rumors}, validation issues {actualFinal.ValidationIssues}.");
        if (summary.AssertionError is not null)
        {
            Console.WriteLine(summary.AssertionError);
        }
    }

    /// <summary>
    /// Splits the log into one <see cref="ReplayTranscript"/> per start record. A single-episode
    /// transcript/script log is the common case and produces a one-element list.
    /// </summary>
    private static IReadOnlyList<ReplayTranscript> LoadAll(string path)
    {
        var transcripts = new List<ReplayTranscript>();
        var steps = new List<ReplayCommandStep>();
        var resolvedMagicJson = new List<string>();
        var dialogueRouteRecords = new List<DialogueRouteRecord>();
        var dialogueRecords = new List<DialogueResolutionRecord>();
        var claimExtractionRecords = new List<DialogueClaimExtractionRecord>();
        var dialogueParseRecords = new List<DialogueParseRecord>();
        var backgroundTextsByJobId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReplayObservationSummary? final = null;
        string? originId = null;
        var seed = 7;
        bool? backgroundEnabled = null;
        int? maxBackgroundJobs = null;
        int? backgroundJobsPerTurn = null;
        var quickstart = false;
        string? quickstartScene = null;
        var started = false;

        void FinishCurrentEpisode()
        {
            if (!started)
            {
                return;
            }

            transcripts.Add(new ReplayTranscript(
                seed,
                originId,
                backgroundEnabled,
                maxBackgroundJobs,
                backgroundJobsPerTurn,
                quickstart,
                quickstartScene,
                steps.ToArray(),
                resolvedMagicJson.ToArray(),
                dialogueRouteRecords.ToArray(),
                dialogueRecords.ToArray(),
                claimExtractionRecords.ToArray(),
                dialogueParseRecords.ToArray(),
                dialogueParseRecords
                    .Select(record => record.ParserRoute)
                    .Where(record => record is not null)
                    .Select(record => record!)
                    .ToArray(),
                new Dictionary<string, string>(backgroundTextsByJobId, StringComparer.OrdinalIgnoreCase),
                final));
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var recordType = ReadString(root, "recordType");
            if (IsStartRecord(recordType))
            {
                FinishCurrentEpisode();
                started = true;
                steps = new List<ReplayCommandStep>();
                resolvedMagicJson = new List<string>();
                dialogueRouteRecords = new List<DialogueRouteRecord>();
                dialogueRecords = new List<DialogueResolutionRecord>();
                claimExtractionRecords = new List<DialogueClaimExtractionRecord>();
                dialogueParseRecords = new List<DialogueParseRecord>();
                backgroundTextsByJobId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                final = null;

                seed = ReadInt(root, "seed", 7);
                originId = ReadNullableString(root, "originId");
                backgroundEnabled = ReadNullableBool(root, "backgroundEnabled");
                maxBackgroundJobs = ReadNullableInt(root, "maxBackgroundJobs");
                backgroundJobsPerTurn = ReadNullableInt(root, "backgroundJobsPerTurn");
                quickstartScene = ReadNullableString(root, "quickstartScene");
                quickstart = ReadBool(
                    root,
                    "quickstart",
                    fallback: !string.IsNullOrWhiteSpace(quickstartScene));
                continue;
            }

            if (IsStepRecord(recordType))
            {
                var command = ReadString(root, "command");
                var step = ReadInt(root, "step", steps.Count);
                steps.Add(new ReplayCommandStep(step, command));
                ReadBackgroundTexts(root, backgroundTextsByJobId);
                var magicJson = ReadMagicJson(root);
                if (!string.IsNullOrWhiteSpace(magicJson))
                {
                    resolvedMagicJson.Add(magicJson);
                }

                var route = ReadDialogueRouteRecord(root);
                if (route is not null)
                {
                    dialogueRouteRecords.Add(route);
                }

                var dialogue = ReadDialogueRecord(root);
                if (dialogue is not null)
                {
                    dialogueRecords.Add(dialogue);
                }

                claimExtractionRecords.AddRange(ReadClaimExtractionRecords(root));
                dialogueParseRecords.AddRange(ReadDialogueParseRecords(root));
                continue;
            }

            if (IsFinalRecord(recordType)
                && root.TryGetProperty("finalObservation", out var finalObservation))
            {
                ReadBackgroundTexts(root, backgroundTextsByJobId);
                final = ReplayObservationSummary.FromJson(finalObservation);
            }
        }

        FinishCurrentEpisode();
        return transcripts;
    }

    private static void ReadBackgroundTexts(
        JsonElement root,
        IDictionary<string, string> backgroundTextsByJobId)
    {
        if (root.TryGetProperty("result", out var result)
            && result.TryGetProperty("deltas", out var deltas)
            && deltas.ValueKind == JsonValueKind.Array)
        {
            foreach (var delta in deltas.EnumerateArray())
            {
                if (!delta.TryGetProperty("details", out var details)
                    || details.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var jobId = ReadNullableString(details, "backgroundJobId")
                    ?? ReadNullableString(details, "jobId");
                var resultText = ReadNullableString(details, "resultText");
                if (!string.IsNullOrWhiteSpace(jobId) && !string.IsNullOrWhiteSpace(resultText))
                {
                    backgroundTextsByJobId[jobId] = resultText;
                }
            }
        }

        foreach (var observationProperty in new[] { "observation", "finalObservation" })
        {
            if (!root.TryGetProperty(observationProperty, out var observation)
                || !observation.TryGetProperty("debug", out var debug)
                || !debug.TryGetProperty("backgroundJobs", out var jobs)
                || jobs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var job in jobs.EnumerateArray())
            {
                var jobId = ReadNullableString(job, "id");
                var resultText = ReadNullableString(job, "resultText");
                if (!string.IsNullOrWhiteSpace(jobId) && !string.IsNullOrWhiteSpace(resultText))
                {
                    backgroundTextsByJobId[jobId] = resultText;
                }
            }
        }
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

    private static DialogueResolutionRecord? ReadDialogueRecord(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("dialogue", out var dialogue)
            || dialogue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<DialogueResolutionRecord>(dialogue.GetRawText(), JsonOptions);
    }

    private static DialogueRouteRecord? ReadDialogueRouteRecord(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("dialogueRoute", out var route)
            || route.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<DialogueRouteRecord>(route.GetRawText(), JsonOptions);
    }

    private static IReadOnlyList<DialogueClaimExtractionRecord> ReadClaimExtractionRecords(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("dialogueClaimExtractions", out var records)
            || records.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<DialogueClaimExtractionRecord>();
        }

        return JsonSerializer.Deserialize<List<DialogueClaimExtractionRecord>>(records.GetRawText(), JsonOptions)
            ?? new List<DialogueClaimExtractionRecord>();
    }

    private static IReadOnlyList<DialogueParseRecord> ReadDialogueParseRecords(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("dialogueParses", out var records)
            || records.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<DialogueParseRecord>();
        }

        return JsonSerializer.Deserialize<List<DialogueParseRecord>>(records.GetRawText(), JsonOptions)
            ?? new List<DialogueParseRecord>();
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

    private static bool ReadBool(JsonElement root, string property, bool fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static bool IsStartRecord(string recordType) =>
        recordType.Equals("transcript_start", StringComparison.OrdinalIgnoreCase)
        || recordType.Equals("episode_start", StringComparison.OrdinalIgnoreCase);

    private static bool IsStepRecord(string recordType) =>
        recordType.Equals("transcript_step", StringComparison.OrdinalIgnoreCase)
        || recordType.Equals("episode_step", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinalRecord(string recordType) =>
        recordType.Equals("transcript_final", StringComparison.OrdinalIgnoreCase)
        || recordType.Equals("episode_final", StringComparison.OrdinalIgnoreCase);

    private sealed record ReplayTranscript(
        int Seed,
        string? OriginId,
        bool? BackgroundEnabled,
        int? MaxBackgroundJobs,
        int? BackgroundJobsPerTurn,
        bool Quickstart,
        string? QuickstartScene,
        IReadOnlyList<ReplayCommandStep> Steps,
        IReadOnlyList<string> ResolvedMagicJson,
        IReadOnlyList<DialogueRouteRecord> DialogueRouteRecords,
        IReadOnlyList<DialogueResolutionRecord> DialogueRecords,
        IReadOnlyList<DialogueClaimExtractionRecord> ClaimExtractionRecords,
        IReadOnlyList<DialogueParseRecord> DialogueParseRecords,
        IReadOnlyList<DialogueParserRouteRecord> DialogueParserRouteRecords,
        IReadOnlyDictionary<string, string> BackgroundTextsByJobId,
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
        int MaterializedDialogueRouteCount,
        int MaterializedDialogueCount,
        int MaterializedClaimExtractionCount,
        int MaterializedDialogueParseCount,
        int MaterializedBackgroundTextCount,
        ReplayObservationSummary FinalSummary,
        bool AssertFinal,
        string? AssertionError,
        IReadOnlyList<ReplayStepSummary> Steps,
        bool Failed);

    private sealed record ReplayObservationSummary(
        int Turn,
        string ControlledEntityId,
        string? ZoneId,
        int EntityCount,
        int ValidationIssues,
        int CanonRecords,
        int Promises,
        int Claims,
        int Rumors,
        int WorldTurns,
        int Bonds,
        int Memories,
        int BackgroundJobs,
        string BackgroundTextFingerprint,
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
                observation.Debug?.PromiseIds.Count ?? 0,
                observation.Debug?.Ledgers?.Claims ?? 0,
                observation.Debug?.Ledgers?.Rumors ?? 0,
                observation.Debug?.Ledgers?.WorldTurns ?? 0,
                observation.Debug?.Ledgers?.Bonds ?? 0,
                observation.Debug?.Ledgers?.Memories ?? 0,
                observation.Debug?.BackgroundJobs?.Count ?? 0,
                ComputeBackgroundTextFingerprint(observation.Debug?.BackgroundJobs),
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
                debug.ValueKind != JsonValueKind.Undefined && debug.TryGetProperty("promiseIds", out var promises) && promises.ValueKind == JsonValueKind.Array
                    ? promises.GetArrayLength()
                    : 0,
                ReadInt(ledgers, "claims"),
                ReadInt(ledgers, "rumors"),
                ReadInt(ledgers, "worldTurns"),
                ReadInt(ledgers, "bonds"),
                ReadInt(ledgers, "memories"),
                debug.ValueKind != JsonValueKind.Undefined && debug.TryGetProperty("backgroundJobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array
                    ? jobs.GetArrayLength()
                    : 0,
                ComputeBackgroundTextFingerprintFromJson(debug),
                ReadString(debug, "runStatus", "running"),
                ReadNullableString(debug, "runConclusion"));
        }

        private static string ComputeBackgroundTextFingerprint(IReadOnlyList<BackgroundJobCard>? jobs)
        {
            if (jobs is null || jobs.Count == 0)
            {
                return "";
            }

            return string.Join(
                "|",
                jobs
                    .Where(job => !string.IsNullOrWhiteSpace(job.ResultText))
                    .OrderBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(job => $"{job.Id}:{job.Purpose}:{job.TargetId}:{job.ResultText}"));
        }

        private static string ComputeBackgroundTextFingerprintFromJson(JsonElement debug)
        {
            if (debug.ValueKind == JsonValueKind.Undefined
                || !debug.TryGetProperty("backgroundJobs", out var jobs)
                || jobs.ValueKind != JsonValueKind.Array)
            {
                return "";
            }

            return string.Join(
                "|",
                jobs.EnumerateArray()
                    .Select(job => new
                    {
                        Id = ReadString(job, "id"),
                        Purpose = ReadString(job, "purpose"),
                        TargetId = ReadString(job, "targetId"),
                        ResultText = ReadNullableString(job, "resultText"),
                    })
                    .Where(job => !string.IsNullOrWhiteSpace(job.ResultText))
                    .OrderBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(job => $"{job.Id}:{job.Purpose}:{job.TargetId}:{job.ResultText}"));
        }
    }

    private sealed class ReplayBackgroundTextGenerator(IReadOnlyDictionary<string, string> textsByJobId) : IBackgroundTextGenerator
    {
        public string Name => "replay-background";

        public BackgroundTextGenerationResult Generate(BackgroundTextRequest request) =>
            textsByJobId.TryGetValue(request.JobId, out var text)
                ? new BackgroundTextGenerationResult(text, Provider: Name, Model: "transcript", AlreadyMaterialized: true)
                : new BackgroundTextGenerationResult(
                    null,
                    TechnicalFailure: true,
                    Error: $"No materialized background text for {request.JobId}.",
                    Provider: Name,
                    Model: "transcript");
    }
}
