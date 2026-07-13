using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Core.Runtime;
using Sorcerer.Llm.Configuration;

namespace Sorcerer.Llm;

public static class BackgroundTextGeneratorFactory
{
    public static IBackgroundTextGenerator? Create(
        LlmConfiguration configuration,
        LlmPurpose purpose,
        IBackgroundTextAuditSink? audit = null) =>
        Create(configuration.SettingsFor(purpose), audit);

    public static IBackgroundTextGenerator? Create(
        LlmPurposeSettings settings,
        IBackgroundTextAuditSink? audit = null)
    {
        if (!settings.Enabled)
        {
            return null;
        }

        audit ??= NullBackgroundTextAuditSink.Instance;

        return settings.Provider.Trim().ToLowerInvariant() switch
        {
            "" or "mock" => new MockBackgroundTextGenerator(audit),
            "ollama" or "local" => new OllamaBackgroundTextGenerator(
                settings.Host ?? "http://127.0.0.1:11434",
                settings.Model ?? "qwen3.5:9b",
                TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                Math.Max(1, settings.MaxConcurrentCalls),
                audit),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleBackgroundTextGenerator(
                settings.Host ?? "https://api.openai.com/v1",
                settings.Model ?? "default",
                TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                Math.Max(1, settings.MaxConcurrentCalls),
                audit,
                settings.ApiKey),
            "anthropic" or "claude" => new OpenAiCompatibleBackgroundTextGenerator(
                new AnthropicMessagesClient(
                    settings.Host ?? "https://api.anthropic.com/v1",
                    settings.Model ?? "claude-sonnet-5",
                    settings.Effort,
                    apiKey: settings.ApiKey),
                "anthropic-background",
                settings.Model ?? "claude-sonnet-5",
                TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                Math.Max(1, settings.MaxConcurrentCalls),
                audit),
            "gemini" or "google" => new OpenAiCompatibleBackgroundTextGenerator(
                new GeminiInteractionsClient(
                    settings.Host ?? "https://generativelanguage.googleapis.com/v1beta",
                    settings.Model ?? "gemini-3.5-flash",
                    settings.Effort,
                    apiKey: settings.ApiKey),
                "gemini-background",
                settings.Model ?? "gemini-3.5-flash",
                TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                Math.Max(1, settings.MaxConcurrentCalls),
                audit),
            _ => new MockBackgroundTextGenerator(audit),
        };
    }
}

public sealed record BackgroundTextAuditEntry(
    DateTimeOffset Timestamp,
    string Provider,
    string? Model,
    BackgroundTextRequest Request,
    string RawText,
    string? ParsedText,
    bool TechnicalFailure,
    string? Error,
    long ElapsedMilliseconds,
    Sorcerer.Core.Telemetry.ProviderCallStats? ProviderStats = null);

public interface IBackgroundTextAuditSink
{
    void Record(BackgroundTextAuditEntry entry);
}

public sealed class NullBackgroundTextAuditSink : IBackgroundTextAuditSink
{
    public static NullBackgroundTextAuditSink Instance { get; } = new();

    private NullBackgroundTextAuditSink()
    {
    }

    public void Record(BackgroundTextAuditEntry entry)
    {
    }
}

public sealed class MockBackgroundTextGenerator(IBackgroundTextAuditSink? audit = null) : IBackgroundTextGenerator
{
    public string Name => "mock-background";

    public BackgroundTextGenerationResult Generate(BackgroundTextRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var text = request.Purpose.Equals("rumor_distortion", StringComparison.OrdinalIgnoreCase)
            ? $"It is said that {LowerFirst((request.OriginalText ?? "the rumor changed").Trim().TrimEnd('.'))}."
            : $"{request.TargetName ?? request.TargetId} carries a quiet generated detail from {request.RegionId}.";
        var result = new BackgroundTextGenerationResult(text, Provider: Name, Model: "mock");
        BackgroundTextAudit.Record(audit, request, result, stopwatch.ElapsedMilliseconds);
        return result;
    }

    private static string LowerFirst(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? "the rumor changed"
            : $"{char.ToLowerInvariant(text[0])}{text[1..]}";
}

public sealed class OllamaBackgroundTextGenerator : IBackgroundTextGenerator
{
    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate;
    private readonly IBackgroundTextAuditSink _audit;

