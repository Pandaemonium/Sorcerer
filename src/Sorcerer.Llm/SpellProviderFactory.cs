using Sorcerer.Llm.Configuration;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public static class SpellProviderFactory
{
    public static ISpellProvider Create(LlmConfiguration configuration, LlmPurpose purpose) =>
        Create(configuration.SettingsFor(purpose));

    public static ISpellProvider Create(LlmPurposeSettings settings) =>
        settings.Enabled
            ? Create(settings.Provider, settings.Host, settings.Model)
            : new MockSpellProvider();

    public static ISpellProvider Create(
        string provider,
        string? host = null,
        string? model = null)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "" or "mock" => new MockSpellProvider(),
            "ollama" or "local" => new OllamaSpellProvider(
                host ?? "http://127.0.0.1:11434",
                model ?? "qwen3.5:9b"),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleSpellProvider(
                host ?? "https://api.openai.com/v1",
                model ?? "default"),
            _ => new MockSpellProvider(),
        };
    }
}
