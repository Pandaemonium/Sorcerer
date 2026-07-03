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

Pending casts use the resolver/apply seam. `begin_cast` records the cast text and starts
`ResolveAsync` in the background; this may call the provider and materialize JSON, but it does not
mutate game state. `await_cast` waits if needed, then sends the materialized result through
`ApplyResolved`, which re-parses and validates against the current apply point before mutating
state. `cancel_cast` signals the pending cast's provider cancellation token, clears the pending
cast, and does not consume a turn. Turn-consuming and
state-changing commands remain blocked while a cast is pending, but read-only state and ledger
commands stay available so humans and agents can inspect maps, atlas/world context, journals,
rumors, bonds, services, jobs, standing, followers, and character state during a slow provider call.
Saving while a cast is pending waits for the non-mutating resolution to finish and persists the
materialized result; loading that save can `await_cast` without calling the provider again.
Agent observations expose pending cast state (`resolving`, `ready`, `failed`, or legacy `waiting`)
plus provider/effect/error metadata when materialized. `CastAsync` remains the normal
resolve-then-apply convenience path for one-shot casts.
Accepted magic still applies atomically. During `ApplyResolved`, operation effects, post-cast
costs, and the wild-magic deed are staged in one transaction; if any child consequence returns
`worldConsequenceRejected`, the staged effects roll back, hidden rejection diagnostics are retained,
and the cast reports a non-turn-consuming technical failure.

Dialogue uses the same seam. With an `IDialogueProvider`, `talk` asks the engine to prepare a
speaker-bound dialogue turn, sends compact state context to the provider, validates the generated
`spokenText`, applies accepted proposals, and returns a normal `ActionResult`. Without a provider,
the deterministic fallback path still returns player-facing text; `GameSession` then queues an
`IDialogueClaimExtractor` request from the structured dialogue delta. Completed extractor results
are proposals only; the session validates and applies them later as claim records, memories,
merchant stock, bond shifts, or promises.
If a generated memory, bond, want, claim, or action proposal rejects at the shared consequence
boundary, the raw hidden `worldConsequenceRejected` delta is preserved and the dialogue layer adds a
hidden `dialogueProposalSkipped` breadcrumb naming the rejected proposal type. The spoken line still
stands; rejected proposal mechanics do not partially mutate state.
The `record_memory` consequence remains broad enough for abstract world memories, but dialogue
memory proposals set `requireOwnerEntity`, so NPC personal memories reject if their owner entity is
missing instead of creating orphaned ledger context.

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
The Godot command line uses the same `TextCommandParser` as CLI text mode, so human-facing
commands such as `journal`, `rumors`, `services`, `request`, `bonds`, `open`, `possess`,
and `jobs` reach the same `GameSession` routes instead of GUI-specific handlers. CLI JSON
mode uses the same typed command vocabulary for agent playtesting.
Godot also exposes a read-only Journal scene backed by `GameView.Journal`, the same shared
line builder used by the CLI `journal` command, so leads, claims, rumors, legend, and pressure
summaries do not fork into a GUI-only formatter.

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
targets, places the controlled body and bond-followers at an entry edge through hidden
`move_entity` consequences (`travelPlaceTraveler`), and advances the same turn pump used by
ordinary commands.
Traveler clones are given their destination entry position before that hidden placement
consequence runs, so a rejected placement audit cannot strand a carried body at stale coordinates
from the previous zone. Such rejections remain hidden diagnostics (`worldConsequenceRejected` plus
`travelPlacementSkipped`) rather than player-facing travel narration.
Lazy generation also writes region texture tiles through hidden `set_terrain` consequences
(`generateZoneTerrain`) in the detached destination-state sandbox. The resulting terrain map and
blocking set are what the zone snapshot saves, so generated terrain uses the same validation and
audit path as spell-made terrain instead of a raw side channel.

Tactical zones are 40 by 30 tiles. The perimeter is not a ring of wall terrain: edge tiles are
ordinary map cells, and a cardinal move that steps past the map boundary routes through the same
shared travel path as an explicit `travel <direction>` command. Diagonal off-map moves stay blocked
so corner clipping does not create ambiguous zone transitions.

After the destination loads, `WorldTurnSystem` may spend a small travel budget on audited world
moves. The first move families are `rumor_spread` (moving durable `RumorRecord`s into the current
region and local NPC carriers) and `promise_stir` (a non-mutating hint that a high-salience promise
is active). `NarrationSystem.ZoneEntryRumors` may still add deterministic zone-entry color based on
current legend tags and faction standing. Durable world-turn moves are stateful and saved; entry
narration is a legibility layer that writes messages/action deltas without changing mechanics,
standing, promises, or canon. The zone transition delta is audit-only while the visible travel
line is a typed `travelMessage` consequence. Travel writes generated and world-turn messages
through the same `StateDelta.IsPlayerVisible()` filter used by action results, so audit wrapper
deltas remain transcript/debug facts instead of leaking into the player's durable message log; any
visible generated/world-turn delta that is not already a message consequence is persisted through
`generatedDeltaMessage`. Generated rejection and skip deltas are never promoted into player
narration, even if a child applier forgot to set visibility metadata; they remain hidden audit
evidence for tests and transcripts.
Zone-entry rumor
narration is legibility-only, but it still emits through typed `message` consequences
(`zone_entry_rumor`) so circulated reputation has the same action-result, durable-log, and
transcript provenance as other player-facing consequence lines.

During lazy generation, bound travel promises may realize inside the destination zone snapshot. The
generation system marks the promise realized, writes canon, adds `PromiseAnchorComponent` ids to
concrete payoffs, and returns typed deltas through the same travel `ActionResult`. Destination-zone
payoffs apply ordinary `WorldConsequenceApplier` handlers against a detached destination-state
snapshot. Zone-local entities commit back to the generated zone map, while global ledgers such as
canon, memories, rumors, scheduled events, world flags, entity serial, and RNG commit back to the
authoritative state only after successful application. Item, person, threat, site, and route payoffs use
`spawn_item`, `spawn_entity`, `spawn_fixture`, and `create_route`; merchant and service payoffs
create the provider through `spawn_entity`, then attach the affordance through `offer_trade` or
`offer_service`. Their audit records therefore match active-zone magic/dialogue effects instead of
coming from a parallel generated-zone interpreter.
When an offered service is later performed in the active zone, the `request` command submits
`request_service`; the consequence applier commits the effect, payment, optional want completion,
and request narration together, while the command wrapper owns turn consumption.

