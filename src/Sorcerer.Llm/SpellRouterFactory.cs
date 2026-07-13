using Sorcerer.Llm.Configuration;
using Sorcerer.Magic.Capabilities;

namespace Sorcerer.Llm;

public static class SpellRouterFactory
{
    public static ISpellRouter Create(LlmConfiguration configuration, LlmPurpose purpose = LlmPurpose.Router) =>
        Create(configuration.SettingsFor(purpose));

    public static ISpellRouter Create(LlmPurposeSettings settings) =>
        settings.Enabled
            ? Create(settings.Provider, settings.Host, settings.Model, settings.TimeoutSeconds, settings.ApiKey, settings.Effort)
            : NullSpellRouter.Instance;

    public static ISpellRouter Create(
        string provider,
        string? host = null,
        string? model = null,
        int? timeoutSeconds = null,
        string? apiKey = null,
        string? effort = null)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds ?? 30));
        return provider.Trim().ToLowerInvariant() switch
        {
            // No usable local/remote model configured: contribute nothing, so selection stays
            // keyword-only rather than paying for a call that cannot help.
            "" or "mock" => NullSpellRouter.Instance,
            "ollama" or "local" => new OllamaSpellRouter(
                host ?? "http://127.0.0.1:11434",
                model ?? "qwen3.5:9b",
                timeout: timeout),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleSpellRouter(
                host ?? "https://api.openai.com/v1",
                model ?? "default",
                timeout: timeout,
                apiKey: apiKey),
            "anthropic" or "claude" => new OpenAiCompatibleSpellRouter(
                new AnthropicMessagesClient(
                    host ?? "https://api.anthropic.com/v1",
                    model ?? "claude-sonnet-5",
                    effort,
                    apiKey: apiKey),
                "anthropic-router",
                timeout),
            "gemini" or "google" => new OpenAiCompatibleSpellRouter(
                new GeminiInteractionsClient(
                    host ?? "https://generativelanguage.googleapis.com/v1beta",
                    model ?? "gemini-3.5-flash",
                    effort,
                    apiKey: apiKey),
                "gemini-router",
                timeout),
            _ => NullSpellRouter.Instance,
        };
    }
}
