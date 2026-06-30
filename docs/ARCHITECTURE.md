# Architecture

Sorcerer is a Godot 4 C# game with an authoritative, renderer-agnostic simulation core.

The Godot scene tree owns lifecycle, input, windows, renderer nodes, and developer tools.
Plain C# classes own game rules. The CLI, GUI, automated agents, and tests all drive the
same session/action backend.

## Design Summary

```text
Godot application
  scenes, windows, input maps, renderer hosts, developer overlays

Sorcerer.Core
  game state, entities, components, actions, turns, combat, items, props, world rules,
  interactables, pending cast coordination, actor turns

Sorcerer.Magic
  operation registry, effect validation/application, costs, transactions, audit records

Sorcerer.Llm
  live/mock providers, prompt assembly, JSON parsing, audit sinks

Sorcerer.Cli
  separate headless JSON-first console executable over GameSession, spell eval harness

Sorcerer.Tests
  unit, integration, CLI, and resolver-contract tests
```

Names can change as implementation starts, but the boundaries should not.

## Core Rule

Only the core/session layer mutates game state.

Input systems produce commands. Commands flow into `GameSession`. `GameSession` calls the
engine. The engine validates and mutates state. Renderers observe views.

```text
GUI input       CLI input       test input       agent input
    |              |               |                |
    +--------------+---------------+----------------+
                       |
                 GameSession
                       |
                 GameEngine
                       |
                  GameState
                       |
              read-only view builders
                       |
       GUI renderer / CLI / tests / agent observation
```

## Godot Boundary

Godot nodes should be thin hosts. Avoid putting rules inside `_Process`, `_Input`, or scene
scripts when a plain C# class can own the behavior.

The core should avoid Godot-specific types where practical. Use small project-owned value
objects for coordinates, colors, directions, ids, commands, and result records. Godot
adapters can translate those into engine inputs or renderer state.

Good Godot responsibilities:

- window lifecycle
- input mapping
- drawing ASCII glyphs
- menus and overlays
- async task polling
- developer panels
- loading/saving project settings

Bad Godot responsibilities:

- combat rules
- spell effect application
- inventory mutation
- target binding
- world simulation
- LLM result validation
- turn-cost decisions

## Session Layer

`GameSession` is the public backend object used by all interfaces.

Current shape:

```csharp
public sealed class GameSession
{
    public GameSession(GameState state, IWildMagicController? magic = null);
    public Task<ActionResult> ExecuteAsync(GameCommand command, CancellationToken cancellationToken = default);
    public GameView View();
    public AgentObservation Observation(bool debug = false);
}
```

Responsibilities:

- own `GameEngine`
- own provider stack
- route commands
- coordinate immediate and pending foreground casts
- return `ActionResult`
- expose read-only views and agent observations
- record audit/replay data where enabled

`GameSession` should not render.

Pending casts currently use a conservative seam: `begin_cast` records the cast text without
mutating state, `await_cast` resolves it through the same magic controller, and
`cancel_cast` clears it. This preserves the CLI submit/await contract while avoiding
state races before the resolver/apply split becomes more granular.

## Command Layer

Commands should have a typed representation. Text commands can parse into typed commands.

Examples:

```csharp
public abstract record GameCommand;
public sealed record MoveCommand(Direction Direction) : GameCommand;
public sealed record CastCommand(string Text, CastPerformance? Performance = null) : GameCommand;
public sealed record BeginCastCommand(string Text, CastPerformance? Performance = null) : GameCommand;
public sealed record AwaitCastCommand() : GameCommand;
public sealed record CancelCastCommand() : GameCommand;
public sealed record InspectCommand() : GameCommand;
public sealed record TargetCommand(GridPoint Position) : GameCommand;
public sealed record UseItemCommand(EntityId ItemOrStack) : GameCommand;
```

The CLI can accept both:

```text
cast bind the nearest enemy in blue glass
```

and:

```json
{"type":"cast","text":"bind the nearest enemy in blue glass"}
```

The engine should receive typed commands after parsing.

`CastPerformance` is reserved for future GUI casting minigames. The CLI should use neutral
performance by default. See [CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md).

## Action Result

Every command returns a stable `ActionResult`.

