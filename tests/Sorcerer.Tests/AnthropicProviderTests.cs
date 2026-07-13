using System.Net;
using System.Text;
using System.Text.Json;
using Sorcerer.Cli;
using Sorcerer.Llm;
using Sorcerer.Llm.Configuration;
using Sorcerer.Llm.Diagnostics;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

[Collection("LlmTrace")]
public sealed class AnthropicProviderTests
{
    [Fact]
    public async Task NativeMessagesClientUsesSonnetFiveMediumAndCapturesUsageAndLatency()
    {
        var resolution = """
            {"accepted":true,"severity":"minor","outcomeText":"The lock remembers rain.","effects":[{"type":"message","text":"The lock remembers rain."}],"costs":[],"rejectedReason":null}
            """;
        var handler = new CapturingHttpHandler(AnthropicResponse(resolution));
        var client = new AnthropicMessagesClient(
            "https://api.anthropic.test/v1",
            "claude-sonnet-5",
            "medium",
            new HttpClient(handler),
            "anthropic-test-key");
        var provider = new OpenAiCompatibleSpellProvider(client, "anthropic");
        LlmTrace.Clear();

        var result = await provider.ResolveAsync(new SpellRequest(
            "ask the lock to remember rain",
            new { caster = new { id = "player" } },
            new[] { "message" }), CancellationToken.None);

        Assert.False(result.TechnicalFailure, result.Error);
        Assert.Equal("anthropic", result.Provider);
        Assert.Equal("message", Assert.Single(result.Resolution!.Effects).Type);
        Assert.Equal(150, result.Stats!.PromptTokens); // uncached + cache write + cache read
        Assert.Equal(40, result.Stats.OutputTokens);
        Assert.Equal(20, result.Stats.CacheWriteTokens);
        Assert.Equal(30, result.Stats.CacheReadTokens);
        Assert.Equal(15, result.Stats.ThinkingTokens);
        Assert.True(result.Stats.TotalMs >= 0);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.test/v1/messages", request.Uri);
        Assert.Null(request.AuthorizationScheme);
        Assert.Equal("anthropic-test-key", request.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);
        Assert.Contains("\"model\":\"claude-sonnet-5\"", request.Body);
        Assert.Contains("\"thinking\":{\"type\":\"adaptive\"}", request.Body);
        Assert.Contains("\"output_config\":{\"effort\":\"medium\"}", request.Body);
        Assert.DoesNotContain("temperature", request.Body, StringComparison.OrdinalIgnoreCase);

        var trace = Assert.Single(LlmTrace.Snapshot(), entry =>
            entry.Model.StartsWith("claude-sonnet-5", StringComparison.OrdinalIgnoreCase));
        Assert.True(trace.Completed);
        Assert.Equal(150, trace.Stats!.PromptTokens);
        Assert.True(trace.ElapsedMs >= 0);
    }

    [Fact]
    public void EveryProviderPurposeRecognizesAnthropicAndClaudeAliases()
    {
        var settings = new LlmPurposeSettings(
            "anthropic",
            "https://api.anthropic.com/v1",
            "claude-sonnet-5",
            30,
            Enabled: true,
            ApiKey: "test-key",
            Effort: "medium");

        Assert.Equal("anthropic", SpellProviderFactory.Create(settings).Name);
        Assert.Equal("anthropic-router", SpellRouterFactory.Create(settings).Name);
        Assert.Equal("anthropic-dialogue", DialogueProviderFactory.Create(settings).Name);
        Assert.Equal("anthropic-dialogue-router", DialogueRouterFactory.Create(settings).Name);
        Assert.Equal("anthropic-dialogue-parser-router", DialogueParserRouterFactory.Create(settings).Name);
        Assert.Equal("anthropic-dialogue-claims", DialogueParserFactory.Create(settings).Name);
        Assert.Equal("anthropic-dialogue-claims", DialogueClaimExtractorFactory.Create(settings).Name);
        Assert.Equal("anthropic-background", BackgroundTextGeneratorFactory.Create(settings)!.Name);

        Assert.Equal(
            "anthropic",
            SpellProviderFactory.Create(settings with { Provider = "claude" }).Name);
    }

