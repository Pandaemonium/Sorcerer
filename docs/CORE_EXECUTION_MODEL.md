# Core Execution Model (Architecture RFC)

Status: proposed, awaiting approval before implementation.

This RFC specifies the load-bearing core of Sorcerer: how a command (especially a
wild-magic cast) travels from player/agent intent to authoritative state change. It
turns the vision in [MAGIC_RESOLVER_ARCHITECTURE.md](MAGIC_RESOLVER_ARCHITECTURE.md)
and [ARCHITECTURE.md](ARCHITECTURE.md) into a concrete, buildable design.

It exists because the current scaffold has the right boundaries but not yet the right
internals: operations are a hardcoded `switch`, there is no transaction boundary, the
model is sent no game context, there is no seeded RNG, and casts block the turn loop.
Those are the seams to get right before content multiplies.

Code sketches below are illustrative C#, not final signatures.

## Locked Decisions (this RFC assumes these)

1. Operations are code-owned behavior plus data-driven cards. C# owns the operation set
   and each operation's validate/apply logic. Data files carry prompt cards, examples,
   aliases, and tuning. There is exactly one source of truth for behavior.
2. Long casts use a pending-cast state. The session does not block the turn loop while a
   provider thinks. This is also the seam the future casting minigame and agent-skip ride on.
3. The playtesting agent sees a player-equivalent view by default; perfect debug state is
   opt-in behind a flag.
4. A seeded RNG is threaded through the engine from the start. Replay artifacts stay
   lightweight and can come later, but the determinism seam exists now.

## Goals

- One command spine: GUI, CLI, tests, and agents all call `GameSession`.
- Adding a magic mechanic touches one operation object plus its card, nothing else.
- Malformed or rejected magic never partially mutates state.
- The model can address any visible entity, so magic is local and specific.
- The same actor-agnostic methods drive the player and AI.
- Every live cast is auditable and (given recorded resolutions) reproducible.

## Non-Goals (deferred, but not designed out)

- Engine-side spell-budget auto-pricing.
- Full promise realization across regions.
- Heavy historical rewind. We record enough to reproduce, not to scrub.
- The minigame implementation itself (only its input seam, `CastPerformance`).

## 1. The Cast Pipeline (the spine)

Every wild-magic cast follows one pipeline. Each later section specifies a stage.

```text
CastCommand(text, performance?)
  -> snapshot MagicContextView         (what the caster can perceive)
  -> [Resolving] provider.ResolveAsync (background task; turn NOT yet consumed)
  -> raw JSON
  -> parse + repair                    (shape fixes: effect->effects, nested details, aliases)
  -> normalize                         (canonicalize op names; normalize EntityRefs)
  -> [await minigame score, GUI only]  (CLI/agents use neutral performance)
  -> validate-all                      (each op.Validate; resolve refs; cost legality; treasured guard)
       -> any fatal -> REJECTION       (turn consumed, zero mutation, audit, return)
  -> open CastTransaction              (snapshots touched entities)
  -> apply-all effects                 (op.Apply -> StateDelta[])
  -> apply costs                       (mana/hp/maxhp/item/curse; treasured -> pending-confirm)
  -> commit; advance turn
  -> write audit record                (one JSONL line)
  -> ActionResult(success, consumedTurn=true, deltas, magic record)
```

The ordinary roguelike commands (move, wait, inspect, target, interact) are the same
pipeline minus the provider and minigame stages: they validate, apply inside a
transaction, and advance the turn. Wild magic is not a special path; it is the same
execution model with an LLM stage in front of validation.

## 2. Operation Model

An operation is a registered engine verb that owns its own validation and application.
The LLM composes operations; the engine owns them.

```csharp
public interface IOperation
{
    string Name { get; }                       // canonical, e.g. "damage"
    IReadOnlyList<string> Aliases { get; }      // e.g. "harm", "attack"
    OperationCard Card { get; }                 // data-loaded prompt guidance + examples

    ValidationOutcome Validate(EffectContext ctx, SpellEffect effect);
    IReadOnlyList<StateDelta> Apply(EffectContext ctx, SpellEffect effect);
}

public sealed record ValidationOutcome(bool Ok, string? RejectReason = null, bool Fatal = false);
```

- Validate is pure. It reads state and the effect, resolves references, and reports
  whether this effect is legal. It must not mutate anything.
- Apply runs only after every effect in the resolution has validated. It performs the
  mutation through the transaction and returns deltas describing what changed.

`OperationCard` is the data-driven half, loaded from `content/operations/*.json` and
attached to the code operation by name:

```jsonc
{
  "name": "damage",
  "aliases": ["harm", "attack"],
  "summary": "Injure one or more targets.",
  "promptGuidance": "Use for direct injury. Prefer specific targets from context.",
  "fields": {
    "target": "entity ref (id | selector | name), required",
    "amount": "int, 1-30",
    "damageType": "short flavor word, optional"
  },
  "examples": [
    { "spell": "blue fire at the ward-captain",
      "json": { "type": "damage", "target": "soldier_2", "amount": 6, "damageType": "arcane" } }
  ]
}
```

Resolution rules for the code/data pairing:

- Code is authoritative for the operation set and behavior. A card with no matching code
  operation is inert and logged as a content warning. A code operation with no card uses
  built-in defaults.
- This retires `content/operations/initial_operations.json` as a parallel authority. That
  file's contents become cards attached to code operations, not a second definition of the
  set.

The registry is built by explicit registration (no reflection magic, for legibility),
then enriched with cards:

```csharp
var registry = OperationRegistry.Build(
    operations: new IOperation[] { new DamageOp(), new HealOp(), new CreatePromiseOp(), ... },
    cards: OperationCardLoader.LoadFrom("content/operations"));
```

The controller becomes a thin loop with no per-operation `switch`:

```csharp
foreach (var effect in normalized.Effects)
{
    var op = registry.Resolve(effect.Type);          // canonical or alias -> IOperation, or null
    if (op is null) { rejections.Add($"unsupported operation: {effect.Type}"); continue; }
    validations.Add((op, effect, op.Validate(ctx, effect)));
}
// gate, then apply-all (section 5)
```

Adding "transform item" or "possess" later means adding one `IOperation` plus one card.
Nothing else changes.

## 3. Reference and Targeting Model

"Every object is grammar" requires that the model can address anything the caster can see,
and that the engine binds those references safely. References resolve to a set, so single
and group targeting are the same mechanism from day one.

```csharp
public sealed record EntityRef(string Kind, string Value, int? Radius = null, string? Filter = null);
// Kind: "id" | "selector" | "name"

public interface IReferenceResolver
{
    IReadOnlyList<Entity> Resolve(EffectContext ctx, EntityRef reference);
}
```

Three reference kinds, all supported:

- id: direct lookup, e.g. `"soldier_2"`. Precise. The context view gives the model stable
  ids, so this is the preferred path.
- selector: engine-computed, reliable, relative to the caster. Initial set:
  `self`, `caster`, `here`, `selected_target`, `nearest_enemy`, `nearest_ally`,
  `all_enemies`, `all_allies`, `all_in_radius` (uses `Radius`), `random_enemy` (uses RNG).
- name: fuzzy match against context-surfaced entities by name and tags, e.g. `"the brass brazier"`.
  Case-insensitive token overlap; ties broken by nearest to caster; no match or ambiguous
  match is a validation reject, not a silent no-op.

Normalization (pipeline stage) coerces the model's loose target fields into `EntityRef`s:
a bare id string becomes `{id}`; a known selector word becomes `{selector}`; anything else
becomes `{name}`. Operations never see raw strings; they call `ctx.Refs.Resolve(...)`.

Terrain stays efficient grid data (`BlockingTerrain` and future tile layers) until magic
addresses a specific feature. When a spell targets "that wall" or "the pool", the engine
promotes that tile to a real entity with the relevant components (Position, Renderable,
Physical, Fixture/MagicAnchor) so it can carry identity, transformation, and promises. This
keeps the common case cheap and the addressable case unified.

## 4. Cast Context View and Agent Perception

The provider is currently sent only the spell text and a list of operation names, so it is
blind. It must instead receive a `MagicContextView`: a compact, visibility-annotated packet.

```csharp
public sealed record MagicContextView(
    CasterView Caster,                       // id, name, hp/mp, position, statuses, soul id
    IReadOnlyList<ContextEntity> Entities,   // id, name, glyph, rel position, faction, materials, tags, hp?, visibility
    IReadOnlyList<TileNote> Terrain,         // notable tiles/fixtures, with visibility annotations
    EntityRef? SelectedTarget,
    IReadOnlyList<string> RecentEvents,      // last few message-log lines
    IReadOnlyList<PromiseCard> Promises,     // relevant promises, annotated by player visibility
    OperationIndex Operations);              // one-line index + full cards for routed ops
```

Resolver context boundary: player perception is not the resolver's hard knowledge limit. The
context may include hidden or debug-only facts when useful for coherent magic, but every entity
and tile note must say whether it is visible, explored, hidden from the player, or debug-only.
Renderer views remain player-perception bounded, and the engine remains authoritative about
references, operation legality, costs, curse limits, and transactional state. A target is not
illegal merely because it is hidden from the player. The same view-building code can feed provider
context and agent observations, but it must filter or annotate according to the consumer.