Zone snapshots own zone-local tactical fields as well as entities and terrain. `TileFlows`
(`createFlow` fields such as currents, conveyors, and gravity wells) are captured, loaded, cloned,
and saved with the zone so they neither leak into other coordinates in another zone nor disappear
when the player returns.

The current world graph places the Vigovian Capital east of Hollowmere. The capital is a
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

The persistence test suite includes a deliberately wide flywheel-surface round trip that seeds the
state through typed consequences, then save/load/save checks claims, rumors, promises, bonds, wants,
services, triggers, persistent effects, tile flows, world flags, world turns, background jobs, and
faction pressure together. When a new durable consequence lane is added, extend that fixture rather
than relying only on incidental scenario coverage.

Run completion also belongs in `GameSession`, not in renderer code. A completed run submits
`update_run_status` (`runComplete`) to set `RunStatus` (`victory` or `defeat` in the current slice)
and `RunConclusion`, emits the visible closeout through `runCompleteMessage`, creates a chronicle
`CanonRecord`, and returns `ShouldQuit = true`. Status, closeout message, and chronicle are staged
as one packet; rejected closeout children roll the packet back with hidden `runCompleteSkipped`
rather than ending a run without its durable memorial. The
chronicle can be appended to the cross-run memorial JSONL store; later runs may surface it as inert
readable content, never as inherited power.

`RegionDefinition` supplies deterministic region identity: realm, tradition, imperial presence,
static wildness texture, floor terrain, and `RegionAffordanceCard`s. This is not a mutable
wild-magic saturation map or gradient. Views expose it through `WorldCard`, and the magic context
includes the same region and affordance notes in `resolverLens` as soft guidance.
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

The first population layer is intentionally small: generated zones create their feature fixture,
curio item, resident, resident wares, and capital emperor through ordinary `spawn_fixture`,
`spawn_item`, `spawn_entity`, and `offer_trade` consequences applied to a detached generated-zone
state. Rejected generated children return ordinary `worldConsequenceRejected` plus hidden
`generationConsequenceSkipped` audit deltas instead of throwing out of travel; later generated
children still run where they do not depend on the rejected content. The generated snapshot then
commits entity serial/RNG state back to the run. Residents use
normal `ActorComponent`, `FactionComponent`, `ProfileComponent`, `MemoryComponent`, `AiComponent`,
`MerchantComponent`, `WantComponent`, and `InteractableComponent` data, so dialogue, gifts,
recruitment, trade, hostility, and future memories use the same systems as hand-authored NPCs.
`spawn_entity` can take an explicit engine id for rare canonical actors such as Emperor Odran while
still using the same validation, profile, want, and audit path as ordinary generated residents.

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

- `MovementSystem`: controlled movement, bump combat entry points, forced movement. Ordinary
  controlled movement submits `move_entity` before the turn pump, so player, AI, dialogue, tile-flow,
  and magic movement share one mutation lane. Successful player move/wait narration emits typed
  `moveMessage`/`waitMessage` message consequences instead of session-only strings; blocked
  controlled movement uses `moveBlockedMessage` with an explicit `blockedReason` while preserving
  whether that block consumes a turn. Durable movement narration is appended by the
  `move_entity` consequence applier rather than by the low-level coordinate mutation helper.
  `move_entity` also owns controlled-movement metadata for mimic-style behavior when callers opt in
  with `recordControlledMovement`; forced movement, travel placement, tile flow, and teleport do not
  rewrite `LastControlledMoveDelta` unless they explicitly request that semantic.
- Ordinary attacks enter through the shared `damage` consequence with attack metadata. Only accepted
  damage can fire persistent combat hooks from the resulting typed delta; rejected base damage
  returns its diagnostics and stops the combat fan-out. Damage, delayed-damage release,
  resistance/weakness scaling, and defeated-actor cleanup are owned by the consequence applier.
  Delayed-damage buffers are actor-only when created; stale buffers found on non-actors at release
  time dissipate through the same release consequence so the turn pump does not repeat a hidden
  rejection forever.
- `AiSystem`: simple hostile actor turns and faction-ledger hostility checks; AI movement submits
  `move_entity` consequences so tactical movement uses the same apply path as dialogue and magic.
- `PerceptionSystem`: line-of-sight, visible entities, witnesses, and pending suspicion
  attribution. Soul-bound exploration memory is written by the shared `record_exploration`
  consequence at initialization and turn/apply points, not by renderer/view reads.
- `ItemSystem`: pickup/drop/use/equip/focus and reagent views.
- `InteractionSystem`: read, examine, open, talk, promise realization.
- `TurnSystem`: turn advancement, expiry, scheduled events, background job pump, world
  reaction pump.
- `EngineViewBuilder`: player-facing views, debug observations, resolver context.
- `BodySwapSystem`: possession/body-control changes.
- `WorldReactionSystem`: deed capture, witness/visibility classification, and deterministic
  deed-to-legend/reputation application.
- `GameSession`: command orchestration and pending-cast state. Session-control narration such as
  targeting, pending-cast begin/cancel, run-complete lines, and scenario
  setup warnings emit typed message consequences (`targetMessage`, `pendingCastMessage`,
  `runCompleteMessage`, `scenarioMessage`) instead of mutating the message
  log directly. Selected tactical targets use the typed `set_selected_target` consequence for set
  and clear operations; possession and travel also clear stale targets through the same lane.

Renderers still call `GameSession`/`GameEngine`; they do not call these systems directly.