    [Fact]
    public void PurposeOverrideCarriesEffortWithoutChangingOtherPurposes()
    {
        var configuration = new LlmConfiguration(new Dictionary<LlmPurpose, LlmPurposeSettings>
        {
            [LlmPurpose.Wild] = new("mock", null, null, 30),
            [LlmPurpose.Background] = new("mock", null, null, 30, Enabled: false),
        });

        var updated = configuration.WithPurposeOverride(
            LlmPurpose.Wild,
            provider: "anthropic",
            host: "https://api.anthropic.com/v1",
            model: "claude-sonnet-5",
            effort: "medium");

        Assert.Equal("anthropic", updated.SettingsFor(LlmPurpose.Wild).Provider);
        Assert.Equal("claude-sonnet-5", updated.SettingsFor(LlmPurpose.Wild).Model);
        Assert.Equal("medium", updated.SettingsFor(LlmPurpose.Wild).Effort);
        Assert.False(updated.SettingsFor(LlmPurpose.Background).Enabled);

        var defaults = configuration.WithPurposeOverride(LlmPurpose.Wild, provider: "anthropic");
        Assert.Equal("https://api.anthropic.com/v1", defaults.SettingsFor(LlmPurpose.Wild).Host);
        Assert.Equal("claude-sonnet-5", defaults.SettingsFor(LlmPurpose.Wild).Model);
        Assert.Equal("medium", defaults.SettingsFor(LlmPurpose.Wild).Effort);
    }

    [Fact]
    public void CliAcceptsClaudeSonnetFiveMediumConfiguration()
    {
        var options = CliOptions.Parse(new[]
        {
            "--provider", "anthropic",
            "--model", "claude-sonnet-5",
            "--effort", "medium",
        });

        Assert.Equal("anthropic", options.Provider);
        Assert.Equal("claude-sonnet-5", options.Model);
        Assert.Equal("medium", options.Effort);
    }

    [Fact]
    public void DotEnvLoadsQuotedAndExportedValuesWithoutOverwritingProcessEnvironment()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var loadedKey = $"SORCERER_TEST_LOADED_{suffix}";
        var quotedKey = $"SORCERER_TEST_QUOTED_{suffix}";
        var existingKey = $"SORCERER_TEST_EXISTING_{suffix}";
        var path = Path.Combine(Path.GetTempPath(), $"sorcerer-{suffix}.env");
        var previousExisting = Environment.GetEnvironmentVariable(existingKey);
        try
        {
            Environment.SetEnvironmentVariable(existingKey, "from-process");
            File.WriteAllText(
                path,
                $"# local test\n{loadedKey}=plain\nexport {quotedKey}=\"two words\"\n{existingKey}=from-file\n");

            var loadedPath = DotEnv.Load(path);

            Assert.Equal(Path.GetFullPath(path), loadedPath);
            Assert.Equal("plain", Environment.GetEnvironmentVariable(loadedKey));
            Assert.Equal("two words", Environment.GetEnvironmentVariable(quotedKey));
            Assert.Equal("from-process", Environment.GetEnvironmentVariable(existingKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(loadedKey, null);
            Environment.SetEnvironmentVariable(quotedKey, null);
            Environment.SetEnvironmentVariable(existingKey, previousExisting);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string AnthropicResponse(string content) =>
        JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            content = new object[]
            {
                new { type = "thinking", thinking = "hidden reasoning" },
                new { type = "text", text = content },
            },
            stop_reason = "end_turn",
            usage = new
            {
                input_tokens = 100,
                cache_creation_input_tokens = 20,
                cache_read_input_tokens = 30,
                output_tokens = 40,
                output_tokens_details = new { thinking_tokens = 15 },
            },
        });

    private sealed class CapturingHttpHandler(string response) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers
                .ToDictionary(
                    pair => pair.Key,
                    pair => string.Join(",", pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            Requests.Add(new CapturedRequest(
                request.RequestUri?.ToString() ?? "",
                request.Headers.Authorization?.Scheme,
                headers,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(
        string Uri,
        string? AuthorizationScheme,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
