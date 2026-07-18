# Providers And Configuration

Sorcerer should support local models and API-key providers through one provider interface.

Do not make engine rules depend on Ollama, HTTP details, API vendors, prompt formatting, or
model-specific quirks.

## Provider Goals

- Live wild-magic resolution from early playable builds.
- Mock provider for fast tests and agent runs.
- Local model provider support.
- API-key provider support.
- Separate foreground and background settings.
- User-controlled configuration.
- Robust audit logs.

## Provider Interface

Suggested shape:

```csharp
public interface ILlmProvider<TRequest, TResponse>
{
    string ProviderId { get; }
    Task<LlmCallResult<TResponse>> ResolveAsync(
        TRequest request,
        CancellationToken cancellationToken
    );
}
```

Provider code returns text or structured results. It does not mutate game state.

## Provider Purposes

Every call should have a purpose:

- `wild`
- `dialogue`
- `dialogue_router` (pre-speech context-card router; defaults to `dialogue`
  settings so local models do not thrash)
- `dialogue_parser_router` (post-speech parser-capability router; defaults
  local Ollama to CPU)
- `dialogue_parser` (post-speech mechanical parser; defaults local Ollama to CPU)
- `item`
- `canon`
- `background`
- `agent`

Purpose-specific settings allow the game to use different models, timeouts, hosts, or API
keys for different jobs.

Current implementation has `LlmConfiguration` and `LlmPurposeSettings` for these lanes. The CLI
creates foreground spell and generated-dialogue providers from the `wild` and
`dialogue` purposes, creates the pre-dialogue context router from
`dialogue_router`, creates the post-speech parser router from
`dialogue_parser_router`, and creates post-dialogue parsing from
`dialogue_parser`.
Background settings are kept separate and currently drive provider-backed
background text generation plus deterministic queue/throttle controls.
Dialogue context routing is a separate `dialogue_router` purpose, but it falls
back to the ordinary `dialogue` provider/model by default; using a separate tiny
router model is an opt-in optimization only when it actually lowers end-to-end
latency.
Purpose-specific environment variables follow
`SORCERER_<PURPOSE>_PROVIDER`, `SORCERER_<PURPOSE>_HOST`,
`SORCERER_<PURPOSE>_MODEL`, `SORCERER_<PURPOSE>_TIMEOUT_SECONDS`,
`SORCERER_<PURPOSE>_MAX_CONCURRENT_CALLS`, `SORCERER_<PURPOSE>_ENABLED`, and
`SORCERER_<PURPOSE>_API_KEY`. Anthropic and Gemini purposes also honor
`SORCERER_<PURPOSE>_EFFORT`; the shared fallback is `SORCERER_EFFORT`. Ollama-backed purposes honor
`SORCERER_<PURPOSE>_NUM_GPU` where the provider exposes it. Dialogue parser
lanes default `SORCERER_DIALOGUE_PARSER_ROUTER_NUM_GPU` and
`SORCERER_DIALOGUE_PARSER_NUM_GPU` to `0` so parser routing/detail can run on
CPU after visible speech instead of occupying the foreground GPU.

## Foreground Calls

Anything the player actively triggers can be on the urgent path:

- wild spells
- dialogue interpretation and NPC response
- reading or examining
- item identification
- other explicit interactions

Foreground failures should be visible. Technical failures should not consume a turn unless
the action had already committed a separate engine-owned cost.

## Background Calls

Background generation should default low.

Controls should include:

- enabled/disabled
- max concurrent background calls
- max queued calls
- purpose-specific model/provider
- developer queue view
- CLI queue/debug output

Background jobs are enrichment, not critical-path gameplay.

## Local And API Providers

Ollama can be supported, but Sorcerer should not require Ollama as the only real option.

Provider implementations can include:

- Ollama for local `/api/chat`
- Anthropic's native `/v1/messages` API
- Google's native Gemini `/v1beta/interactions` API
- OpenAI-compatible local or hosted `/v1/chat/completions` endpoints
- mock
- deterministic fixture provider
- replay provider fed by materialized transcript JSON

Configuration should make it easy for users to use their preferred model. The current native
Anthropic, native Gemini, and OpenAI-compatible adapters are implemented for wild magic, generated dialogue,
dialogue routing/parsing, dialogue-claim extraction, and background text generation. They parse
assistant content and reuse the same engine-side parsing/repair contracts as the Ollama adapters.
`--host` may be either a base URL such as `https://api.openai.com/v1` or a full
`/chat/completions` endpoint.

### Gemini 3.5 Flash (medium thinking)

