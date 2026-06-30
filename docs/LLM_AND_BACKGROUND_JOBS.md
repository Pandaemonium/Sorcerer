# LLM And Background Jobs

Sorcerer uses LLMs for interpretation and world enrichment. The engine remains
authoritative.

Provider details should stay isolated so users can swap local models or API-key providers.
Ollama can be supported, but it should not be the only real path.

## Provider Purposes

Each model call should have a purpose:

- `wild`: resolve typed wild-magic spells
- `dialogue`: NPC conversation
- `item`: identify/adapt item abilities
- `canon`: on-demand examine/read materialization
- `background`: non-urgent world enrichment
- `agent`: autonomous playtesting command chooser, if used

Purposes allow separate models, hosts, timeouts, and concurrency limits.

## Foreground Calls

Foreground calls block player action:

- wild spell resolution
- dialogue response
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
- `read` and `examine` can enqueue deterministic background detail jobs for the target.
- `AdvanceTurn` is the explicit apply point: it starts at most the configured number of
  queued jobs, produces deterministic placeholder text, writes durable canon, and marks
  the job `Applied`.
- `jobs` exposes the queue through the shared CLI/Godot command path.
- Debug observations include background job cards so diagnostic episode transcripts can
  capture the queue without scraping text.

This is intentionally not a real background LLM worker yet. It proves the state,
throttling, visibility, and apply-boundary architecture before any provider call can
consume user resources.

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
- current provider/model, once real background providers are wired in
- elapsed time

The GUI can render this as a hidden developer panel. The CLI already exposes it with
`jobs`.
