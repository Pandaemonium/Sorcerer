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
    int? OllamaNumGpu = null,
    string? Effort = null);

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
        int? ollamaNumGpu = null,
        string? effort = null)
    {
        var current = SettingsFor(purpose);
        var providerChanged = !string.IsNullOrWhiteSpace(provider)
            && !provider.Equals(current.Provider, StringComparison.OrdinalIgnoreCase);
        var nextProvider = string.IsNullOrWhiteSpace(provider) ? current.Provider : provider;
        var updated = current with
        {
            Provider = nextProvider,
            Host = string.IsNullOrWhiteSpace(host)
                ? providerChanged ? DefaultHost(nextProvider) : current.Host
                : host,
            Model = string.IsNullOrWhiteSpace(model)
                ? providerChanged ? DefaultModel(nextProvider) : current.Model
                : model,
            TimeoutSeconds = timeoutSeconds ?? current.TimeoutSeconds,
            MaxConcurrentCalls = maxConcurrentCalls ?? current.MaxConcurrentCalls,
            Enabled = enabled ?? current.Enabled,
            ApiKey = string.IsNullOrWhiteSpace(apiKey)
                ? providerChanged ? DefaultApiKey(nextProvider) : current.ApiKey
                : apiKey,
            OllamaNumGpu = ollamaNumGpu ?? current.OllamaNumGpu,
            Effort = string.IsNullOrWhiteSpace(effort)
                ? providerChanged && UsesEffort(nextProvider) ? "medium" : current.Effort
                : effort,
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
        var host = Environment.GetEnvironmentVariable("SORCERER_HOST") ?? DefaultHost(provider);
        var model = Environment.GetEnvironmentVariable("SORCERER_MODEL") ?? DefaultModel(provider);
        var effort = Environment.GetEnvironmentVariable("SORCERER_EFFORT")
            ?? (UsesEffort(provider) ? "medium" : null);

        return new LlmConfiguration(new Dictionary<LlmPurpose, LlmPurposeSettings>
        {
            [LlmPurpose.Wild] = PurposeFromEnvironment(LlmPurpose.Wild, provider, host, model, 240, defaultEffort: effort),
            // The router reuses the resolver's provider/host/model by default so no second model is
            // loaded; it is a short call, hence the tighter timeout. Override with SORCERER_ROUTER_*.
            [LlmPurpose.Router] = PurposeFromEnvironment(LlmPurpose.Router, provider, host, model, 30, defaultEffort: effort),
            [LlmPurpose.Dialogue] = PurposeFromEnvironment(LlmPurpose.Dialogue, provider, host, model, 180, defaultEffort: effort),
            [LlmPurpose.DialogueRouter] = PurposeFromEnvironment(LlmPurpose.DialogueRouter, provider, host, model, 30, defaultEffort: effort),
            [LlmPurpose.DialogueParserRouter] = PurposeFromEnvironment(
                LlmPurpose.DialogueParserRouter,
                provider,
                host,
                model,
                30,
                defaultOllamaNumGpu: 0,
                defaultEffort: effort),
            [LlmPurpose.DialogueParser] = PurposeFromEnvironment(
                LlmPurpose.DialogueParser,
                provider,
                host,
                model,
                180,
                defaultOllamaNumGpu: 0,
                defaultEffort: effort),
            [LlmPurpose.Item] = PurposeFromEnvironment(LlmPurpose.Item, provider, host, model, 180, defaultEffort: effort),
            [LlmPurpose.Canon] = PurposeFromEnvironment(LlmPurpose.Canon, provider, host, model, 180, defaultEffort: effort),
            [LlmPurpose.Background] = PurposeFromEnvironment(
                LlmPurpose.Background,
                "mock",
                host,
                model,
                180,
                maxConcurrentCalls: 1,
                enabled: false,
                defaultEffort: effort),
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
        int? defaultOllamaNumGpu = null,
        string? defaultEffort = null)
    {
        var prefix = $"SORCERER_{PurposeEnvironmentName(purpose)}";
        var providerOverride = Environment.GetEnvironmentVariable($"{prefix}_PROVIDER");
        var provider = providerOverride ?? defaultProvider;
        var providerChanged = !string.IsNullOrWhiteSpace(providerOverride)
            && !provider.Equals(defaultProvider, StringComparison.OrdinalIgnoreCase);
        var host = Environment.GetEnvironmentVariable($"{prefix}_HOST")
            ?? (providerChanged ? DefaultHost(provider) : defaultHost);
        var model = Environment.GetEnvironmentVariable($"{prefix}_MODEL")
            ?? (providerChanged ? DefaultModel(provider) : defaultModel);
        var timeout = ReadInt($"{prefix}_TIMEOUT_SECONDS", defaultTimeoutSeconds);
        var concurrency = ReadInt($"{prefix}_MAX_CONCURRENT_CALLS", maxConcurrentCalls);
        var purposeEnabled = ReadBool($"{prefix}_ENABLED", enabled);
        var apiKey = Environment.GetEnvironmentVariable($"{prefix}_API_KEY") ?? DefaultApiKey(provider);
        var ollamaNumGpu = ReadNullableInt($"{prefix}_NUM_GPU", defaultOllamaNumGpu);
        var effort = Environment.GetEnvironmentVariable($"{prefix}_EFFORT")
            ?? defaultEffort
            ?? (UsesEffort(provider) ? "medium" : null);
        return new LlmPurposeSettings(provider, host, model, timeout, concurrency, purposeEnabled, apiKey, ollamaNumGpu, effort);
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

    private static string DefaultHost(string provider) =>
        IsGemini(provider)
            ? Environment.GetEnvironmentVariable("SORCERER_GEMINI_HOST")
                ?? Environment.GetEnvironmentVariable("GEMINI_BASE_URL")
                ?? "https://generativelanguage.googleapis.com/v1beta"
            : IsAnthropic(provider)
            ? Environment.GetEnvironmentVariable("SORCERER_ANTHROPIC_HOST")
                ?? Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
                ?? "https://api.anthropic.com/v1"
            : provider.Trim().ToLowerInvariant() is "api" or "openai" or "openai-compatible"
                ? Environment.GetEnvironmentVariable("SORCERER_OPENAI_HOST")
                    ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                    ?? "https://api.openai.com/v1"
                : Environment.GetEnvironmentVariable("SORCERER_OLLAMA_HOST")
                    ?? Environment.GetEnvironmentVariable("OLLAMA_HOST")
                    ?? "http://127.0.0.1:11434";

    private static string DefaultModel(string provider) =>
        IsGemini(provider)
            ? "gemini-3.5-flash"
            : IsAnthropic(provider)
            ? "claude-sonnet-5"
            : provider.Trim().ToLowerInvariant() is "api" or "openai" or "openai-compatible"
                ? "default"
                : "qwen3.5:9b";

    private static bool IsAnthropic(string provider) =>
        provider.Trim().ToLowerInvariant() is "anthropic" or "claude";

    private static bool IsGemini(string provider) =>
        provider.Trim().ToLowerInvariant() is "gemini" or "google";

    private static bool UsesEffort(string provider) =>
        IsAnthropic(provider) || IsGemini(provider);

    private static string? DefaultApiKey(string provider) =>
        IsGemini(provider)
            ? GeminiApiKeySetup.Resolve()
            : IsAnthropic(provider)
            ? Environment.GetEnvironmentVariable("SORCERER_ANTHROPIC_API_KEY")
                ?? Environment.GetEnvironmentVariable("SORCERER_API_KEY")
                ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            : Environment.GetEnvironmentVariable("SORCERER_OPENAI_API_KEY")
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
