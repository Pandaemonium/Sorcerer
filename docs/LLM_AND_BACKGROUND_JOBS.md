# LLM And Background Jobs

Sorcerer uses LLMs for interpretation and world enrichment. The engine remains
authoritative.

Provider details should stay isolated so users can swap local models or API-key providers.
Ollama can be supported, but it should not be the only real path.

## Provider Purposes

Each model call should have a purpose:

- `wild`: resolve typed wild-magic spells
- `dialogue`: parse organic player speech into structured dialogue intent/proposals and voice NPC
  responses; also run post-dialogue claim routing/structuring by default so local-model users do
  not pay model-load thrash for a separate promise router
- `item`: identify/adapt item abilities
- `canon`: on-demand examine/read materialization
- `background`: non-urgent world enrichment
- `agent`: autonomous playtesting command chooser, if used

Purposes allow separate models, hosts, timeouts, and concurrency limits.

## Foreground Calls

Foreground calls block player action:

- wild spell resolution
- dialogue interpretation/response
- item identification
- on-demand reading/examination

Foreground calls should:

- have clear resolving UI
- be cancellable only if cancellation leaves state unchanged
- write audit records
- fail technically without consuming turns
- never directly mutate state

## Background Calls

Background calls enrich the world:

- book titles
- book pages
- room descriptions
- far-look object details
- lore extraction
- promise fleshing
- post-dialogue claim extraction, when it is not part of the foreground dialogue call
- rumor distortion after high-salience stories have travelled far enough to plausibly change
- town/site details

Background calls must be resource-aware.

Background generation should default low.

Required controls:

- enable/disable background generation
- maximum concurrent background jobs
- maximum queued jobs
- per-purpose model/host settings
- visible developer queue
- logging/audits
- priority for nearby/relevant content

If a background job completes, its output should become durable only at an explicit apply
point controlled by `GameSession`, not from an arbitrary worker thread mutating state.

Implemented background jobs follow that rule now: `entity_detail`/`canon_detail` settle as canon
only when the turn pump accepts an `add_canon` consequence, while `rumor_distortion` retells a
high-hop rumor and applies the changed text, `distorted` tag, and `distortion:` history through the
shared `update_rumor` consequence. `Completed` means a worker produced candidate text; `Applied`
means the text survived the authoritative consequence lifecycle. Generic detail/canon applies now
also leave a hidden `record_world_turn` receipt (`background_detail_applied`), so background
enrichment has the same bounded audit trail as rumor distortion. Candidate text can come from a
configured background provider or from the deterministic fallback; either way, durability still
waits for the same typed consequence apply point.

## Post-Dialogue Claim Extraction

Dialogue should return to the player before expensive promise work runs. A conversation may queue
a low-latency claim extraction flow:

1. The player-facing dialogue result is shown immediately.
2. A small router call, using the already-loaded `dialogue` model, receives the recent exchange,
   speaker ids/cards, and a compact definition of claim/promise.
3. If the router finds a plausible claim, a larger structuring call uses selected claim capability
   cards and strict JSON to propose memories, canon notes, merchant-stock hints, or promises.
4. The engine validates and applies the structured proposal between turns or at another explicit
   session pump point.
5. A save command is an explicit synchronization point: the session waits for pending dialogue
   extraction to complete, applies accepted proposals or records technical failures, and only then
   writes the save.

This is intentionally a model interpretation lane, not a simulation lane. The model may say
"this dialogue probably claimed that Jimmer sells a blade"; the engine decides whether that becomes
a reported claim, an NPC memory, merchant stock, a future item promise, or nothing.

Do not bind player-spoken claims as world truth. Ordinary player dialogue can ask, lie, speculate,
or manipulate, but only validated engine actions, wild magic, NPC/document claims, or authored
world sources create durable commitments.

Current implementation uses both `IDialogueProvider` and `IDialogueClaimExtractor` in
`Sorcerer.Core`, with mock, Ollama, and OpenAI-compatible implementations in `Sorcerer.Llm`.
Provider-generated dialogue returns `spokenText` plus structured proposals and applies accepted
claims immediately.
Fallback deterministic dialogue can still queue extraction after successful `talk` results and pump
completed results on a later command. Proposals can record claims through `record_claim`, write
memories, add existing merchant stock, bind promises, or request bounded bond deltas. Gift commands
only create gift memories; if that gift changes a relationship, generated dialogue or claim
extraction must propose the bond change during a later conversation.

Current generated dialogue supports validated action proposals such as
`step_aside`, `flee`, `call_help`, `give`, `open`, `attack`, `recruit`,
`create_promise`, `offer_trade`, `reveal_service`, `mark_location`, and
`spawn_fixture`. Unsupported actions are rejected with a diagnostic delta.

## Ollama Concerns

Ollama is easy for some users, but resource control can be coarse.

Mitigations:

- expose background generation toggles
- default background concurrency low
- allow separate foreground/background Ollama hosts
- allow smaller background model
- avoid model thrash by documenting model and context settings
- never require background jobs for critical path gameplay

If users want stricter resource control later, the provider interface should be narrow
enough to add another backend.