Agent perception (decision 3): `AgentObservation` defaults to the player-equivalent view so
unattended playtests surface "the player cannot tell what just happened" bugs. `--debug-state`
adds a `DebugStateView` superset: all entities, hidden promises, pending-cast internals, RNG
position, and the audit tail. The agent CLI is a playtesting tool, so debug omniscience is
allowed but not the default.

Capability-card routing starts trivial: the operation set is small, so send the full index
plus all cards. The seam (route to a subset) stays in place for when the set grows, and
routing is recall-biased: loading an extra card is cheaper than missing the one that makes a
spell work.

## 5. Transaction and Turn-Cost Semantics

Two guarantees: no partial mutation, and a crisp turn-cost contract.

Approach: validate-all-then-apply-all, with a lightweight touched-entity transaction as a
safety net against bugs.

- Validation is mutation-free and gates application. Most bad cases (unsupported op,
  impossible target, illegal cost, protected item) are caught here before anything changes.
- Application runs inside a `CastTransaction` that snapshots the component-set of any entity
  it writes. If an operation throws mid-apply (a programming error, not expected flow), the
  transaction rolls every touched entity back to its snapshot. Costs apply inside the same
  transaction, so a refused or failed cost rolls back the whole cast.

```csharp
using var tx = engine.BeginCastTransaction();
var deltas = new List<StateDelta>();
foreach (var (op, effect) in applicable) deltas.AddRange(op.Apply(ctx with { Tx = tx }, effect));
ApplyCosts(ctx, tx, resolution.Costs);   // may raise TreasuredItemPending
tx.Commit();                              // or tx.Rollback() on exception / pending refusal
```

We deliberately do not force every operation into a pure declarative-delta form yet; that
would slow content velocity for ops like summon and transform. The snapshot net gives the
no-partial-mutation guarantee without that cost. We can migrate hot operations to staged
deltas later if a concrete need appears.

Turn-cost contract (this fixes a current bug where an unsupported op is mislabeled a
technical failure and skips the turn):

| Outcome | ConsumedTurn | TechnicalFailure | Meaning |
|---|---|---|---|
| Technical failure | no | yes | Our failure: provider/transport down, JSON unparseable, an op threw. |
| Rejection | yes | no | The world refuses: model said no, or validation found it illegal/overpowered/unsupported-op/impossible. |
| Success | yes | no | Applied. |

The rule of thumb: if the JSON was well-formed and the engine simply would not allow it,
that is a rejection and costs the player the turn. If we could not get a usable answer at
all, that is a technical failure and costs nothing.

## 6. Determinism and RNG

`GameState` owns a seed and a deterministic RNG, threaded through `EffectContext` so every
random draw (damage variance, scatter, AI choices, `random_enemy`, wildness) comes from it.

```csharp
public interface IRng
{
    int NextInt(int minInclusive, int maxExclusive);
    double NextDouble();
}
```

Use a small explicit PRNG (PCG or xorshift) rather than `System.Random`, because the latter
is not guaranteed stable across runtimes and we want reproducible bug repros.

Determinism boundary: given the seed, the command sequence, and the recorded LLM
resolutions, a run reproduces. The live model is the only nondeterministic input, so replay
records each cast's resolved JSON and replays that instead of re-calling the provider.

Replay artifacts stay minimal. The current CLI transcript/replay path records this material and
re-feeds it through `ReplaySpellProvider`; the RNG seam remains important because retrofitting
determinism later means touching every call site:

```text
seed, scenario id, command log, per-cast resolved JSON, final summary
```

## 7. Pending-Cast Async Model

A cast against a local model can take seconds. The session must not freeze the turn loop,
and the turn must not be consumed until the spell actually resolves.

State machine on `GameState`:

```text
Idle --CastCommand--> Resolving --provider done + performance final--> Applying --commit--> Idle
                          |                                                 |
                          +--cancel--> Idle (no turn consumed)              +--reject--> Idle (turn consumed)
```

```csharp
public sealed class PendingCast
{
    public string SpellText;
    public int StartedTurn;
    public MagicContextView Context;            // snapshotted at submit
    public Task<SpellProviderResult> Provider;  // background call
    public CastPerformance Performance;         // accrues from minigame; Neutral for CLI
}
```

Session surface:

```csharp
ActionResult Execute(GameCommand command);   // CastCommand starts PendingCast, returns "casting", consumedTurn=false
PendingCastStatus PollPendingCast();          // GUI calls per frame: Resolving | ReadyToApply | None
ActionResult ResolvePendingCast();            // runs validate/apply pipeline, consumes turn, returns final result
```

- While Resolving, input is locked except minigame input (accrues `CastPerformance`) and
  cancel. Because nothing else acts during a cast, the snapshotted context cannot go stale.