Character state is split across body and soul. `BodyStatsComponent(Vigor)` lives on the
entity body and drives max HP, attack, defense, and physical cost framing. `SoulLedger`
stores `SoulRecord`s keyed by `SoulComponent.SoulId`; the record owns Attunement,
Composure, current/max mana, origin/tradition, magical signature, backstory, and
first-reaction seeds. `ActorComponent` remains the runtime combat container, but it is
synced from the active body Vigor and soul mana. Possession keeps HP/Vigor/inventory/public
name with the body while moving soul stats, mana, signature, exploration memory, and
resolver potency to the new controlled body through the typed `swap_souls` consequence.

The first actor scheduler is intentionally simple: after a consumed player turn, hostile AI
actors either step toward the controlled body or attack if adjacent. Movement and attacks
use the same engine mutation methods as player actions. Restraint statuses such as
`bound`, `webbed`, `pinned`, `anchored`, `frozen`, `asleep`, and `petrified` suppress
hostile AI turns while active through `StatusRegistry` traits rather than hard-coded engine
string checks. Timed statuses expire during turn advancement. Movement-blocking status
traits also prevent the controlled body from walking and consume the attempted movement
turn.
Persistent combat hooks fire as small consequence fan-outs: each embedded hook effect submits
ordinary consequences such as `damage`, `heal`, `apply_status`, or `message`; the generic
`effectType: consequence` lane can submit any typed consequence payload with `consequenceType` plus
fields. Nested payload fields win on conflicts, while top-level typed fields fill missing payload
values through the shared payload merge helper used by other delayed consequence sources. The hook collects the full child delta set, then consumes/removes its use through
`update_persistent_effect`. Each hook record stages its fired effect and lifecycle consume together,
rolling back with `persistentEffectSkipped` if the lifecycle update or child consequence rejects.
Rejected generic hook payloads also remove the bad persistent record after rollback, so combat does
not keep retrying the same malformed deferred effect. A generic hook missing `consequenceType` is
malformed content, not in-fiction failure narration, and follows this hidden rejection/removal
path. Unknown hook effect types use the same hidden rejection/removal lifecycle. Hook code must not
assume child effects produce exactly one delta, because consequence handlers may emit audit
siblings or compound results.
Stored hook visibility propagates into fired child consequences, including message effects.
The consequence applier canonicalizes common source spellings such as `addTags`,
`requestService`, or hyphenated ids to the engine's snake_case consequence type
before dispatch. That repair happens at the authoritative mutation boundary, so
dialogue, wild magic, scheduled events, triggers, persistent effects, services,
and promise payoffs do not each need their own alias table.
`GameEngine.ApplyConsequence` is the normal authoritative apply point. It uses
`WorldConsequenceGuard`, which snapshots state, delegates to
`WorldConsequenceApplier`, restores the snapshot on rejection, and validates the
resulting state before committing. If a handler reports success but leaves a
previously valid state invalid, the guard rolls the mutation back and returns a
hidden `worldConsequenceRejected` audit delta with validation issue codes. If a
handler reports `Applied=false` without already producing a rejection delta, the
guard now synthesizes the same hidden audit signal while preserving the original
error and non-rejection diagnostics. If a handler throws after beginning work,
the guard restores the snapshot and returns a hidden `worldConsequenceRejected`
with exception type and message, so malformed content cannot leave partial
mutation behind. Turn pumps and background systems can therefore key off
`worldConsequenceRejected` without allowing silent non-apply or exception-shaped
failures to age, consume, or deliver lifecycle records as if they had succeeded.

Perception is player-facing but not resolver-limiting. `PerceptionSystem` computes current
visibility from the controlled body with Bresenham line-of-sight and a circular default sight radius.
Terrain in `BlockingTerrain` and entities with `PhysicalComponent.BlocksSight` block LOS;
closed doors can block sight and opening a door clears that flag. `GameState.ExploredBySoulId`
stores explored map memory by soul id, so body swap changes the eyes and location but not
the player's remembered map. `PerceptionSystem` computes visible/explored snapshots without
mutating state; `record_exploration` owns the durable append of currently visible tiles to the
controlled soul's explored set. `GameView` hides unexplored tiles as `unknown`, dims explored
but non-visible tiles, and includes only visible entities plus the controlled entity.
`MagicContext` may still include hidden facts for coherent wild-magic resolution, but each
entity/tile carries a relation such as `visible`, `explored`, or `hidden_from_player`.

The witness seam is also in place. `WitnessesOf(point)` returns living actors with LOS to a
point, and perception only plans which witnesses would become suspicious; `record_suspicion`
owns the actual `SuspicionLedger` append for witnesses who see an effect without seeing the caster.
If they gain LOS to the player within the 20-turn attribution window, `update_suspicion`
can attribute the record to the player soul at the next turn boundary; otherwise it remains
unattributed or expires through the same typed update path. The deed/reputation pipeline
now uses this same visibility model for public, witnessed, suspicious, mythic, and secret
deed classification.

The first world-reaction pipeline is deterministic and soul-bound. Engine events submit
`record_deed` consequences through `GameEngine.RecordDeed`/`RecordDeedConsequence`; the engine and
`WorldReactionSystem` only plan/classify witnesses, visibility, and attribution, while
`WorldConsequenceApplier.ApplyRecordDeed` owns the `DeedLedger` append.
`TurnSystem.AdvanceTurn` applies pending deeds through `WorldReactionSystem`, which marks each
processed deed through `update_deed`/`deedApplied` instead of mutating applied ids directly; and
resulting legend tags live on the actor's soul
rather than the body. Accepted wild magic, player attacks/kills, prisoner rescue, and
body swap currently feed this lane. Secret deeds do not alter public legend or standing,
suspicious effect-only deeds can raise suspicion through `record_suspicion` without attribution, and witnessed/public
deeds update bounded legend tags plus multidimensional faction standing. The reaction system
still classifies deeds deterministically, but suspicion/deed capture and attribution, witness
memories (`deedWitnessMemory` as hidden `record_memory` deltas), legend tags, faction
standing/resources, and visible reaction messages (`worldReactionMessage`) now apply through the
shared typed consequence applier with deed id/kind/visibility provenance. Witness memories are
ordinary recent-memory context for later dialogue, not a separate NPC knowledge system. Autonomous pressure
spending is owned by the bounded `WorldTurnSystem`.
Each pending deed is now applied as a transaction-sized reaction plan: child consequences are
staged, `deedApplied` is submitted only after the staged plan validates, and any rejected child
consequence rolls back the whole deed reaction before emitting a hidden audit-only
`worldReactionSkipped` delta beside the original hidden `worldConsequenceRejected` diagnostics. A
failed legend, rumor, memory, faction, or message child can no longer leave a partial public
reaction behind while still marking the deed processed, and transcripts retain the exact rejected
child that caused the rollback.
Audit-style ledger deltas for deeds, memories, legend, scheduled events, trigger/background-job
lifecycle changes, persistent-effect lifecycle changes, and faction standing/resources default to
player-invisible unless explicitly emitted as visible message consequences; action results should
surface the in-fiction narration, not raw ledger bookkeeping.