    public OllamaBackgroundTextGenerator(
        string host,
        string model,
        TimeSpan timeout,
        int maxConcurrentCalls,
        IBackgroundTextAuditSink? audit = null,
        HttpClient? httpClient = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _timeout = timeout;
        _gate = new SemaphoreSlim(maxConcurrentCalls, maxConcurrentCalls);
        _audit = audit ?? NullBackgroundTextAuditSink.Instance;
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public string Name => "ollama-background";

    public BackgroundTextGenerationResult Generate(BackgroundTextRequest request) =>
        // Run on a thread-pool thread with no captured SynchronizationContext. Called
        // synchronously from the turn pump, which on Godot runs on the main thread; awaiting
        // in-place here would try to marshal continuations back onto that same blocked thread
        // and deadlock the GUI permanently.
        Task.Run(() => GenerateAsync(request)).GetAwaiter().GetResult();

    private async Task<BackgroundTextGenerationResult> GenerateAsync(BackgroundTextRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await GenerateCoreAsync(request);
        BackgroundTextAudit.Record(_audit, request, result, stopwatch.ElapsedMilliseconds);
        return result;
    }

    private async Task<BackgroundTextGenerationResult> GenerateCoreAsync(BackgroundTextRequest request)
    {
        if (!await _gate.WaitAsync(_timeout))
        {
            return Failure("Background generator concurrency gate timed out.");
        }

        var systemPrompt = BackgroundTextPrompt.SystemPrompt();
        var userPrompt = BackgroundTextPrompt.UserPrompt(request);
        var traceId = Diagnostics.LlmTrace.Begin("background", _model, systemPrompt, userPrompt);
        var raw = string.Empty;
        try
        {
            using var timeout = new CancellationTokenSource(_timeout);
            var payload = new
            {
                model = _model,
                stream = false,
                format = "json",
                think = false,
                options = new
                {
                    temperature = 0.45,
                    num_ctx = OllamaDefaults.NumCtx,
                    num_predict = 220,
                },
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
            };
            var response = await _httpClient.PostAsJsonAsync($"{_host}/api/chat", payload, timeout.Token);
            raw = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var httpError = $"Ollama returned HTTP {(int)response.StatusCode}.";
                Diagnostics.LlmTrace.End(traceId, raw, httpError);
                return Failure(httpError, raw);
            }

            using var document = JsonDocument.Parse(raw);
            var content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            Diagnostics.LlmTrace.End(traceId, content, null);
            return BackgroundTextPrompt.FromContent(content, raw, Name, _model);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            Diagnostics.LlmTrace.End(traceId, raw, ex.Message);
            return Failure(ex.Message, raw);
        }
        finally
        {
            _gate.Release();
        }
    }

    private BackgroundTextGenerationResult Failure(string error, string raw = "") =>
        new(null, TechnicalFailure: true, Error: error, Provider: Name, Model: _model, RawText: raw);

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

public sealed class OpenAiCompatibleBackgroundTextGenerator : IBackgroundTextGenerator
{
    private readonly IJsonChatClient _chat;
    private readonly string _name;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate;
    private readonly IBackgroundTextAuditSink _audit;

    public OpenAiCompatibleBackgroundTextGenerator(
        string endpoint,
        string model,
        TimeSpan timeout,
        int maxConcurrentCalls,
        IBackgroundTextAuditSink? audit = null,
        string? apiKey = null,
        HttpClient? httpClient = null)
        : this(
            new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey),
            "openai-compatible-background",
            model,
            timeout,
            maxConcurrentCalls,
            audit)
    {
    }

    internal OpenAiCompatibleBackgroundTextGenerator(
        IJsonChatClient chat,
        string name,
        string model,
        TimeSpan timeout,
        int maxConcurrentCalls,
        IBackgroundTextAuditSink? audit = null)
    {
        _chat = chat;
        _name = name;
        _model = model;
        _timeout = timeout;
        _gate = new SemaphoreSlim(maxConcurrentCalls, maxConcurrentCalls);
        _audit = audit ?? NullBackgroundTextAuditSink.Instance;
    }

    public string Name => _name;

    public BackgroundTextGenerationResult Generate(BackgroundTextRequest request) =>
        // See OllamaBackgroundTextGenerator.Generate: must not await in-place on the calling
        // thread, or the turn pump deadlocks the Godot GUI's main thread.
        Task.Run(() => GenerateAsync(request)).GetAwaiter().GetResult();

    private async Task<BackgroundTextGenerationResult> GenerateAsync(BackgroundTextRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await GenerateCoreAsync(request);
        BackgroundTextAudit.Record(_audit, request, result, stopwatch.ElapsedMilliseconds);
        return result;
    }