- GUI: shows the minigame during Resolving; the score becomes `CastPerformance`; when both
  the provider result and the score are ready, it calls `ResolvePendingCast`.
- CLI and agents: use neutral performance and skip the minigame. For ergonomics the CLI
  exposes `ExecuteAsync(CastCommand)` that internally submits, awaits the provider, and
  resolves, returning one final envelope. Agents stay one-command-in, one-result-out.
- Cancel before resolution consumes no turn. This is also the natural pump point for
  background jobs (`PumpBackgroundJobs` runs at the same explicit apply boundary).

`CastPerformance` is plumbed but starts with minimal mechanical effect. It is never passed
to the provider - the minigame plays during the provider call, so the score does not exist
when the prompt is sent. The engine applies it at the apply boundary: deterministic
power/control scaling, wildness bias, and (for a severe spell on a poor performance)
escalation into a mishap. It is not an engine pricing input yet (auto-pricing is deferred).
See [CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md).

## 8. Audit Log

Every live cast writes one JSON line to `logs/wild_magic_audit.jsonl`. Audits are how we
improve the most important and most failure-prone system, so they are first-class.

```jsonc
{
  "ts": "...", "runSeed": 7, "turn": 12,
  "provider": "ollama", "model": "qwen3.5:9b", "purpose": "wild_cast",
  "spellText": "...", "performance": "neutral",
  "context": { /* compact MagicContextView */ },
  "routedCards": ["damage", "createTiles"],
  "rawResponse": "...", "repairedJson": { }, "validationErrors": [],
  "accepted": true, "technicalFailure": false,
  "effects": [ ], "costs": [ ], "deltas": [ ],
  "finalResult": { "success": true, "consumedTurn": true }
}
```

## 9. What Changes In The Current Scaffold

Concrete deltas from the present code:

- `Sorcerer.Magic`: introduce `IOperation`, `ValidationOutcome`, `OperationCard`,
  `OperationCardLoader`, and one class per operation. `OperationRegistry` maps names/aliases
  to `IOperation`. Remove the `switch` in `WildMagicController`; it becomes the gated loop.
- `Sorcerer.Magic`: add `EntityRef`, `IReferenceResolver`, and the selector set. Replace the
  inline `ResolveEntity(string)` in `GameEngine` with calls through the resolver.
- `Sorcerer.Core`: add `MagicContextView` and its builder; feed it to providers. Update
  `OllamaSpellProvider`/`MockSpellProvider` to consume context (currently they ignore it).
- `Sorcerer.Core`: add seed + `IRng` to `GameState`; thread `EffectContext`.
- `Sorcerer.Core`: add `BeginCastTransaction` / `CastTransaction` and route entity writes
  during a cast through it.
- `Sorcerer.Core`: add `PendingCast` to `GameState` and the poll/resolve session surface;
  `Execute(CastCommand)` becomes non-blocking; keep an `ExecuteAsync` convenience for CLI.
- `Sorcerer.Core`: fix the technical-failure vs rejection classification per section 5.
- `Sorcerer.Core`/`Sorcerer.Llm`: add the audit writer.
- `Sorcerer.Core`: add an actor scheduler so AI entities act through the same move/attack
  methods the player uses (the test chamber currently claims soldiers act, but they do not).
- Wire cost application with the treasured-item guard and the refuse -> consume-turn ->
  fizzle confirmation flow.



## 10. Acceptance and Test Contracts

The implementation of this RFC is correct when:

- Adding an operation requires only a new `IOperation` plus a card; no other file changes.
- A resolution containing one unsupported op rejects the whole cast, consumes a turn, and
  mutates nothing.
- A provider/transport failure consumes no turn and is flagged technical.
- An operation that throws during apply leaves state unchanged (transaction rollback).
- A cast can target a specific entity by id and by name from context.
- A treasured item cannot be consumed without explicit confirmation.
- Every live cast writes one audit line.
- The same seed plus the same recorded resolutions reproduce a run.
- The GUI stays responsive during a multi-second cast; the CLI returns one final envelope.
- The default agent observation is player-equivalent; debug state is opt-in.

## 11. Relationship To Existing Docs

This RFC refines, and where they differ supersedes, the implementation-level details in
[ARCHITECTURE.md](ARCHITECTURE.md) and [MAGIC_RESOLVER_ARCHITECTURE.md](MAGIC_RESOLVER_ARCHITECTURE.md).
The vision and scope in [GRAND_VISION.md](GRAND_VISION.md), [FIRST_PLAYABLE.md](FIRST_PLAYABLE.md),
and [NORTHSTAR.md](../NORTHSTAR.md) are unchanged. On approval, the four locked decisions
should be appended to [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md).
