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
public sealed class GeminiProviderTests
{
    [Fact]
    public async Task NativeInteractionsClientUsesJsonMediumThinkingAndCapturesUsageAndLatency()
    {
        var resolution = """
            {"accepted":true,"severity":"minor","outcomeText":"The lock remembers rain.","effects":[{"type":"message","text":"The lock remembers rain."}],"costs":[],"rejectedReason":null}
            """;
        var handler = new CapturingHttpHandler(GeminiResponse(resolution));
        var client = new GeminiInteractionsClient(
            "https://generativelanguage.test/v1beta",
            "gemini-3.5-flash",
            "medium",
            new HttpClient(handler),
            "gemini-test-key");
        var provider = new OpenAiCompatibleSpellProvider(client, "gemini");
        LlmTrace.Clear();

        var result = await provider.ResolveAsync(new SpellRequest(
            "ask the lock to remember rain",
            new { caster = new { id = "player" } },
            new[] { "message" }), CancellationToken.None);

        Assert.False(result.TechnicalFailure, result.Error);
        Assert.Equal("gemini", result.Provider);
        Assert.Equal("message", Assert.Single(result.Resolution!.Effects).Type);
        Assert.Equal(120, result.Stats!.PromptTokens);
        Assert.Equal(40, result.Stats.OutputTokens);
        Assert.Equal(10, result.Stats.CacheReadTokens);
        Assert.Equal(25, result.Stats.ThinkingTokens);
        Assert.True(result.Stats.TotalMs >= 0);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://generativelanguage.test/v1beta/interactions", request.Uri);
        Assert.Null(request.AuthorizationScheme);
        Assert.Equal("gemini-test-key", request.Headers["x-goog-api-key"]);
        Assert.Contains("\"model\":\"gemini-3.5-flash\"", request.Body);
        Assert.Contains("\"store\":false", request.Body);
        Assert.Contains("\"mime_type\":\"application/json\"", request.Body);
        Assert.Contains("\"additionalProperties\":true", request.Body);
        Assert.Contains("\"thinking_level\":\"medium\"", request.Body);
        Assert.Contains("\"max_output_tokens\":4096", request.Body);
        Assert.DoesNotContain("temperature", request.Body, StringComparison.OrdinalIgnoreCase);

        var trace = Assert.Single(LlmTrace.Snapshot(), entry =>
            entry.Model.StartsWith("gemini-3.5-flash", StringComparison.OrdinalIgnoreCase));
        Assert.True(trace.Completed);
        Assert.Equal(120, trace.Stats!.PromptTokens);
        Assert.True(trace.ElapsedMs >= 0);
    }

    [Fact]
    public async Task NativeInteractionsClientSurfacesGoogleErrorMessage()
    {
        var handler = new CapturingHttpHandler(
            JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = 429,
                    message = "Free-tier quota exceeded.",
                    status = "RESOURCE_EXHAUSTED",
                },
            }),
            HttpStatusCode.TooManyRequests);
        var client = new GeminiInteractionsClient(
            "https://generativelanguage.test/v1beta",
            "gemini-3.5-flash",
            httpClient: new HttpClient(handler),
            apiKey: "gemini-test-key");

        var result = await client.ChatAsync("system", "user", 1, 200, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("HTTP 429", result.Error);
        Assert.Contains("Free-tier quota exceeded.", result.Error);
    }

    [Fact]
    public void EveryProviderPurposeRecognizesGeminiAndGoogleAliases()
    {
        var settings = new LlmPurposeSettings(
            "gemini",
            "https://generativelanguage.googleapis.com/v1beta",
            "gemini-3.5-flash",
            30,
            Enabled: true,
            ApiKey: "test-key",
            Effort: "medium");

        Assert.Equal("gemini", SpellProviderFactory.Create(settings).Name);
        Assert.Equal("gemini-router", SpellRouterFactory.Create(settings).Name);
        Assert.Equal("gemini-dialogue", DialogueProviderFactory.Create(settings).Name);
        Assert.Equal("gemini-dialogue-router", DialogueRouterFactory.Create(settings).Name);
        Assert.Equal("gemini-dialogue-parser-router", DialogueParserRouterFactory.Create(settings).Name);
        Assert.Equal("gemini-dialogue-claims", DialogueParserFactory.Create(settings).Name);
        Assert.Equal("gemini-dialogue-claims", DialogueClaimExtractorFactory.Create(settings).Name);
        Assert.Equal("gemini-background", BackgroundTextGeneratorFactory.Create(settings)!.Name);

        Assert.Equal(
            "gemini",
            SpellProviderFactory.Create(settings with { Provider = "google" }).Name);
    }

    [Fact]
    public void PurposeOverrideSelectsGeminiDefaults()
    {
        var configuration = new LlmConfiguration(new Dictionary<LlmPurpose, LlmPurposeSettings>
        {
            [LlmPurpose.Wild] = new("mock", null, null, 30),
        });

        var updated = configuration.WithPurposeOverride(LlmPurpose.Wild, provider: "gemini");

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta", updated.SettingsFor(LlmPurpose.Wild).Host);
        Assert.Equal("gemini-3.5-flash", updated.SettingsFor(LlmPurpose.Wild).Model);
        Assert.Equal("medium", updated.SettingsFor(LlmPurpose.Wild).Effort);
    }

    [Fact]
    public void CliAcceptsGeminiMediumConfiguration()
    {
        var options = CliOptions.Parse(new[]
        {
            "--provider", "gemini",
            "--model", "gemini-3.5-flash",
            "--effort", "medium",
        });

        Assert.Equal("gemini", options.Provider);
        Assert.Equal("gemini-3.5-flash", options.Model);
        Assert.Equal("medium", options.Effort);
    }

    private static string GeminiResponse(string content) =>
        JsonSerializer.Serialize(new
        {
            id = "interaction_test",
            model = "gemini-3.5-flash",
            status = "completed",
            steps = new object[]
            {
                new { type = "thought", signature = "encrypted" },
                new
                {
                    type = "model_output",
                    content = new[] { new { type = "text", text = content } },
                },
            },
            usage = new
            {
                total_input_tokens = 120,
                total_output_tokens = 40,
                total_cached_tokens = 10,
                total_thought_tokens = 25,
                total_tokens = 185,
            },
        });

    private sealed class CapturingHttpHandler(
        string response,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
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
            return new HttpResponseMessage(statusCode)
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
