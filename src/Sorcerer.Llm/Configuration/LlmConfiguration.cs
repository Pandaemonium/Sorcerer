namespace Sorcerer.Llm.Configuration;

public enum LlmPurpose
{
    Wild,
    Router,
    Dialogue,
    DialogueRouter,
    DialogueParserRouter,
    DialogueParser,
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
    bool Enabled = true,
    string? ApiKey = null,
    int? OllamaNumGpu = null);

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
        bool? enabled = null,
        string? apiKey = null,
        int? ollamaNumGpu = null)
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
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? current.ApiKey : apiKey,
            OllamaNumGpu = ollamaNumGpu ?? current.OllamaNumGpu,
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
            // The router reuses the resolver's provider/host/model by default so no second model is
            // loaded; it is a short call, hence the tighter timeout. Override with SORCERER_ROUTER_*.
            [LlmPurpose.Router] = PurposeFromEnvironment(LlmPurpose.Router, provider, host, model, 30),
            [LlmPurpose.Dialogue] = PurposeFromEnvironment(LlmPurpose.Dialogue, provider, host, model, 180),
            [LlmPurpose.DialogueRouter] = PurposeFromEnvironment(LlmPurpose.DialogueRouter, provider, host, model, 30),
            [LlmPurpose.DialogueParserRouter] = PurposeFromEnvironment(
                LlmPurpose.DialogueParserRouter,
                provider,
                host,
                model,
                30,
                defaultOllamaNumGpu: 0),
            [LlmPurpose.DialogueParser] = PurposeFromEnvironment(
                LlmPurpose.DialogueParser,
                provider,
                host,
                model,
                180,
                defaultOllamaNumGpu: 0),
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
        bool enabled = true,
        int? defaultOllamaNumGpu = null)
    {
        var prefix = $"SORCERER_{PurposeEnvironmentName(purpose)}";
        var provider = Environment.GetEnvironmentVariable($"{prefix}_PROVIDER") ?? defaultProvider;
        var host = Environment.GetEnvironmentVariable($"{prefix}_HOST") ?? defaultHost;
        var model = Environment.GetEnvironmentVariable($"{prefix}_MODEL") ?? defaultModel;
        var timeout = ReadInt($"{prefix}_TIMEOUT_SECONDS", defaultTimeoutSeconds);
        var concurrency = ReadInt($"{prefix}_MAX_CONCURRENT_CALLS", maxConcurrentCalls);
        var purposeEnabled = ReadBool($"{prefix}_ENABLED", enabled);
        var apiKey = Environment.GetEnvironmentVariable($"{prefix}_API_KEY") ?? DefaultApiKey();
        var ollamaNumGpu = ReadNullableInt($"{prefix}_NUM_GPU", defaultOllamaNumGpu);
        return new LlmPurposeSettings(provider, host, model, timeout, concurrency, purposeEnabled, apiKey, ollamaNumGpu);
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static int? ReadNullableInt(string name, int? fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed) && parsed >= 0
            ? parsed
            : fallback;

    private static bool ReadBool(string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
            ? parsed
            : fallback;

    private static string PurposeEnvironmentName(LlmPurpose purpose)
    {
        var name = purpose.ToString();
        var chars = new List<char>(name.Length + 2);
        for (var index = 0; index < name.Length; index++)
        {
            var ch = name[index];
            if (index > 0 && char.IsUpper(ch))
            {
                chars.Add('_');
            }

            chars.Add(char.ToUpperInvariant(ch));
        }

        return new string(chars.ToArray());
    }

    private static string? DefaultApiKey() =>
        Environment.GetEnvironmentVariable("SORCERER_OPENAI_API_KEY")
        ?? Environment.GetEnvironmentVariable("SORCERER_API_KEY")
        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
}

public sealed record LlmCallResult<T>(
    string Provider,
    string Model,
    T? Value,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    long ElapsedMilliseconds);
