namespace Sorcerer.Llm.Configuration;

public enum LlmPurpose
{
    Wild,
    Dialogue,
    Item,
    Canon,
    Background,
    Agent,
}

public sealed record LlmPurposeSettings(
    string Provider,
    string? Host,
    string? Model,
    int TimeoutSeconds,
    int MaxConcurrentCalls = 1,
    bool Enabled = true);

public sealed record LlmConfiguration(
    IReadOnlyDictionary<LlmPurpose, LlmPurposeSettings> Purposes)
{
    public LlmPurposeSettings SettingsFor(LlmPurpose purpose) =>
        Purposes.TryGetValue(purpose, out var settings)
            ? settings
            : new LlmPurposeSettings("mock", null, null, 30);

    public static LlmConfiguration FromEnvironment()
    {
        var provider = Environment.GetEnvironmentVariable("SORCERER_PROVIDER") ?? "mock";
        var host = Environment.GetEnvironmentVariable("SORCERER_OLLAMA_HOST")
            ?? Environment.GetEnvironmentVariable("OLLAMA_HOST")
            ?? "http://127.0.0.1:11434";
        var model = Environment.GetEnvironmentVariable("SORCERER_MODEL") ?? "qwen3.5:9b";

        return new LlmConfiguration(new Dictionary<LlmPurpose, LlmPurposeSettings>
        {
            [LlmPurpose.Wild] = new(provider, host, model, 240),
            [LlmPurpose.Dialogue] = new(provider, host, model, 180),
            [LlmPurpose.Item] = new(provider, host, model, 180),
            [LlmPurpose.Canon] = new(provider, host, model, 180),
            [LlmPurpose.Background] = new("mock", host, model, 180, MaxConcurrentCalls: 1, Enabled: false),
            [LlmPurpose.Agent] = new("mock", host, model, 60),
        });
    }
}

public sealed record LlmCallResult<T>(
    string Provider,
    string Model,
    T? Value,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    long ElapsedMilliseconds);
