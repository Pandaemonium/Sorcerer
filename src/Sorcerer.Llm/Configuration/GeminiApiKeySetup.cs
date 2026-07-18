namespace Sorcerer.Llm.Configuration;

public sealed record GeminiApiKeyStatus(
    bool Available,
    string ExpectedVariableName,
    string? SourceVariable);

/// <summary>
/// Finds Gemini credentials without validating or exposing their value, and owns the setup text
/// shared by the CLI and Godot. Purpose-specific keys remain authoritative in
/// <see cref="LlmConfiguration"/>; this locator owns the provider-wide fallback chain.
/// </summary>
public static class GeminiApiKeySetup
{
    public const string DefaultVariableName = "GEMINI_API_KEY";
    public const string VariableNameSetting = "SORCERER_GEMINI_API_KEY_ENV_VAR";
    public const string AiStudioUrl = "https://aistudio.google.com/apikey";

    public static string ConfiguredVariableName
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(VariableNameSetting)?.Trim();
            return IsValidVariableName(configured) ? configured! : DefaultVariableName;
        }
    }

    public static bool IsGeminiProvider(string? provider) =>
        provider?.Trim().ToLowerInvariant() is "gemini" or "google";

    public static bool UsesGemini(LlmConfiguration configuration) =>
        configuration.Purposes.Values.Any(settings =>
            settings.Enabled && IsGeminiProvider(settings.Provider));

    public static GeminiApiKeyStatus Check(LlmConfiguration? configuration = null)
    {
        var candidate = ResolveCandidate();
        if (candidate is not null)
        {
            return new GeminiApiKeyStatus(true, ConfiguredVariableName, candidate.Value.Key);
        }

        var hasConfiguredPurposeKey = configuration?.Purposes.Values.Any(settings =>
            settings.Enabled
            && IsGeminiProvider(settings.Provider)
            && !string.IsNullOrWhiteSpace(settings.ApiKey)) == true;
        return new GeminiApiKeyStatus(
            hasConfiguredPurposeKey,
            ConfiguredVariableName,
            hasConfiguredPurposeKey ? "purpose-specific provider setting" : null);
    }

    public static string SetupInstructions()
    {
        var expected = ConfiguredVariableName;
        var primaryKeyLine = $"{expected}=paste-your-key-here";
        var customNameExample = expected.Equals(DefaultVariableName, StringComparison.Ordinal)
            ? $"Optional custom name:\n{VariableNameSetting}=MY_GEMINI_KEY\nMY_GEMINI_KEY=paste-your-key-here"
            : $"This installation expects the key in {expected}, configured by {VariableNameSetting}.";
        return "Gemini is selected, but no API key was found. Sorcerer will still start, but "
            + "provider-backed magic and dialogue cannot use Gemini until a key is present.\n\n"
            + $"1. Create a Gemini API key in Google AI Studio:\n{AiStudioUrl}\n\n"
            + "2. Create a file named .env beside the game executable (or in the repository root) and add:\n"
            + $"{primaryKeyLine}\nSORCERER_PROVIDER=gemini\n\n"
            + "3. Restart Sorcerer. The startup check only confirms that a non-empty value exists; "
            + "it never prints or validates the key.\n\n"
            + customNameExample;
    }

    internal static string? Resolve() => ResolveCandidate()?.Value;

    private static KeyValuePair<string, string>? ResolveCandidate()
    {
        var names = new[]
        {
            "SORCERER_GEMINI_API_KEY",
            "SORCERER_API_KEY",
            ConfiguredVariableName,
            DefaultVariableName,
            "GOOGLE_API_KEY",
        };
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new KeyValuePair<string, string>(name, value.Trim());
            }
        }

        return null;
    }

    private static bool IsValidVariableName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (char.IsLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsLetterOrDigit(character) || character == '_');
}