Gemini uses Google's native, stateless Interactions API rather than its OpenAI compatibility
layer. Sorcerer requests JSON object output, sets `store: false`, and uses `medium` thinking by
default. The model id is `gemini-3.5-flash`; `gemini` and `google` are accepted provider names.
Google currently lists this stable model on the API free tier. See the official
[model guide](https://ai.google.dev/gemini-api/docs/models),
[thinking guide](https://ai.google.dev/gemini-api/docs/thinking), and
[billing guide](https://ai.google.dev/gemini-api/docs/billing).

Create a key in [Google AI Studio](https://aistudio.google.com/apikey), then put it in the local
git-ignored `.env`:

```dotenv
GEMINI_API_KEY=your-key-here
SORCERER_PROVIDER=gemini
SORCERER_HOST=https://generativelanguage.googleapis.com/v1beta
SORCERER_MODEL=gemini-3.5-flash
SORCERER_EFFORT=medium
SORCERER_BACKGROUND_ENABLED=false
```

At startup, if any enabled provider purpose uses Gemini, Sorcerer checks only whether a non-empty
key is available. It does not make a validation request. The Godot game shows a non-blocking setup
dialog when the key is absent; the CLI prints the same instructions to stderr so `--json` stdout
remains machine-readable.

`GEMINI_API_KEY` is the default. To keep the credential under a different environment-variable
name, configure that name in the same `.env` file:

```dotenv
SORCERER_GEMINI_API_KEY_ENV_VAR=MY_GEMINI_KEY
MY_GEMINI_KEY=your-key-here
```

The configured variable participates in the existing fallback chain; purpose-specific
`SORCERER_<PURPOSE>_API_KEY`, `SORCERER_GEMINI_API_KEY`, `SORCERER_API_KEY`, `GEMINI_API_KEY`, and
`GOOGLE_API_KEY` values are still accepted.

Then launch Godot normally, or run the CLI explicitly:

```powershell
dotnet run --project src/Sorcerer.Cli -- `
  --provider gemini `
  --host https://generativelanguage.googleapis.com/v1beta `
  --model gemini-3.5-flash `
  --effort medium
```

### Claude Sonnet 5 (medium effort)

Claude uses the native Messages API rather than Anthropic's OpenAI compatibility layer. This is
important for Sonnet 5: non-default sampling parameters are rejected, adaptive thinking is on by
default, and effort belongs in `output_config`. Sorcerer sends `thinking: {type: "adaptive"}` and
`output_config: {effort: "medium"}` by default for an Anthropic provider. The model id is
`claude-sonnet-5`. See Anthropic's [Sonnet 5 notes](https://platform.claude.com/docs/en/docs/about-claude/models/whats-new-sonnet-5)
and [effort reference](https://platform.claude.com/docs/en/build-with-claude/effort).

The CLI and Godot automatically load the first `.env` found while walking upward from the working
or application directory. A process/OS environment variable always wins over the file. The root
`.env` is git-ignored, while `.env.example` is the safe committed template. Put the key in the local
file and never commit it:

```dotenv
ANTHROPIC_API_KEY=your-key-here
SORCERER_PROVIDER=anthropic
SORCERER_HOST=https://api.anthropic.com/v1
SORCERER_MODEL=claude-sonnet-5
SORCERER_EFFORT=medium
SORCERER_BACKGROUND_ENABLED=false
```

Then launch Godot normally, or run the CLI explicitly:

```powershell
dotnet run --project src/Sorcerer.Cli -- `
  --provider anthropic `
  --host https://api.anthropic.com/v1 `
  --model claude-sonnet-5 `
  --effort medium
```

The Godot provider row accepts the same provider/host/model/effort values as the CLI.

`ReplaySpellProvider` is not a live model provider. It replays normalized `SpellResolution` JSON
captured in transcripts so command sequences that touched wild magic can be reproduced without
calling the model again.

Useful CLI controls:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu `
  --disable-background `
  --command "cast bind the nearest soldier in blue glass"
```

Use `--background-provider`, `--background-host`, and `--background-model` to run separate
background text generation behind the job queue. Use `--max-background-jobs` and
`--background-jobs-per-turn` to throttle the lane; when the background provider is disabled or
fails, the turn pump falls back to deterministic routed-lore text.

The Godot menu uses the same provider factories for new runs. Its provider, host, model, and effort fields
default from `SORCERER_PROVIDER`, `SORCERER_HOST`/provider-specific host variables, and
`SORCERER_MODEL`/`SORCERER_EFFORT`, so switching GUI playtests between Ollama, mock, Anthropic,
Gemini, and OpenAI-compatible endpoints
does not require a separate code path.

## Secrets

API keys must not be stored in committed files.

Local `.env` files are ignored by git. `.env.example` documents variable names without secrets.

The OpenAI-compatible adapter reads purpose-specific keys first
(`SORCERER_WILD_API_KEY`, `SORCERER_DIALOGUE_API_KEY`, and so on through
`SORCERER_<PURPOSE>_API_KEY`), then falls back to `SORCERER_OPENAI_API_KEY`, `SORCERER_API_KEY`,
and `OPENAI_API_KEY`. Local OpenAI-compatible endpoints that do not require auth can omit these.
The Anthropic adapter reads the same purpose-specific key first, then
`SORCERER_ANTHROPIC_API_KEY`, `SORCERER_API_KEY`, and `ANTHROPIC_API_KEY`.
The Gemini adapter reads the purpose-specific key first, then `SORCERER_GEMINI_API_KEY`,
`SORCERER_API_KEY`, the variable named by `SORCERER_GEMINI_API_KEY_ENV_VAR` (default
`GEMINI_API_KEY`), and `GOOGLE_API_KEY`.

## Audits

Audit records should include provider information, but not secrets.

Current JSONL sinks:

- `logs/wild_magic_audit.jsonl` for spell resolution
- `logs/dialogue_audit.jsonl` for generated dialogue and claim extraction context
- `logs/background_audit.jsonl` for background text generation

Record:

- purpose
- provider id
- model id
- request metadata
- prompt or compact request
- raw response
- parsed response
- validation errors
- elapsed time
- token counts when available

Anthropic responses populate total input tokens (including cache creation/read tokens), output
tokens, cache-read tokens, cache-write tokens, thinking tokens, and end-to-end HTTP milliseconds.
Gemini responses populate input, output, cached, and thinking tokens plus end-to-end HTTP
milliseconds.
Wild-magic and dialogue JSONL audits retain these as `providerStats`; background audits do the
same. In Godot, press F6 to open the LLM debug panel: every call—including routers and parsers—shows
elapsed time and an input→output token summary, with the detailed cache/thinking breakdown on
selection.