Faction pressure now has a first finite-resource loop. `FactionCatalog` loads starter faction
definitions from `content/factions` with fallback data: each faction has an id, display name,
role, hostile roles, standing axes, and debug-only resource pool. Deed rules target roles such
as `empire_bloc` instead of literal faction ids. Empire-bloc heat can spend patrol, warrant, and
defense resources through budgeted `WorldTurnSystem` moves recorded as `faction_pressure` or
`faction_pressure_blocked`; patrol responses enter `ScheduledEventLedger` and then spawn ordinary
hostile entities through the shared `spawn_entity` consequence. Patrol responses are capped by active/pending patrol checks and a response
cooldown; deed-free turn pumps run quiet `WorldTurnSystem` recovery that regenerates small pressure
resources and cools heat before any budgeted faction-pressure move.
`AiSystem.IsHostile` reads the faction ledger, so explicit standing such as
`player-alliance` can override default role hostility. `GameEngine.EmperorReachable()` is the
first long-arc seam and currently checks whether empire-bloc defense pools have been exhausted.

Tactical trigger state is separate from the world-reaction pump. `createTrigger` writes records to
`TriggerLedger` through `WorldConsequenceApplier`; `TriggerSystem` evaluates due records during
`TurnSystem.AdvanceTurn`. The first
slice supports delayed and radius/filter aura-style triggers that apply either one small shorthand
effect (`addStatus`, `damage`, `heal`, or `message`) or a generic `effectType: consequence`
payload with `consequenceType` plus typed fields. Nested payload fields win on conflicts, and
top-level typed fields fill missing payload values just as they do for wild magic and dialogue.
Both paths submit through the shared typed
consequence applier and return structured deltas through the turn pump. Due trigger fires are
staged as one small transaction: the embedded effect fan-out and the `update_trigger` lifecycle
update commit together. If a generic child consequence rejects, the fire rolls back, the rejection
diagnostic and hidden audit-only `triggerSkipped` delta are returned, and the bad trigger expires
so it cannot retry the same malformed payload forever.
Missing `consequenceType` on a generic trigger payload is treated as the same hidden malformed
payload case rather than as player-facing trigger narration.
Unknown trigger effect types use that same hidden rejection/expiry lifecycle.
Stored trigger visibility propagates into fired child consequences, including description and
message effects.
Explicit stored target ids remain binding; if a named entity is gone, the trigger produces no
targeted effect rather than falling back to the controlled body. This keeps a ward, an aura, and a
delayed curse as templates over one mechanic rather than separate spell handlers.

Terrain reactions are also turn-pump rules, not renderer behavior. `TerrainReactionSystem` reads
actor positions plus map terrain during `TurnSystem.AdvanceTurn`: water extinguishes `burning` into
temporary `steam_mist`, `wild_fire` applies `burning`, and `vines` apply rooted `vine-snared`
status. These reactions apply via typed consequences (`remove_status`, `set_terrain`,
`apply_status`, and `message`) and keep their structured deltas through the turn pump. More
terrain physics should extend this deterministic lane instead of becoming spell phrase fallbacks.

The first interactable and social slice also lives here. `pickup`, `drop`, `use`, `equip`,
`unequip`, `focus`, `unfocus`, `read`, `examine`, `open`, `talk`, `give`, `recruit`,
`bonds`, `journal`, `standing`, and `followers` are shared engine/session actions, so Godot,
CLI agents, and tests all exercise the same behavior. `pickup`/`drop` cross the
world/inventory boundary through `transfer_item`, and explicit gifts plus dialogue-granted items
use the same `transfer_item` actor-to-actor `give` mode; explicit gifts stage the transfer and
gift memory together, rolling back with `giftSkipped` if either child rejects; `use` stages its
visible `useItemMessage`, heal/mana effect, and consumable `modify_inventory` spend together,
rolling back with `useItemSkipped` if any child rejects; `protect` and `unprotect`
update inventory protection metadata through `modify_inventory`; `equip`, `unequip`,
`focus`, and `unfocus` mutate `EquipmentComponent` through `update_equipment`; consumed item
actions concatenate the same `AdvanceTurn` deltas they trigger, so world-turn messages, scheduled
events, reactions, and audit records remain visible to CLI/Godot action results rather than only to
the post-action state; `read` emits the
visible text through a typed `readMessage` consequence before writing canon. Readable or
examinable entities with `ClaimSourceComponent` also surface authored claim seeds through
`record_claim`; high-salience visible seeds mint durable rumors through `record_rumor`, can
immediately bind with `create_promise`, then mark the claim bound with `update_claim`, using the
same lifecycle as dialogue extraction. Those seeded claim packets commit as one transaction:
a rejected rumor, promise, or claim-status child rolls the packet back and leaves only a hidden
`claimSeedSkipped` audit delta. `open` mutates
door state and emits visible open/unlock narration through `open_or_unlock`. Minimal trade is also engine-side:
`MerchantComponent` plus `wares`, `buy`, and `sell` commands live in `ItemSystem`, with no model call
for trade intent. Service offers are explicit engine affordances too: `ServiceComponent` plus
`services` and `request` commands let dialogue reveal hush-hush help without auto-performing a
bargain; requested services announce execution through a typed `message` consequence, pay costs
through `modify_inventory`, and perform their effect through ordinary consequences such as
`open_or_unlock`, `create_route`, or `record_memory`. If a service declares provider-want
completion metadata, the `update_want` plus its optional memory child are part of the same request
transaction. Service execution, payment, want completion, and service narration share one
transaction, so a failed authoritative effect, payment, want update, or final request narration
rolls back the staged request and leaves a hidden `serviceRequestSkipped` audit with child
rejection counts. Opening the first locked cell demonstrates how a plain door entity can
realize a promise, update ledgers, and change combat allegiance without scripting a separate
renderer path. The door mutation itself remains the `open_or_unlock` consequence; the follow-on
cell-rescue cascade is now the reusable `free_captive` consequence. It stages faction, control,
bond, want, deed, and rescue narration as one transaction after the door opens. If that cascade
rejects or fails preflight, the parent `open_or_unlock` rolls the door state back too; the result
keeps hidden child diagnostics such as `freeCaptiveSkipped` plus the parent `openOrUnlockSkipped`
audit.
Interaction fallback persistence also routes through typed `message` consequences
(`dialogueFallbackMessage`, `readFallbackMessage`, `examineFallbackMessage`, or
`openFallbackMessage`) instead of writing directly to the message log. Normal interaction paths
should prefer the narrower typed line first: `dialogueMessage`, `readMessage`,
`open_or_unlock`, queued-job messages, or promise payoff messages.