See [PROVIDERS_AND_CONFIGURATION.md](PROVIDERS_AND_CONFIGURATION.md).

## Provider Interface

Suggested shape:

```csharp
public interface ILlmProvider<TRequest, TResponse>
{
    string Name { get; }
    Task<LlmCallResult<TResponse>> ResolveAsync(TRequest request, CancellationToken ct);
}
```

Provider modules should not know how to mutate game state. They return text or structured
records. Session/engine code validates and applies.

## Audit Logs

Every live model call should record:

- timestamp
- purpose
- provider
- model
- prompt messages or compact request
- state context
- raw response
- parsed response
- validation errors
- technical failure flag
- audit schema version

Audits are developer tools. They are also essential for improving prompts and parsing.

## Mock Providers

Mock providers should exist for:

- fast tests
- agent playtesting
- deterministic command scripts
- offline development

Mock provider output should follow the same contract as real provider output. Do not
create a second hidden engine in mocks.

## Background Job Queue

Current implementation:

- `GameState` owns `BackgroundJobSettings` and `BackgroundJobQueue`.
- Background generation defaults on but low: one job per turn, with a small queue cap.
- CLI playtests can disable or throttle this lane with `--disable-background`,
  `--max-background-jobs`, and `--background-jobs-per-turn`.
- `read` and `examine` enqueue entity-detail jobs through the shared
  `queue_background_job` consequence. High-hop, high-salience rumors enqueue
  `rumor_distortion` jobs through the same consequence using a `rumor` target kind. The
  consequence enforces disabled/max-queue/duplicate/canon guards where relevant and returns a typed
  action-result delta. Queue visibility belongs to `jobs` and debug observations, not ordinary
  player-facing action narration.
- `AdvanceTurn` is the explicit apply point: it starts at most the configured number of
  queued jobs, records `Running`/`Completed`/`Applied`/`Failed` transitions through
  `update_background_job`, asks the optional `background` provider for candidate prose when one is
  configured, falls back to deterministic routed-lore text on provider failure or absence, and then
  applies candidate output through the narrow typed consequence for that job purpose.
  `entity_detail`/`canon_detail` jobs become durable through `add_canon`; `rumor_distortion`
  jobs become durable through `update_rumor`. Successful applies stage the job output, applied job
  state, and a hidden `record_world_turn` audit together, so a rejected child restores state rather
  than leaving half-applied enrichment.
- Save/replay is explicit for intermediate states: a `Completed` job that has result text but has
  not applied yet is picked up at the next turn pump before new queued work, while a stale
  `Running` job is marked `Failed` with `stale_running_job` so it cannot block duplicate guards
  after an interrupted worker.
- Apply is transactional. If the typed consequence rejects, the staged state is restored, a hidden
  audit-only `backgroundJobApplySkipped` delta is emitted, and the job is marked `Failed` instead
  of partially applying. Obsolete detail jobs whose canon was already supplied fail with
  `canon_already_exists` rather than duplicating ledger entries. Queue skips such as duplicate
  active jobs return hidden audit-only typed deltas rather than player-facing narration.
- Subsequent `examine` calls show attached canon as known detail, so background enrichment is
  visible through the shared CLI/Godot command path.
- `jobs` exposes the queue through the shared CLI/Godot command path.
- Provider materialization emits hidden `backgroundTextGenerated` audit deltas that record provider,
  model, technical failure, and deterministic fallback use without adding player-facing narration.
  CLI/Godot live runs also write provider prompts, raw responses, parsed text, errors, and timing to
  `logs/background_audit.jsonl`.
- Debug observations include background job cards so diagnostic episode transcripts can
  capture the queue without scraping text.

`LlmConfiguration` separates purpose settings (`wild`, `dialogue`, `item`, `canon`,
`background`, `agent`). The CLI uses the `wild` purpose for foreground spell resolution and can
wire a separate `background` text generator for non-critical enrichment. Passing
`--background-provider`, `--background-host`, or `--background-model` opts into that provider unless
`--disable-background` is also supplied; otherwise the turn pump uses deterministic fallback text.
Purpose-specific environment variables use names such as `SORCERER_WILD_PROVIDER`, `SORCERER_WILD_MODEL`,
`SORCERER_BACKGROUND_PROVIDER`, `SORCERER_BACKGROUND_MODEL`, and
`SORCERER_BACKGROUND_ENABLED`. OpenAI-compatible endpoints can also use purpose-specific
`SORCERER_<PURPOSE>_API_KEY` values before falling back to shared OpenAI-compatible key variables.

Current job record:

```csharp
public sealed record BackgroundJob(
    string Id,
    string Purpose,
    string TargetEntityId,
    int Priority,
    BackgroundJobState State,
    int CreatedTurn,
    DateTimeOffset CreatedAt,
    int? StartedTurn,
    int? CompletedTurn,
    int? AppliedTurn,
    string? ResultText,
    string? Error
);
```

Queue should expose a read-only developer view:

- running jobs
- queued jobs
- completed waiting-to-apply jobs
- failed jobs
- current provider/model, when a provider-backed materializer is configured
- elapsed time

The GUI can render this as a hidden developer panel. The CLI already exposes it with
`jobs`.
