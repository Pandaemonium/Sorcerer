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
  interactables, persistence, pending cast coordination, actor turns

Sorcerer.Magic
  operation registry, effect validation/application, costs, transactions, audit records

Sorcerer.Llm
  live/mock providers, prompt assembly, JSON parsing, audit sinks

Sorcerer.Cli
  separate headless JSON-first console executable over GameSession, spell eval/replay harness

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
    public GameSession(
        GameState state,
        IWildMagicController? magic = null,
        IDialogueClaimExtractor? claimExtractor = null,
        IDialogueProvider? dialogueProvider = null,
        IDialogueAuditSink? dialogueAudit = null);
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
- resolve provider-backed dialogue responses and apply validated dialogue proposals
- record generated dialogue provider audits where configured
- queue post-dialogue claim extraction and apply completed proposals at explicit session
  boundaries
- flush pending dialogue extraction before saving so live provider tasks are never silently lost
- save/load authoritative state through the persistence layer
- return `ActionResult`
- expose read-only views and agent observations
- record audit/replay data where enabled

`GameSession` should not render.

Pending casts currently use a conservative seam: `begin_cast` records the cast text without
mutating state, `await_cast` resolves it through the same magic controller, and
`cancel_cast` clears it. This preserves the CLI submit/await contract while avoiding
state races before the resolver/apply split becomes more granular.

Dialogue uses the same seam. With an `IDialogueProvider`, `talk` asks the engine to prepare a
speaker-bound dialogue turn, sends compact state context to the provider, validates the generated
`spokenText`, applies accepted proposals, and returns a normal `ActionResult`. Without a provider,
the deterministic fallback path still returns player-facing text; `GameSession` then queues an
`IDialogueClaimExtractor` request from the structured dialogue delta. Completed extractor results
are proposals only; the session validates and applies them later as claim records, memories,
merchant stock, bond shifts, or promises.

Generated dialogue audits flow through `IDialogueAuditSink`. The CLI and Godot frontend currently
write JSONL records to `logs/dialogue_audit.jsonl`.

Saving is one of those explicit session boundaries. If dialogue extraction is pending, `save`
should wait for completion, apply accepted proposals or record technical failures, and then write
the snapshot. Pending provider tasks themselves are not serialized.

## Command Layer

Commands should have a typed representation. Text commands can parse into typed commands.

Examples:

```csharp
public abstract record GameCommand;
public sealed record MoveCommand(Direction Direction) : GameCommand;
public sealed record TravelCommand(Direction Direction) : GameCommand;
public sealed record AtlasCommand() : GameCommand;
public sealed record CastCommand(string Text, CastPerformance? Performance = null) : GameCommand;
public sealed record BeginCastCommand(string Text, CastPerformance? Performance = null) : GameCommand;
public sealed record AwaitCastCommand() : GameCommand;
public sealed record CancelCastCommand() : GameCommand;
public sealed record InspectCommand() : GameCommand;
public sealed record TargetCommand(GridPoint Position) : GameCommand;
public sealed record UseItemCommand(EntityId ItemOrStack) : GameCommand;
public sealed record SaveCommand(string Path) : GameCommand;
public sealed record LoadCommand(string Path) : GameCommand;
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
- current zone id, region id, and saved zone snapshots
- entities and components
- controlled entity id
- turn/time
- message log
- selected target
- RNG state or seed
- world facts, promises, and delayed events
- active background-generated canon, if any
- run status and conclusion
- all durable ledgers, saved zone snapshots, background queue/settings, and serial/RNG state

Do not split durable truth across UI-only objects.

The live lore catalog is data loaded by core services, not mutable run state. Routed lore enters
views and prompts as read-only context; if generated or discovered prose becomes durable, it is
written as `CanonRecord`s attached to an entity, place, promise, or other state subject.

## World Generation And Zones

The first procedural-world seam is `GenerationSystem`. The active tactical map still lives directly
on `GameState`, while non-active places are stored as `ZoneSnapshot`s keyed by `CurrentZoneId`.
Travel snapshots the current zone, loads or lazily generates the destination, clears coordinate
targets, places the controlled body and bond-followers at an entry edge, and advances the same turn
pump used by ordinary commands.

Tactical zones are 40 by 30 tiles. The perimeter is not a ring of wall terrain: edge tiles are
ordinary map cells, and a cardinal move that steps past the map boundary routes through the same
shared travel path as an explicit `travel <direction>` command. Diagonal off-map moves stay blocked
so corner clipping does not create ambiguous zone transitions.

After the destination loads, `NarrationSystem.ZoneEntryRumors` may add deterministic zone-entry
rumor deltas based on current legend tags and faction standing. These lines are a legibility layer:
they read existing ledgers and write messages/action deltas, but they do not change mechanics,
standing, promises, or canon.

During lazy generation, bound travel/site promises may realize as promise-site entities in the new
zone. The generation system marks the promise realized, writes canon, adds a `PromiseAnchorComponent`
to the site, and returns `promiseSite` deltas through the same travel `ActionResult`.

Zone snapshots own zone-local tactical fields as well as entities and terrain. `TileFlows`
(`createFlow` fields such as currents, conveyors, and gravity wells) are captured, loaded, cloned,
and saved with the zone so they neither leak into other coordinates in another zone nor disappear
when the player returns.

The current thin-slice world graph places the Vigovian Capital east of Hollowmere. The capital is a
normal generated zone with region affordances and ordinary entities, including Emperor Odran as an
actor tagged `emperor` / `win_condition`. Killing him is not a special spell path: ordinary validated
damage marks the actor defeated, and `GameSession` observes that state to complete the run.

## Persistence And Run Completion

Persistence is separate from both renderers and lossy view models. `GameSaveService` writes a
schema-v1 `System.Text.Json` envelope:

```json
{
  "schemaVersion": 1,
  "savedAt": "2026-06-30T00:00:00Z",
  "state": {},
  "pendingCast": null,
  "pendingCastSerial": 0
}
```

The save DTOs map authoritative state lanes explicitly and sort map-like data so save -> load ->
save can be byte-stable in tests. Saves include active and saved zones, entities/components,
body/soul state, all current ledgers, background settings/jobs, RNG/serial state, run status, run
conclusion, and pending cast information. `save` and `load` are normal `GameCommand`s, so CLI and
Godot use the same backend path.

Run completion also belongs in `GameSession`, not in renderer code. A completed run sets
`RunStatus` (`victory` or `defeat` in the current slice), writes `RunConclusion`, emits a
`runComplete` delta, creates a chronicle `CanonRecord`, and returns `ShouldQuit = true`. The
chronicle can be appended to the cross-run memorial JSONL store; later runs may surface it as inert
readable content, never as inherited power.

`RegionDefinition` supplies deterministic region identity: realm, tradition, imperial presence,
wildness, floor terrain, and `RegionAffordanceCard`s. Views expose this through `WorldCard`, and
the magic context includes the same region and affordance notes in `resolverLens` as soft guidance.
This makes regions readable to the CLI, GUI, and resolver without making renderer code own world
rules.

Generated zones also use a deterministic `TextureGrammar` for immediate fixture names,
descriptions, and subject tags. These are not bespoke story handlers: the subjects are ordinary
lore-router keys that can feed atlas text, background detail, and magic context. The current
`LoreCatalog` loads Markdown cards from `content/lore/*.md` with built-in fallbacks, and
`LoreRouter` selects access-gated cards by subjects and triggers without provider calls.

`WorldRoll` is the current seeded geopolitical layer. It derives realm status, ruler, tradition,
tags, and imperial-grip deltas from `GameState.Seed` using stable hashing rather than process hash
codes. `WorldCard`, `atlas`, and `resolverLens` expose the rolled realm profile; tactical maps still
derive lazily from regions.

The first population layer is intentionally small: generated zones create a resident entity from
the region and rolled realm profile. Residents use normal `ActorComponent`, `FactionComponent`,
`ProfileComponent`, `MemoryComponent`, `AiComponent`, and `InteractableComponent` data, so dialogue,
gifts, recruitment, hostility, and future memories use the same systems as hand-authored NPCs.

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

`GameEngine` owns deterministic rules and delegates to focused engine systems under
`src/Sorcerer.Core/Engine/Systems/`:

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

The current system split is:

- `MovementSystem`: controlled movement, bump combat entry points, forced movement.
- `CombatSystem`: attacks, damage, healing, mana restoration, defeated actor state.
- `AiSystem`: simple hostile actor turns and faction-ledger hostility checks.
- `PerceptionSystem`: line-of-sight, visible entities, soul-bound exploration memory,
  witnesses, and pending suspicion attribution.
- `ItemSystem`: pickup/drop/use/equip/focus and reagent views.
- `InteractionSystem`: read, examine, open, talk, promise realization.
- `TurnSystem`: turn advancement, expiry, scheduled events, background job pump, world
  reaction pump.
- `EngineViewBuilder`: player-facing views, debug observations, resolver context.
- `BodySwapSystem`: possession/body-control changes.
- `EffectSystem`: terrain, statuses, and spawned entities.
- `WorldReactionSystem`: deed capture, witness/visibility classification, and deterministic
  deed-to-legend/reputation application.

Renderers still call `GameSession`/`GameEngine`; they do not call these systems directly.

Character state is split across body and soul. `BodyStatsComponent(Vigor)` lives on the
entity body and drives max HP, attack, defense, and physical cost framing. `SoulLedger`
stores `SoulRecord`s keyed by `SoulComponent.SoulId`; the record owns Attunement,
Composure, current/max mana, origin/tradition, magical signature, backstory, and
first-reaction seeds. `ActorComponent` remains the runtime combat container, but it is
synced from the active body Vigor and soul mana. Possession keeps HP/Vigor/inventory/public
name with the body while moving soul stats, mana, signature, exploration memory, and
resolver potency to the new controlled body.

The first actor scheduler is intentionally simple: after a consumed player turn, hostile AI
actors either step toward the controlled body or attack if adjacent. Movement and attacks
use the same engine mutation methods as player actions. Restraint statuses such as
`bound`, `webbed`, `pinned`, `anchored`, `frozen`, `asleep`, and `petrified` suppress
hostile AI turns while active through `StatusRegistry` traits rather than hard-coded engine
string checks. Timed statuses expire during turn advancement. Movement-blocking status
traits also prevent the controlled body from walking and consume the attempted movement
turn.

Perception is player-facing but not resolver-limiting. `PerceptionSystem` computes current
visibility from the controlled body with Bresenham line-of-sight and a circular default sight radius.
Terrain in `BlockingTerrain` and entities with `PhysicalComponent.BlocksSight` block LOS;
closed doors can block sight and opening a door clears that flag. `GameState.ExploredBySoulId`
stores explored map memory by soul id, so body swap changes the eyes and location but not
the player's remembered map. `GameView` hides unexplored tiles as `unknown`, dims explored
but non-visible tiles, and includes only visible entities plus the controlled entity.
`MagicContext` may still include hidden facts for coherent wild-magic resolution, but each
entity/tile carries a relation such as `visible`, `explored`, or `hidden_from_player`.

The witness seam is also in place. `WitnessesOf(point)` returns living actors with LOS to a
point, and a minimal suspicion ledger can record witnesses who see an effect without seeing
the caster. If they gain LOS to the player within the 20-turn attribution window, the
record can become attributed to the player soul; otherwise it remains unattributed or
expires. The deed/reputation pipeline now uses this same visibility model for public,
witnessed, suspicious, mythic, and secret deed classification.

The first world-reaction pipeline is deterministic and soul-bound. Engine events record
`DeedRecord`s through `GameEngine.RecordDeed`; `TurnSystem.AdvanceTurn` applies pending
deeds through `WorldReactionSystem`; and resulting legend tags live on the actor's soul
rather than the body. Accepted wild magic, player attacks/kills, prisoner rescue, and
body swap currently feed this lane. Secret deeds do not alter public legend or standing,
suspicious effect-only deeds can raise suspicion without attribution, and witnessed/public
deeds update bounded legend tags plus multidimensional faction standing.

Faction pressure now has a first finite-resource loop. `FactionCatalog` loads starter faction
definitions from `content/factions` with fallback data: each faction has an id, display name,
role, hostile roles, standing axes, and debug-only resource pool. Deed rules target roles such
as `empire_bloc` instead of literal faction ids. Empire-bloc heat can spend patrol, warrant, and
defense resources through the turn pump; patrol responses enter `ScheduledEventLedger` and then
spawn ordinary hostile entities. Patrol responses are capped by active/pending patrol checks and
a response cooldown; quiet pump turns regenerate small pressure resources and cool heat.
`AiSystem.IsHostile` reads the faction ledger, so explicit standing such as
`player-alliance` can override default role hostility. `GameEngine.EmperorReachable()` is the
first long-arc seam and currently checks whether empire-bloc defense pools have been exhausted.

Tactical trigger state is separate from the world-reaction pump. `createTrigger` writes records to
`TriggerLedger`; `TriggerSystem` evaluates due records during `TurnSystem.AdvanceTurn`. The first
slice supports delayed and radius/filter aura-style triggers that apply one small embedded effect
(`addStatus`, `damage`, `heal`, or `message`) through existing engine methods. This keeps a ward,
an aura, and a delayed curse as templates over one mechanic rather than separate spell handlers.

Terrain reactions are also turn-pump rules, not renderer behavior. `TerrainReactionSystem` reads
actor positions plus map terrain during `TurnSystem.AdvanceTurn`: water extinguishes `burning` into
temporary `steam_mist`, `wild_fire` applies `burning`, and `vines` apply rooted `vine-snared`
status. More terrain physics should extend this deterministic lane instead of becoming spell
phrase fallbacks.

The first interactable and social slice also lives here. `pickup`, `drop`, `use`, `equip`,
`unequip`, `focus`, `unfocus`, `read`, `examine`, `open`, `talk`, `give`, `recruit`,
`bonds`, `journal`, `standing`, and `followers` are shared engine/session actions, so Godot,
CLI agents, and tests all exercise the same behavior. Minimal trade is also engine-side:
`MerchantComponent` plus `wares`, `buy`, and `sell` commands live in `ItemSystem`, with no model call
for trade intent. Service offers are explicit engine affordances too: `ServiceComponent` plus
`services` and `request` commands let dialogue reveal hush-hush help without auto-performing a
bargain. Opening the first locked cell demonstrates how a plain door entity can realize a promise,
update ledgers, and change combat allegiance without scripting a separate renderer path.

Personal bonds are separate from combat allegiance and organization membership. `BondLedger`
stores soul-keyed loyalty/fear/admiration/resentment/posture records; `ActorComponent.Faction`
controls combat allegiance; `FactionComponent` preserves membership/roles. Gifts write memories
for later dialogue, recruitment changes combat allegiance while preserving membership, and
`followers` reads bond posture rather than same-faction membership. The first deterministic
dialogue parser handles a few common organic intents; post-dialogue claim extraction handles
reported claims, promise proposals, merchant stock, and bond shifts through structured proposals
that the engine validates. The shared typed consequence applier now owns common side effects such
as memory recording, bond updates, merchant-stock changes, trade offers, service offers, door
open/unlock effects, and route creation, so dialogue, claim extraction, services, promise payoffs,
and future systems can request the same engine-authorized mutations.

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
update, inventory stays with the body that carried it, body Vigor/HP come from the
inhabited body, and soul Attunement/Composure/mana come from the player soul. This is
intentionally narrow, but it proves actions, views, and resolver operations follow the
control pointer instead of a hard-coded player object.

Temporary terrain is tracked with expiry turns. When terrain expires, the tile returns to
ordinary floor and temporary blocking is removed unless the tile is a boundary wall. This
keeps terrain spells useful without committing yet to a deep terrain simulation.

The current delayed-event implementation is deliberately small: due scheduled events are
popped during turn advancement and surfaced as log messages, with a first concrete handler for
imperial patrol pressure that spawns an ordinary hostile entity. `AdvanceTurn` also returns
turn-boundary deltas for messages created by expiry, scheduled events, or background jobs,
so player-facing `ActionResult.Messages` can announce that delayed magic, faction pressure, or
background detail actually landed. This gives spell-created future consequences an engine-owned
place to land before richer event handlers exist.

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
perception. Debug observations include raw faction resources, hostile roles, bond values, and
ledger counts including triggers;
player-facing `standing`/`bonds` use pressure, mood, rank, and posture language instead of exact
resource or relationship counts.

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
- `MagicResolutionRecord.resolvedMagicJson` stores the normalized materialized spell resolution
  where practical, so a replay does not need to call the model again.
- `--replay <path>` re-feeds transcript commands into a fresh session using `ReplaySpellProvider`;
  `--replay-assert-final` compares a compact final summary when present.
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
by default; durable output enters `CanonLedger`; routed lore can enrich the deterministic
fallback text; later `examine` calls show attached canon as known detail; and `jobs` plus
debug observations expose the queue. This proves the state, visibility, and apply boundary
before provider-backed background generation is added.

See [LLM_AND_BACKGROUND_JOBS.md](LLM_AND_BACKGROUND_JOBS.md).