Personal bonds are separate from combat allegiance and organization membership. `BondLedger`
stores soul-keyed loyalty/fear/admiration/resentment/posture records; `ActorComponent.Faction`
controls combat allegiance; `FactionComponent` preserves membership/roles. Gifts write memories
through `record_memory` for later dialogue, with item transfer and memory recording treated as one
atomic social packet; deterministic threat dialogue stages its fear/resentment `update_bond` and
owner-required `record_memory` together, rolling back with a hidden `threatDialogueSkipped` audit if
either child rejects; deterministic prisoner dialogue and recruitment shift relationships through
`update_bond`, recruitment and prisoner rescue change combat allegiance through `change_faction`
while preserving membership, set follower AI through `update_control`, recruitment success/refusal
and prisoner-rescue narration emit typed `message` consequences, successful recruitment stages
faction/control/bond/memory/message as one bundle, and recruitment refusal stages refusal narration
plus the resentment bond update as one smaller bundle. Either recruitment path rolls back with a
hidden `recruitmentSkipped` audit if a child rejects. Memory edits that add facts express the write as a child `record_memory`
consequence, caster-forgetting memory edits express any hostility-calming loyalty floor as a child
`update_bond` consequence, and removal plus bond-floor cleanup rolls back with a hidden
`editMemorySkipped` audit if the child rejects. Bond inspection uses a neutral fallback instead of creating ledger rows,
and
`followers` reads bond posture rather than same-faction membership. The first deterministic
dialogue parser handles a few common organic intents; spoken dialogue lines emit typed
`dialogueMessage` consequences while the compact `dialogue` delta remains the exchange audit;
generated dialogue reads `WantComponent`
summaries and can submit `update_want` when the NPC's active desire is materially satisfied,
blocked, or redirected; authored notable NPCs from the opening cast through Emperor Odran carry
wants; `spawn_entity` can carry optional want fields, synthesizes a conservative
default want for notable NPCs when none is supplied, and supports `autoWant: false` for deliberate
blank actors; promise-generated people, merchants, and folk-magic service providers receive
promise-shaped wants when they are instantiated; generated dialogue also receives heard `RumorRecord` lines for the speaker
when present; generated dialogue actions can either use named local verbs or a generic
`consequence` proposal carrying `consequenceType` plus `consequencePayload`, which applies through
`GameEngine.ApplyConsequence` for local effects instead of adding another dialogue-only
mutation helper; nested payload aliases such as `world_consequence_type`, `target_id`,
`consequence_timing`, and `consequence_visibility` are accepted here just as they are for wild
magic's generic consequence operation; known consequence ids used directly as action types also
normalize into this path unless a richer named dialogue handler exists, and common top-level
action fields plus additional typed payload fields are preserved through the shared payload merge
helper when no explicit payload field wins;
non-immediate
`consequenceTiming` schedules one delayed typed consequence through
the shared turn pump; post-dialogue claim
extraction records reported claims through `record_claim`, updates their lifecycle through
`update_claim`, and handles promise proposals, merchant stock, and bond shifts through structured
proposals that the engine validates. Initial dialogue-claim intake stages the `record_claim`,
speaker `record_memory` (`claimMemory`), and first visible-claim `record_rumor` (`rumorMinted`)
inside one packet before later applications run; if an intake child rejects, staged state rolls
back and leaves a hidden `claimIntakeSkipped` audit delta. Dialogue claim-to-promise binding commits the promise
create/rebind and the `update_claim` status change together; failures roll back staged binding
state and leave a hidden `claimPromiseSkipped` audit delta. Immediate claim applications such as
bond shifts, canon facts, merchant stock, service offers, and trade offers likewise commit the
affordance plus `update_claim` status together, rolling back with `claimApplicationSkipped` if
either child rejects. High-salience visible claim notes also emit typed `message`
consequences (`claimJournalMessage` / `claimPromiseMessage`) rather than raw session-only strings, so
journal narration is visible to action results, durable logs, and transcripts through the same
delta lifecycle. High-salience
visible claims can mint durable rumors through `record_rumor`, and rumor propagation/distortion
updates those records through `update_rumor`, so later dialogue can quote rumor rather than as
guaranteed truth. Propagation spends budget only on successful new-carrier hops, decays old rumors
through the same update lifecycle, and marks rumors `stale` once their salience falls below the
active threshold. When propagation reaches a local NPC carrier, it also writes a hidden
`record_memory` child consequence so the NPC has durable heard-rumor context beyond the carrier
list. The rumor update plus any heard-memory children are staged as one propagation packet, so a
rejected or silent non-apply child rolls back carrier, hop, salience, and memory changes and leaves
hidden `rumorPropagationSkipped` audit context even when propagation is called outside the
world-turn wrapper. Local NPC carriers are selected deterministically by scoring active wants, entity/faction
tags, and profile text against the rumor tags/text, so a procedural empire rumor tends to reach an
official while a road rumor tends to reach someone worried about routes. Propagation is a bounded world-turn move; child heard-memory deltas do not spend a second
world-turn budget slot. Distortion is a background job that may use provider-backed retelling or
deterministic fallback text, then applies at the turn-pump apply point, leaving a chronological
`distortion:` history entry for later audits. Player-facing rumor cards carry status
and retelling history for heard rumors; debug observations expose full rumor cards with carrier ids
so agents can verify transport without renderer access. The shared typed
consequence applier now owns common side effects such as immediate
damage/healing/mana/movement/status/terrain/spawn/message changes, including spawned-entity
metadata for material, faction roles, controller policy, and summon status, memory recording, bond updates,
want updates, merchant-stock changes, trade offers, executed trades, service offers, inventory changes, item transfers, equipment changes, tag/trait changes,
faction allegiance, controller/AI policy changes, world flags, scheduled events, trigger creation, door open/unlock effects,
route creation, transformations, resistance/weakness changes, delayed incoming damage, status
acceleration, actor-resource changes, memory edits, persistent combat hooks, behavior tags, tile flows, claim records,
rumor records/updates, deed records, faction standing/resource adjustments, legend tags, and canon records, so magic,
dialogue, claim extraction, services, world reactions, promise payoffs, and future systems can
request the same engine-authorized mutations. Service completion metadata such as provider-want
status or tags is likewise applied through `update_want` after successful `request` commands, not
through a private service quest lane. `update_want` can also emit a hidden `record_memory` child
delta, and generated dialogue, service completion, and the opening prisoner rescue use that option
so motivation changes keep their cause as durable NPC context; if that child memory write rejects,
the want update rolls back instead of leaving a cause-less motivational mutation. `move_entity` can also validate an explicit entity swap,
which player movement uses for follower yielding instead of bespoke pathing mutation. Durable narration for coordinate movement,
terrain changes, status application, and status removal is appended by the typed consequence
applier rather than by the low-level mutation helpers, so tests and transcripts can trace the
visible line back to the operation that authorized it. Immediate damage, delayed-damage release,
healing, mana restoration, and other actor-resource changes are fully owned by the typed applier
so HP, defeated-state cleanup, soul-ledger mana, and `ActorComponent` mana stay synchronized in
one place.

