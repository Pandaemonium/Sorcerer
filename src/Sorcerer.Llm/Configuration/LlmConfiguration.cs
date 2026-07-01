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

    public LlmConfiguration WithPurposeOverride(
        LlmPurpose purpose,
        string? provider = null,
        string? host = null,
        string? model = null,
        int? timeoutSeconds = null,
        int? maxConcurrentCalls = null,
        bool? enabled = null)
    {
        var current = SettingsFor(purpose);
        var updated = current with
        {
            Provider = string.IsNullOrWhiteSpace(provider) ? current.Provider : provider,
            Host = string.IsNullOrWhiteSpace(host) ? current.Host : host,
            Model = string.IsNullOrWhiteSpace(model) ? current.Model : model,
            TimeoutSeconds = timeoutSeconds ?? current.TimeoutSeconds,
            MaxConcurrentCalls = maxConcurrentCalls ?? current.MaxConcurrentCalls,
            Enabled = enabled ?? current.Enabled,
        };
        var purposes = new Dictionary<LlmPurpose, LlmPurposeSettings>(Purposes)
        {
            [purpose] = updated,
        };
        return this with { Purposes = purposes };
    }

    public static LlmConfiguration FromEnvironment()
    {
        var provider = Environment.GetEnvironmentVariable("SORCERER_PROVIDER") ?? "mock";
        var host = Environment.GetEnvironmentVariable("SORCERER_OLLAMA_HOST")
            ?? Environment.GetEnvironmentVariable("OLLAMA_HOST")
            ?? "http://127.0.0.1:11434";
        var model = Environment.GetEnvironmentVariable("SORCERER_MODEL") ?? "qwen3.5:9b";

        return new LlmConfiguration(new Dictionary<LlmPurpose, LlmPurposeSettings>
        {
            [LlmPurpose.Wild] = PurposeFromEnvironment(LlmPurpose.Wild, provider, host, model, 240),
            [LlmPurpose.Dialogue] = PurposeFromEnvironment(LlmPurpose.Dialogue, provider, host, model, 180),
            [LlmPurpose.Item] = PurposeFromEnvironment(LlmPurpose.Item, provider, host, model, 180),
            [LlmPurpose.Canon] = PurposeFromEnvironment(LlmPurpose.Canon, provider, host, model, 180),
            [LlmPurpose.Background] = PurposeFromEnvironment(
                LlmPurpose.Background,
                "mock",
                host,
                model,
                180,
                maxConcurrentCalls: 1,
                enabled: false),
            [LlmPurpose.Agent] = PurposeFromEnvironment(LlmPurpose.Agent, "mock", host, model, 60),
        });
    }

    private static LlmPurposeSettings PurposeFromEnvironment(
        LlmPurpose purpose,
        string defaultProvider,
        string? defaultHost,
        string? defaultModel,
        int defaultTimeoutSeconds,
        int maxConcurrentCalls = 1,
        bool enabled = true)
    {
        var prefix = $"SORCERER_{purpose.ToString().ToUpperInvariant()}";
        var provider = Environment.GetEnvironmentVariable($"{prefix}_PROVIDER") ?? defaultProvider;
        var host = Environment.GetEnvironmentVariable($"{prefix}_HOST") ?? defaultHost;
        var model = Environment.GetEnvironmentVariable($"{prefix}_MODEL") ?? defaultModel;
        var timeout = ReadInt($"{prefix}_TIMEOUT_SECONDS", defaultTimeoutSeconds);
        var concurrency = ReadInt($"{prefix}_MAX_CONCURRENT_CALLS", maxConcurrentCalls);
        var purposeEnabled = ReadBool($"{prefix}_ENABLED", enabled);
        return new LlmPurposeSettings(provider, host, model, timeout, concurrency, purposeEnabled);
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static bool ReadBool(string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
            ? parsed
            : fallback;
}

public sealed record LlmCallResult<T>(
    string Provider,
    string Model,
    T? Value,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    long ElapsedMilliseconds);