Suggested fields:

```csharp
public sealed record ActionResult(
    string CommandText,
    string Action,
    bool Success,
    bool ConsumedTurn,
    int TurnBefore,
    int TurnAfter,
    IReadOnlyList<string> Messages,
    bool TechnicalFailure,
    MagicResolutionRecord? Magic,
    DialogueRecord? Dialogue,
    IReadOnlyList<StateDelta> Deltas,
    bool ShouldQuit
);
```

Agent tools and tests should assert behavior through `ActionResult`, not by scraping
renderer state.

## Game State

`GameState` is the authoritative state of a run.

It should contain:

- map and terrain
- entities and components
- controlled entity id
- turn/time
- message log
- selected target
- RNG state or seed
- world facts, promises, and delayed events
- active background-generated canon, if any

Do not split durable truth across UI-only objects.

## Entities And Components

Everything interactable is an entity. Components describe capabilities.

Example component families:

- `Position`
- `Renderable`
- `Physical`
- `Actor`
- `Controller`
- `Inventory`
- `Item`
- `Equipment`
- `Prop`
- `Readable`
- `Interactable`
- `Faction`
- `Memory`
- `PromiseAnchor`
- `MagicAnchor`
- `StatusContainer`

This does not require a heavy ECS framework. A lightweight component dictionary or typed
component collections are enough if they preserve the unified model.

See [ENTITY_MODEL.md](ENTITY_MODEL.md).

## Engine

`GameEngine` owns deterministic rules:

- movement
- occupancy
- FOV
- combat
- item and prop interaction
- inventory/equipment
- standard spells
- turn advancement
- AI turns
- terrain reactions
- status ticks
- triggers
- promises and delayed events
- state validation

Engine methods should take actor/entity ids where practical. Avoid hard-coded player-only
paths.

The first actor scheduler is intentionally simple: after a consumed player turn, hostile AI
actors either step toward the controlled body or attack if adjacent. Movement and attacks
use the same engine mutation methods as player actions. Restraint statuses such as
`bound`, `webbed`, `frozen`, `asleep`, and `petrified` suppress hostile AI turns while
active through `StatusRegistry` traits rather than hard-coded engine string checks. Timed
statuses expire during turn advancement. Movement-blocking status traits also prevent the
controlled body from walking and consume the attempted movement turn.

The first interactable slice also lives here. `pickup`, `drop`, `use`, `equip`,
`unequip`, `focus`, `unfocus`, `read`, `examine`, `open`, `talk`, `journal`,
`standing`, and `followers` are shared engine/session actions, so Godot, CLI agents, and
tests all exercise the same behavior. Opening the first locked cell demonstrates how a
plain door entity can realize a promise, update ledgers, and change an NPC's faction
without scripting a separate renderer path.

Promise binding also lives at this layer. `GameEngine.AddPromise` writes one ledger record
with source, subject, claimed place, bound place, optional bound target, trigger hint,
realization kind, and realization location. Bound target entities receive
`PromiseAnchorComponent` ids. Ordinary verbs such as `read`, `open`, and `talk` can then
realize matching anchored promises and emit `realizePromise` deltas through the same
`ActionResult` surface used by CLI, GUI, and tests. The first realization handlers are
small but concrete: memories write to ledgers and entity memory, threats spawn a hostile
claimant, items become pickupable entities, and less concrete quest/site/omen outcomes
enter durable canon.

The first body-control seam is also implemented as shared engine behavior and as a
wild-magic operation. `possess` targets a nearby living actor: alert hostile bodies resist
and consume the turn, while an incapacitated body can be taken over. On success,
`ControlledEntityId` follows the new body, soul components swap, controllers/factions
update, and inventory stays with the body that carried it. This is intentionally narrow,
but it proves actions, views, and resolver operations follow the control pointer instead
of a hard-coded player object.

Temporary terrain is tracked with expiry turns. When terrain expires, the tile returns to
ordinary floor and temporary blocking is removed unless the tile is a boundary wall. This
keeps terrain spells useful without committing yet to a deep terrain simulation.

The current delayed-event implementation is deliberately small: due scheduled events are
popped during turn advancement and surfaced as log messages. This gives spell-created
future consequences an engine-owned place to land before richer event handlers exist.