Turn-start status damage and regeneration now spend the same `adjust_actor_resource` lane as
costs and other resource changes. If shared HP adjustment reaches zero, the applier performs the
ordinary defeated-actor cleanup.

Promise creation, binding, and status updates also live at this layer.
`WorldConsequenceApplier` owns `create_promise` and `update_promise`: it writes ledger
records with source, subject, claimed place, bound place, optional bound target, trigger
hint, realization kind, realization location, and claim provenance when a claim authored the
promise: source claim id, speaker id, listener soul id, and confidence. Bound target entities receive
`PromiseAnchorComponent` ids. `GameEngine.AddPromise` remains a compatibility wrapper that
submits the same `create_promise` consequence rather than mutating the ledger through an
interaction helper, and curse-shaped spell costs submit `create_promise` with a `cost:curse`
operation so stacked debts keep the same promise lifecycle and metadata as dialogue or prophecy
promises. Lower-level `PromiseSystem.Capture` is also a staged packet: `create_promise` plus its
source `record_memory` must both succeed, or the created promise rolls back before the helper
reports failure. Ordinary verbs such as `read`, `open`, and `talk` can then realize
matching anchored promises by submitting `update_promise` for the status change, then emit
`realizePromise` deltas through the same `ActionResult` surface used by CLI, GUI, and tests.
Anchored realization is budgeted and scored rather than raw promise-id order: trigger fit,
salience, bound target, anchor suitability, realization kind, and stacks decide which promises wake
first, and the audit delta records the selection score.
Selected payoffs now pass through an explicit `PromiseRealizationPlan` before mutation. The hidden
`promiseRealizationPlan` delta records the normalized handler, target, `realizedIn`, score,
selection reasons, context, and source-claim provenance; concrete payoff handlers then consume that plan and emit ordinary
typed consequences. Travel and anchored/ambient payoffs dispatch through registered archetype
handlers, and each handler owns its preflight plus concrete application path.
Plans that create concrete map affordances preflight basic capacity before they mark the promise
realized. If there is no valid placement tile, or a door-rule plan is not anchored to a door, the
system records a hidden eligibility failure and leaves the promise bound.
Registered handler application also happens before the final `update_promise` status change, but
the handler payload and status update now share one realization transaction. If the handler emits a
rejected/skipped payoff delta, or the final status update rejects, staged payoff state is restored,
visible payoff messages are removed, the promise remains bound, and hidden diagnostics include
`promiseRealizationSkipped` plus the last eligibility failure rather than falsely spending the
promise or leaving orphan payoff content.
Failed eligibility checks also stay inside the same lifecycle. Travel, ambient, and anchored
attempts submit hidden `update_promise` diagnostics with the last failure reason, context, and
turn; `PromiseCard` exposes those fields for agent/debug views, and successful realization clears
them through the status update.
Visible anchored-payoff narration is separate typed `message` consequence output
(`promiseAwakened`, `promiseItemMessage`, `promiseThreatMessage`, `promiseRouteMessage`,
`promiseCanonMessage`, or `promiseMemoryMessage`) so the legibility line that reaches the
player has the same audit/provenance shape as the mechanical payoff.
Promise payoff canon receipts are also returned as `promiseCanon` child deltas, with `canonId`
in the delta details; if that `add_canon` child rejects, the payoff rolls back like a failed
spawn, route, service, or final promise-status update.
The first realization handlers are small but concrete: memories write to ledgers and entity
memory, anchored threats, items, routes, and lore payoffs flow through `spawn_entity`,
`spawn_item`, `create_route`, and `add_canon`; destination-zone payoffs apply the same
`spawn_item`, `spawn_entity`, `spawn_fixture`, `create_route`, `offer_trade`, and
`offer_service` handlers against a detached generated-zone state before committing the staged zone
entities and any global ledger changes;
active-zone service performance then uses `request_service`;
and less concrete quest/omen outcomes enter durable canon with the same typed consequence
provenance as dialogue, services, and magic.
Compound destination-zone payoffs, such as promised merchants and folk-magic providers, stage all
child consequences in the detached generated-zone state and commit once. If a later child
consequence is rejected or skipped, the staged spawn and ledger children are discarded, the promise
remains bound, and the audit trail records the handler failure instead of leaving half-realized
provider state or partial canon behind.

