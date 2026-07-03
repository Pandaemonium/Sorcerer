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
- `item`
- `canon`
- `background`
- `agent`

Purpose-specific settings allow the game to use different models, timeouts, hosts, or API
keys for different jobs.

Current implementation has `LlmConfiguration` and `LlmPurposeSettings` for these lanes. The CLI
creates foreground spell, generated-dialogue, and dialogue-claim providers from the `wild` and
`dialogue` purposes, while background settings are kept separate and currently drive only
deterministic queue/throttle controls until real background provider calls are wired in.
Purpose-specific environment variables follow
`SORCERER_<PURPOSE>_PROVIDER`, `SORCERER_<PURPOSE>_HOST`,
`SORCERER_<PURPOSE>_MODEL`, `SORCERER_<PURPOSE>_TIMEOUT_SECONDS`,
`SORCERER_<PURPOSE>_MAX_CONCURRENT_CALLS`, `SORCERER_<PURPOSE>_ENABLED`, and
`SORCERER_<PURPOSE>_API_KEY`.

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
- OpenAI-compatible local or hosted `/v1/chat/completions` endpoints
- mock
- deterministic fixture provider
- replay provider fed by materialized transcript JSON

Configuration should make it easy for users to use their preferred model. The current
OpenAI-compatible adapter is implemented for wild magic, generated dialogue, dialogue-claim
extraction, and background text generation; it posts JSON-mode chat-completion requests, parses
assistant content, and reuses the same engine-side parsing/repair contracts as the Ollama adapters.
`--host` may be either a base URL such as `https://api.openai.com/v1` or a full
`/chat/completions` endpoint.

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

The Godot menu uses the same provider factories for new runs. Its provider, host, and model fields
default from `SORCERER_PROVIDER`, `SORCERER_HOST`/provider-specific host variables, and
`SORCERER_MODEL`, so switching GUI playtests between Ollama, mock, and OpenAI-compatible endpoints
does not require a separate code path.

## Secrets

API keys must not be stored in committed files.

Local config files should be ignored by git once the repo exists. Example files can show
variable names without secrets.

The OpenAI-compatible adapter reads purpose-specific keys first
(`SORCERER_WILD_API_KEY`, `SORCERER_DIALOGUE_API_KEY`, and so on through
`SORCERER_<PURPOSE>_API_KEY`), then falls back to `SORCERER_OPENAI_API_KEY`, `SORCERER_API_KEY`,
and `OPENAI_API_KEY`. Local OpenAI-compatible endpoints that do not require auth can omit these.

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