## Magic System

Wild magic resolves through:

1. command text
2. state context view
3. LLM/mock provider
4. raw JSON
5. parser and normalizer
6. contract validator
7. engine effect applier
8. transaction commit or rollback
9. action result and audit log

Effects should be operations over engine primitives:

- damage
- heal
- move
- teleport
- apply status
- create or mutate terrain
- create entity
- transform entity
- transform item/prop
- change faction
- add trait
- write memory
- create promise
- schedule event
- create trigger
- add curse

The current implementation uses `IOperation` classes in `Sorcerer.Magic.Operations`.
`OperationRegistry` maps canonical names and aliases to operation objects. Each operation
owns validation and application, while `WildMagicController` owns provider calls,
normalization, transaction boundaries, cost application, turn semantics, and audit logging.
`createPromise` and `addCurse` can optionally pass a target plus trigger hint into the
engine promise binder, but the engine remains authoritative about whether and where the
promise actually binds.
The default registry also loads matching prompt/capability cards from `content/operations`
when available, falling back to built-in cards when content is absent.

Important contracts:

- provider/network/JSON technical failures do not consume a turn
- parseable but invalid or unsupported effects are in-world rejections and consume the turn
- accepted effects and costs apply transactionally
- protected item costs fizzle before mutation unless explicitly allowed by the resolution

See [WILD_MAGIC_CONTRACT.md](WILD_MAGIC_CONTRACT.md).

## Presentation Views

Presentation views are read-only data packets.

Important views:

- `GameView`: current player-facing state
- `MapView`: visible/explored map and entities
- `InventoryView`
- `EntityCard`
- `TileCard`
- `MagicContextView`
- `InspectionView`
- `AgentObservationView`
- `DebugStateView`
- `BackgroundJobView`

Renderers and CLI consume these views. They do not ask the engine to explain rules by
importing rule modules.

Agents may request `DebugStateView` or debug fields inside `AgentObservationView`. This is
allowed because the agent CLI is a playtesting interface, not a simulation of human
perception.

## CLI

The CLI is a first-class frontend, not a separate implementation. It should be a separate
.NET console executable that references the same core assemblies as the Godot app.

It should support:

- JSON input/output mode as the primary path
- scripted command mode
- mock provider
- live provider
- pending cast submit/await/cancel
- spell eval harness with `--eval`
- compact map output
- full agent observation output
- perfect debug state when requested

See [CLI_AND_AGENT_PLAYTESTING.md](CLI_AND_AGENT_PLAYTESTING.md).

## Replay And Debuggability

Deterministic replay is desirable but should stay lightweight at first.

Recommended minimum:

- seed
- command log
- resolved LLM JSON for each live magic/dialogue/canon action
- final summary

This is enough to reproduce many bugs without re-calling the model. Avoid building a
heavy rewind system until a concrete mechanic requires it.

Current lightweight replay/debug path:

- `--script <path>` plays newline-delimited CLI commands, ignoring blank lines and `#`
  comments.
- `--transcript <path>` writes diagnostic JSONL records for normal CLI runs:
  `transcript_start`, `transcript_step`, and `transcript_final`.
- Transcript step records include command text, `ActionResult`, and full debug
  observations. They are troubleshooting artifacts first and replay-like material where
  practical, not a full save/rewind system.

## Background Jobs

Background jobs enrich the world:

- book titles/pages
- room details
- prop detail
- lore extraction
- promise fleshing
- town/site generation

They must be:

- throttled
- configurable
- visible to developer tools
- isolated from foreground action legality
- recorded when their output becomes durable

Current implementation is a deterministic, low-resource lane rather than a live LLM
worker. `GameState` owns `BackgroundJobSettings` and `BackgroundJobQueue`; `read` and
`examine` can enqueue target-detail jobs; `AdvanceTurn` starts and applies at most one job
by default; durable output enters `CanonLedger`; and `jobs` plus debug observations expose
the queue. This proves the state and apply boundary before provider-backed background
generation is added.

See [LLM_AND_BACKGROUND_JOBS.md](LLM_AND_BACKGROUND_JOBS.md).