`transform_entity` is the general local object/body retuning lane. Besides name, material, tags,
and description, it can change passability, sight blocking, render glyph/palette, fixture type, and
interactable verbs. Direct engine calls, wild magic, generated dialogue, delayed triggers, and
scheduled events should use this payload for local prop changes such as bridge collapse or shrine
repair instead of growing one-off handlers.

The first body-control seam is also implemented as shared engine behavior and as a
wild-magic operation. `possess` targets a nearby living actor: alert hostile bodies resist
and consume the turn, while an incapacitated body can be taken over. On success,
`ControlledEntityId` follows the new body through `set_controlled_entity`, soul components swap through `swap_souls`, controllers/factions
update through `update_control` and `change_faction`, inventory stays with the body that carried it, body Vigor/HP come from the
inhabited body, soul Attunement/Composure/mana come from the player soul, and the old/new
body aftershock statuses apply through `apply_status`; visible possession narration emits a typed
`message` consequence, and hostile-body resistance narration uses the same typed `message` lane.
The successful possession packet stages soul swap, controller/faction changes, statuses,
controlled-entity change, target clear, deed, and narration together; any child rejection rolls the
packet back with a hidden `possessionSkipped` audit.
This is
intentionally narrow, but it proves actions, views, and resolver operations follow the
control pointer instead of a hard-coded player object.

Temporary terrain is tracked with expiry turns. When terrain expires, the tile returns to
ordinary floor and temporary blocking is removed unless the tile is a boundary wall. This
keeps terrain spells useful without committing yet to a deep terrain simulation. Tile-flow
fields move affected actors through the same `move_entity` consequence used by ordinary
movement, AI, dialogue, and forced magic movement, while keeping flow-specific narration.

Due scheduled events are turn-pump delivery wrappers for the shared consequence grammar. A simple
event with `text` or `description` still surfaces through a typed `message` consequence, but an
event payload with `consequenceType` can now deliver one ordinary typed consequence such as
`add_tags`, `spawn_entity`, `set_world_flag`, or `modify_inventory`. Imperial patrol pressure is a
named scheduled-event kind that uses the same due-event lifecycle while spawning an ordinary hostile
entity. Nested payload fields win on conflicts, while top-level typed event payload fields fill
missing payload values through the same merge helper used by trigger and persistent-effect
consequences. Payload application is staged before the event lifecycle update; if the payload or lifecycle
update rejects, staged state is restored, the hidden `worldConsequenceRejected` diagnostic is
preserved beside a hidden audit-only `scheduledEventSkipped` delta with the failed scheduled
`consequenceType`, and the event is expired instead of leaving a partial delayed effect behind.
If a scheduled payload explicitly claims to be a generic consequence (`effectType`, `type`,
`operation`, or `op` is a consequence alias, or a `consequencePayload` is present) but omits
`consequenceType`, it is malformed content and follows that same hidden rejection/expiry path
instead of falling back to player-facing delayed-message text. Stored scheduled-message visibility
also propagates into the due `message` consequence, so `playerVisible: false` remains audit-only
when the event fires.
`AdvanceTurn` also returns turn-boundary deltas for structured work performed during the pump:
status ticks, delayed damage release, tile flows, trigger fires, scheduled payloads/patrol spawns,
world-turn moves, and background job canon/messages. Message-only work from expiry, simple
scheduled events, terrain reactions, or world reactions is surfaced as generic `turnEvent` deltas
only when it has not already produced a structured delta; typed work keeps its original operation
and `consequenceType`. This gives spell-created future consequences an engine-owned place to land
without a bespoke delayed-effect interpreter.

The turn pump now also runs the bounded `WorldTurnSystem`. It records at most a small budget of
world initiative moves per pump in `WorldTurnLedger` through `record_world_turn`; current moves include faction pressure,
resource-shortage pressure memos, quiet `faction_recovery`, rumor spread through `update_rumor`,
promise stirring, and private `want_stir` moves that let a high-salience active NPC want write one hidden
`record_memory` child delta. These records are
debug/audit facts, not a hidden second simulator. Faction pressure decisions are budgeted
world-turn moves, and their resulting schedule/resource/canon/message changes apply through the
shared typed consequence applier and return their own typed deltas alongside the wrapper
`worldTurn` delta. Compound world-turn moves are snapshot transactions: faction pressure,
resource recovery, rumor spreading, promise stirring, want-stir memories, messages, and audit
records either all land or the state is restored and only rejection diagnostics remain. The
transaction helper treats any child `worldConsequenceRejected` delta as failure even if a move
family forgets to return false, so partial world-turn effects cannot leak through the bounded
pump. The wrapper delta is marked audit-only/player-invisible so CLI and GUI
messages show the concrete in-fiction consequence once while debug state, transcripts, and tests
can still inspect the bounded world-turn move that chose it. Quiet faction-pressure recovery also lives here as maintenance:
it runs only on deed-free turn pumps, before initiative budget is spent, uses the same typed
resource consequences, and writes a hidden `faction_recovery` world-turn audit only when a resource
actually changes. Debug observations expose recent world-turn audit cards with their kind,
source, summary, and details so agent playtests can inspect bounded world activity without scraping
player-facing prose. Want-stir memories are private context for later dialogue and claim extraction;
they do not expose an NPC's want to the journal or message log by themselves.

