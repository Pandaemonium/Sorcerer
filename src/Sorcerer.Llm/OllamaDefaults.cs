namespace Sorcerer.Llm;

/// <summary>
/// Shared Ollama request options. Every call against the same local model must use the same
/// num_ctx: Ollama rebuilds the model runner whenever options change, so a cast at 8192 followed
/// by a dialogue call at 4096 silently forced a full runner reload between actions
/// (docs/OPTIMIZATION_PLAN.md, WS2.1). One value also guarantees the dialogue path can never
/// truncate: its worst observed prompt (~4.3K tokens) exceeded the old 4096 dialogue window.
/// </summary>
public static class OllamaDefaults
{
    public static int NumCtx { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("SORCERER_NUM_CTX"), out var parsed) && parsed > 0
            ? parsed
            : 8192;
}
