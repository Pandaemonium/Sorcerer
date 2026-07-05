using System.Text.Json;
using Sorcerer.Cli;
using Sorcerer.Core;
using Sorcerer.Core.Dialogue;
using Sorcerer.Llm;
using Sorcerer.Magic.Auditing;
using Xunit;

namespace Sorcerer.Tests;

public sealed class EpisodeRunnerTests
{
    [Fact]
    public async Task SocialQuickstartEpisodeExercisesSocialFlywheel()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"sorcerer-social-episode-{Guid.NewGuid():N}.jsonl");
        try
        {
            var exitCode = await EpisodeRunner.RunAsync(
                new MockSpellProvider(),
                new MockDialogueProvider(),
                new MockDialogueRouter(),
                DeterministicDialogueParserRouter.Instance,
                new MockDialogueClaimExtractor(),
                NullDialogueAuditSink.Instance,
                NullSpellAuditSink.Instance,
                backgroundTextGenerator: null,
                options: new EpisodeRunnerOptions(
                    Episodes: 1,
                    MaxTurns: 12,
                    Seed: 7,
                    LogPath: logPath,
                    QuickstartScene: "social"),
                json: false);

            Assert.Equal(0, exitCode);

            var commands = new List<string>();
            JsonElement? finalSummary = null;
            foreach (var line in File.ReadLines(logPath))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var recordType = root.GetProperty("recordType").GetString();
                if (string.Equals(recordType, "episode_step", StringComparison.OrdinalIgnoreCase))
                {
                    commands.Add(root.GetProperty("command").GetString() ?? "");
                }
                else if (string.Equals(recordType, "episode_final", StringComparison.OrdinalIgnoreCase))
                {
                    finalSummary = root.GetProperty("summary").Clone();
                }
            }

            Assert.Contains(commands, command => command.StartsWith("give grave salt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(commands, command => command.Contains("fine blade", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(commands, command => command.Equals("journal", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(commands, command => command.Equals("rumors", StringComparison.OrdinalIgnoreCase));
            Assert.True(finalSummary?.GetProperty("passed").GetBoolean());
            Assert.True(finalSummary?.GetProperty("promises").GetInt32() >= 1);

            var stepRecords = File.ReadLines(logPath)
                .Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                })
                .Where(root => string.Equals(
                    root.GetProperty("recordType").GetString(),
                    "episode_step",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Contains(stepRecords, root =>
                root.TryGetProperty("result", out var result)
                && result.TryGetProperty("dialogue", out var dialogue)
                && dialogue.ValueKind == JsonValueKind.Object);
            Assert.Contains(stepRecords, root =>
                root.TryGetProperty("result", out var result)
                && result.TryGetProperty("dialogue", out var dialogue)
                && dialogue.ValueKind == JsonValueKind.Object
                && dialogue.TryGetProperty("response", out var response)
                && response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("proposals", out var proposals)
                && proposals.ValueKind == JsonValueKind.Object
                && proposals.TryGetProperty("claims", out var claims)
                && claims.ValueKind == JsonValueKind.Array
                && claims.GetArrayLength() > 0);
            Assert.All(stepRecords, root =>
            {
                if (root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("dialogueClaimExtractions", out var records)
                    && records.ValueKind == JsonValueKind.Array)
                {
                    Assert.Equal(0, records.GetArrayLength());
                }
            });

            var replayExitCode = await TranscriptReplayRunner.RunAsync(
                logPath,
                assertFinal: true,
                json: false);
            Assert.Equal(0, replayExitCode);
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public async Task TranscriptReplayReusesMaterializedBackgroundText()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"sorcerer-background-replay-{Guid.NewGuid():N}.jsonl");
        var commands = new[] { "move east", "move east", "examine brazier", "wait" };
        try
        {
            var options = new CliOptions(
                Provider: "mock",
                Host: null,
                Model: null,
                Json: false,
                DebugState: true,
                Eval: false,
                Episode: false,
                Episodes: 1,
                MaxTurns: 40,
                Seed: 7,
                EpisodeLogPath: null,
                ScriptPath: null,
                TranscriptPath: logPath,
                ReplayPath: null,
                ReplayAssertFinal: false,
                ReparseAuditPath: null,
                OriginId: null,
                BackgroundProvider: "mock",
                BackgroundHost: null,
                BackgroundModel: null,
                BackgroundEnabled: null,
                MaxBackgroundJobs: 12,
                BackgroundJobsPerTurn: 1,
                BackgroundConcurrency: 1,
                Quickstart: false,
                QuickstartScene: null,
                Commands: commands);
            var session = GameSession.CreateImperialEncounter(
                backgroundTextGenerator: new MockBackgroundTextGenerator());
            await using (var transcript = TranscriptWriter.Open(logPath)!)
            {
                await transcript.WriteStartAsync(options, session.Observation(debug: true));
                foreach (var command in commands)
                {
                    var result = await session.ExecuteAsync(Program.ParseCommand(command));
                    await transcript.WriteStepAsync(command, result, session.Observation(debug: true));
                }

                await transcript.WriteFinalAsync(session.Observation(debug: true));
            }

            Assert.Contains(
                File.ReadLines(logPath),
                line => line.Contains("quiet generated detail", StringComparison.OrdinalIgnoreCase));

            var replayExitCode = await TranscriptReplayRunner.RunAsync(
                logPath,
                assertFinal: true,
                json: false);

            Assert.Equal(0, replayExitCode);
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }
}