Engine-owned world-turn, travel, world-reaction, rumor, and background-job flows should use
`GameEngine.ApplyConsequence` or an injected consequence sink as their apply point. A local
`WorldConsequenceApplier` should be wrapped by `WorldConsequenceGuard` for isolated tests,
standalone state helpers, and deliberately detached generated-zone sandboxes, and should not be
used raw in ordinary engine runtime paths.

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
`GameTransaction` snapshots the same expanded state surface that the flywheel relies on:
entities, zones, terrain, tile flows, explored cells, messages, claims, rumors, promises,
world-turns, deeds, factions, legend, canon, bonds, scheduled events, triggers, suspicions,
persistent effects, background jobs/settings, world flags, RNG/serial state, selected target,
and the last controlled movement delta. A rejected or failed spell must restore all of those
lanes, not only map/entity state.
When accepted spells or intentional magical rejections advance the turn, their action-result
messages use `PlayerMessages()` for both effect/cost deltas and turn-pump deltas so hidden audit
records stay out of player-facing cast narration. Accepted outcome text, no-op fallback sparks,
technical failure notices, and intentional rejection reasons are emitted as typed `message`
consequences (`wildMagicOutcome`, `wildMagicFallback`, `wildMagicTechnicalFailure`, and
`wildMagicRejected`) rather than direct session log writes.
High-use operations increasingly apply by submitting `WorldConsequence` records to
`GameEngine.ApplyConsequence`; this is already true for tactical effects plus inventory,
tags/traits, faction allegiance, world flags, scheduled event creation/lifecycle, trigger creation/lifecycle,
transformations, resistance/weakness, delayed incoming damage, memory edits, persistent effects,
behavior tags, tile flows, status acceleration, faction standing/resource adjustments, legend tags,
and canon records.
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
- `GameView.Journal`: mixed player-facing journal lines for leads, promises, claims, rumors,
  legend, and pressure, shared by CLI `journal` and the Godot Journal scene
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
perception. Debug observations include raw faction resources, hostile roles, bond values, recent
world-turn audit cards, and ledger counts including triggers;
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
- `--episode-log <path>` uses the parallel `episode_start`, `episode_step`, and
  `episode_final` records for unattended runs; step records now include the same `ActionResult`
  materialization needed by replay providers.
- `MagicResolutionRecord.resolvedMagicJson` stores the normalized materialized spell resolution
  where practical, so a replay does not need to call the model again. The lower-level
  `MaterializedMagicResolution` seam also lets a spell be resolved without mutation and applied
  later, including after save/load, as long as `ApplyResolved` revalidates it against current state.
- Generated dialogue action results store a normalized `DialogueResolutionRecord`, and completed
  post-dialogue extractor work stores `DialogueClaimExtractionRecord`s. Replay feeds these through
  `ReplayDialogueProvider` and `ReplayDialogueClaimExtractor`, preserving the ordinary dialogue
  consequence path without live provider calls. Provider-reported technical failures and task-level
  extractor faults/cancellations are materialized as failed extraction records, so replay preserves
  the attempted background dialogue work instead of silently dropping it.
- Provider-backed background text is harvested from transcript result/debug background job records
  and fed through a replay background text generator keyed by job id. The final replay summary
  includes a compact background result-text fingerprint so missing materialized prose cannot drift
  silently into deterministic fallback text.
- Start records store quickstart metadata as well as seed/origin/background settings when
  available, so replay can rebuild the same initial social smoke setup before commands run.
- `--replay <path>` re-feeds transcript or episode-log commands into a fresh session using replay providers;
  `--replay-assert-final` compares a compact final summary when present, including core social
  ledger counts such as promises, claims, rumors, world turns, bonds, memories, and background job
  result text.
- Replayable step records include command text, `ActionResult`, and full debug
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

Current implementation is a low-resource lane with deterministic fallback and optional
provider-backed candidate prose. `GameState` owns `BackgroundJobSettings` and `BackgroundJobQueue`; `read` and
`examine` enqueue detail jobs through the shared `queue_background_job` consequence, and
high-hop rumors enqueue `rumor_distortion` jobs through the same consequence with a `rumor` target
kind. The consequence owns disabled/max-queue/duplicate/canon guards where relevant and returns
typed action-result deltas; queue visibility belongs to `jobs` and debug observations rather than
ordinary player narration.
`AdvanceTurn` starts at most one job by default, records `Running`/`Completed` through
`update_background_job`, asks the optional background text generator for candidate prose when one
is configured, emits hidden `backgroundTextGenerated` audit deltas for provider/fallback status,
and treats completed output as candidate text until the turn pump applies the purpose's typed
consequence. A saved or otherwise resumed `Completed` job is applied before new queued work starts;
a stale `Running` job is marked `Failed` with `stale_running_job` so it does not block duplicate
guards forever after an interrupted worker. Durable entity output enters
`CanonLedger` through `add_canon`; rumor output applies through `update_rumor`; `Applied` is
recorded only after that consequence, the job lifecycle update, and the hidden `record_world_turn`
receipt (`background_detail_applied` or `background_rumor_distortion`) commit. Rejected apply
consequences restore the staged state, emit a hidden audit-only `backgroundJobApplySkipped` delta,
and mark the job `Failed`; obsolete detail jobs whose canon already exists fail with
`canon_already_exists` instead of duplicating ledger entries. Queue skips such as duplicate active
jobs likewise return hidden audit-only typed deltas.
Routed lore can enrich deterministic fallback text; later `examine` calls show attached canon as
known detail; and `jobs` plus debug observations expose the queue. The provider may narrate a
candidate, but state only changes through the same apply-point consequences.

See [LLM_AND_BACKGROUND_JOBS.md](LLM_AND_BACKGROUND_JOBS.md).
