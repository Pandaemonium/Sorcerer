using Sorcerer.Llm.Configuration;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public static class SpellProviderFactory
{
    public static ISpellProvider Create(LlmConfiguration configuration, LlmPurpose purpose) =>
        Create(configuration.SettingsFor(purpose));

    public static ISpellProvider Create(LlmPurposeSettings settings) =>
        settings.Enabled
            ? Create(settings.Provider, settings.Host, settings.Model, settings.TimeoutSeconds, settings.ApiKey, settings.Effort)
            : new MockSpellProvider();

    public static ISpellProvider Create(
        string provider,
        string? host = null,
        string? model = null,
        int? timeoutSeconds = null,
        string? apiKey = null,
        string? effort = null)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "" or "mock" => new MockSpellProvider(),
            "ollama" or "local" => new OllamaSpellProvider(
                host ?? "http://127.0.0.1:11434",
                model ?? "qwen3.5:9b",
                timeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds ?? 240))),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleSpellProvider(
                host ?? "https://api.openai.com/v1",
                model ?? "default",
                timeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds ?? 240)),
                apiKey: apiKey),
            "anthropic" or "claude" => new OpenAiCompatibleSpellProvider(
                new AnthropicMessagesClient(
                    host ?? "https://api.anthropic.com/v1",
                    model ?? "claude-sonnet-5",
                    effort,
                    apiKey: apiKey),
                "anthropic",
                timeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds ?? 240))),
            "gemini" or "google" => new OpenAiCompatibleSpellProvider(
                new GeminiInteractionsClient(
                    host ?? "https://generativelanguage.googleapis.com/v1beta",
                    model ?? "gemini-3.5-flash",
                    effort,
                    apiKey: apiKey),
                "gemini",
                timeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds ?? 240))),
            _ => new MockSpellProvider(),
        };
    }
}
