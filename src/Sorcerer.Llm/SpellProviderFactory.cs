using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public static class SpellProviderFactory
{
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
            _ => new MockSpellProvider(),
        };
    }
}