    private async Task<BackgroundTextGenerationResult> GenerateCoreAsync(BackgroundTextRequest request)
    {
        if (!await _gate.WaitAsync(_timeout))
        {
            return Failure("Background generator concurrency gate timed out.");
        }

        try
        {
            using var timeout = new CancellationTokenSource(_timeout);
            var result = await _chat.ChatAsync(
                BackgroundTextPrompt.SystemPrompt(),
                BackgroundTextPrompt.UserPrompt(request),
                temperature: 0.45,
                maxTokens: 220,
                timeout.Token,
                label: "background");
            return result.Success
                ? BackgroundTextPrompt.FromContent(result.Content, result.RawText, Name, _model, result.Stats)
                : Failure(result.Error ?? $"{Name} generation failed.", result.RawText, result.Stats);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return Failure(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private BackgroundTextGenerationResult Failure(
        string error,
        string raw = "",
        Sorcerer.Core.Telemetry.ProviderCallStats? stats = null) =>
        new(null, TechnicalFailure: true, Error: error, Provider: Name, Model: _model, RawText: raw, ProviderStats: stats);
}

internal static class BackgroundTextAudit
{
    public static void Record(
        IBackgroundTextAuditSink? audit,
        BackgroundTextRequest request,
        BackgroundTextGenerationResult result,
        long elapsedMilliseconds)
    {
        (audit ?? NullBackgroundTextAuditSink.Instance).Record(new BackgroundTextAuditEntry(
            DateTimeOffset.UtcNow,
            result.Provider,
            result.Model,
            request,
            result.RawText ?? "",
            result.Text,
            result.TechnicalFailure,
            result.Error,
            elapsedMilliseconds,
            result.ProviderStats));
    }
}

internal static class BackgroundTextPrompt
{
    public static string SystemPrompt() =>
        "You write short in-fiction background enrichment for Sorcerer, a folk-magic roguelike. "
        + "The game engine decides what is real; you only provide candidate prose. "
        + "Do not create mechanics, rewards, NPCs, locations, inventory, quests, or guaranteed facts beyond the request. "
        // Voice (docs/AESTHETICS_AND_TONE.md, narration voice): grounded and specific, never portentous.
        + "Keep the voice organic and concrete - named things, plain motives, ordinary detail - not cosmic or fateful. "
        + "Do not write that \"the world remembers\" or that stories \"spread\" on their own; consequences travel because a specific kind of person carries them. "
        + "For rumor_distortion, retell the given rumor as a plausible distorted version while preserving the core claim, and make it sound like a particular teller - a drover, a clerk, a ferry passenger - repeating it, not an omniscient narrator. "
        + "For entity_detail or canon_detail, write one vivid, concrete sentence about the target using its tags, material, region, and routed lore. "
        + "Return only JSON: {\"text\":\"...\"}. Keep text under 55 words.";

    public static string UserPrompt(BackgroundTextRequest request)
    {
        var tags = request.Tags.Count == 0 ? "none" : string.Join(", ", request.Tags.Take(12));
        return "Background request:\n"
            + $"jobId: {request.JobId}\n"
            + $"purpose: {request.Purpose}\n"
            + $"targetId: {request.TargetId}\n"
            + $"targetKind: {request.TargetKind}\n"
            + $"targetName: {request.TargetName ?? "unknown"}\n"
            + $"material: {request.TargetMaterial ?? "unknown"}\n"
            + $"region: {request.RegionId}\n"
            + $"tags: {tags}\n"
            + $"routedLore: {request.RoutedLore ?? "none"}\n"
            + $"originalText: {request.OriginalText ?? "none"}";
    }

    public static BackgroundTextGenerationResult FromContent(
        string content,
        string raw,
        string provider,
        string model,
        Sorcerer.Core.Telemetry.ProviderCallStats? stats = null)
    {
        var text = ExtractText(content);
        return string.IsNullOrWhiteSpace(text)
            ? new BackgroundTextGenerationResult(
                null,
                TechnicalFailure: true,
                Error: "Background provider returned no text.",
                Provider: provider,
                Model: model,
                RawText: raw,
                ProviderStats: stats)
            : new BackgroundTextGenerationResult(
                text.Trim(),
                TechnicalFailure: false,
                Error: null,
                Provider: provider,
                Model: model,
                RawText: raw,
                ProviderStats: stats);
    }

    private static string? ExtractText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in new[] { "text", "description", "rumor", "result" })
                {
                    if (root.TryGetProperty(property, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString();
                    }
                }
            }
        }
        catch (JsonException)
        {
            return content.Trim();
        }

        return null;
    }
}
