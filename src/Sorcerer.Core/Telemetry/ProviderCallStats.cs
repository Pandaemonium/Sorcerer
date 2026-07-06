namespace Sorcerer.Core.Telemetry;

/// <summary>
/// Token and timing stats for one LLM call, as reported by the backend (Ollama's /api/chat
/// returns prompt_eval_count, eval_count, and the matching durations). All fields are optional:
/// mock providers and OpenAI-compatible backends may report none or only some of them.
/// LoadMs &gt; 0 on a warm session is the signature of runner-reload thrash (an option such as
/// num_ctx changed between calls); PromptTokens is the number that latency work must drive down,
/// because local casts are prompt-eval-bound (docs/OPTIMIZATION_PLAN.md, WS0.1).
/// </summary>
public sealed record ProviderCallStats(
    int? PromptTokens = null,
    int? OutputTokens = null,
    double? LoadMs = null,
    double? PromptEvalMs = null,
    double? GenerationMs = null,
    double? TotalMs = null);
