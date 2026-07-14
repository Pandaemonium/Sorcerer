# Implementation Plan — One Machine, Many Stories

Status: active roadmap-to-code handoff, rewritten 2026-07-13 after the prototype foundation and
the product roadmap were established. This is the plan to give an implementation agent.

[ROADMAP_TO_1_0.md](ROADMAP_TO_1_0.md) owns product order and release gates.
[GRAND_VISION.md](GRAND_VISION.md) owns intent and always wins on what the game should be.
[SYSTEMS_VISION.md](SYSTEMS_VISION.md) owns the interlocking simulation model. [ARCHITECTURE.md](ARCHITECTURE.md)
and [AGENTS.md](../AGENTS.md) own the technical boundaries. The earlier
[BUILD_PLAN.md](BUILD_PLAN.md) is now a useful record of how the existing foundation was built;
it is not the queue for the next product phase. This document replaces the prototype-era version
of IMPLEMENTATION_PLAN as the active execution order.

## Mission for the implementer

Build the smallest coherent machine that produces the greatest number of interesting decisions
and stories. Do not implement the roadmap as a collection of features. Make every addition read
from, write to, or visibly return through systems that already exist.

> **Maximum gameplay per concept. Maximum legibility per consequence. Minimum private machinery.**

The danger is not that Sorcerer lacks systems. The engine already represents most of the vision.
The danger is that each new idea grows a private state lane, helper, command, prompt path, or UI
readout until the code contains many correct pieces that do not make one good game. This plan is
organized to prevent that.

Do not attempt this entire plan in one change. Execute one numbered work package at a time. Each
package ends in a playable build, focused tests, a CLI episode, a Godot check, and a short evidence
note. Stop at its exit gate before beginning the next.

## The product compass

Every implementation decision serves the same player rhythm:

> **Notice something specific → choose or author an action → change authoritative state → see who
> noticed and why → meet a later answer → use that answer as new material.**

The finished game is fun because this rhythm works at four nested scales:

- **Turn:** threats and affordances are readable; ordinary actions are immediate.
- **Situation:** combat, evasion, bargaining, theft, rescue, or transformation supports several
  routes and one tempting authored-magic moment.
- **Journey:** a person, object, rumor, promise, debt, or deed travels between places and returns
  changed.
- **Run:** escape becomes foothold, foothold becomes war, war opens the capital, and the chronicle
  can explain the real route without inventing connective tissue.

Correctness is necessary but not sufficient. A change that adds a valid state lane without making
one of these scales more interesting, more responsive, or more legible is not product progress.

## The one machine

All gameplay rides four shared spines. New work should extend these, not grow beside them.

### 1. Command spine — who may ask

```text
Godot / CLI / replay / test / agent
              ↓
         GameCommand
              ↓
         GameSession
              ↓
      engine/system resolver
```

- `GameSession.ExecuteAsync` is the public action surface.
- Text and GUI input become the same typed command.
- Commands decide intent, eligibility, and turn ownership. They do not own private mutations.
- Actor-agnostic systems receive actor/entity ids. A player-only mutation path is a defect.

### 2. Consequence spine — what becomes true

```text
engine rule / magic / dialogue / item / service / promise / faction / world turn
                                  ↓
                         WorldConsequence packet
                                  ↓
                    GameEngine.ApplyConsequence
                                  ↓
                 normalize → guard → validate → apply
                                  ↓
                         StateDelta + audit
```

- `GameEngine.ApplyConsequence` remains the ordinary shared apply point.
- The source of a change does not determine its mechanics. Damage is damage; opening a door is
  opening a door; memory is memory, regardless of whether magic, dialogue, a service, or a faction
  requested it.
- Compound outcomes are ordered packets of existing consequences under one transaction. A
  player-visible event such as freeing a captive may be bundled, but its children remain ordinary
  faction, control, bond, want, deed, memory, and message consequences.
- A materialized provider result is evidence, not authority. It is parsed and validated again at
  the current apply point.

### 3. Time spine — when the world may move

```text
accepted action
    ↓
tactical turn tick → actor initiative → scheduled/triggered effects
    ↓
explicit pump point → bounded WorldTurn moves
```

- Immediate tactical effects use the turn system.
- Rumors, faction expenditure, promise stirring, and NPC initiative use the bounded world-turn
  budget at explicit pump points.
- Deferred timing is an explicit consequence field. It is never inferred from which subsystem
  produced the effect.
- Background workers may materialize candidate text. They never mutate the run.

### 4. Knowledge and legibility spine — how anyone knows

```text
GameState + StateDelta provenance
              ↓
        read-only views
       ↙       ↓       ↘
    Godot     CLI    resolver/narrator context
```

- Renderers consume views and deltas, never rule internals.
- Player views show actionable truth and in-fiction causes. Debug observations show exact state,
  ids, budgets, and audit trails.
- The resolver receives compact, relevance-routed cards and explicit visibility relations.
- Presentation may animate a delta; it may not invent the delta.

## One source of truth per concern

| Concern | Owner | Never duplicate in |
|---|---|---|
| Run state and identity | `GameState`, unified entities/components, existing ledgers | renderer nodes, provider objects, free-text flags |
| Mutation legality and behavior | consequence handlers behind `GameEngine.ApplyConsequence` | dialogue helpers, promise realizers, GUI code, mock fallbacks |
| Command/turn contract | `GameSession`, engine action result | CLI-only wrappers, Godot callbacks |
| Tactical time | `TurnSystem` and actor systems | status-specific timers, renderer `_Process` |
| World initiative | bounded `WorldTurnSystem` | per-faction background loops, NPC schedules |
| Geography | `WorldPlaceGraph`, region/content catalogs, authoritative zone snapshots | dialogue prose, quest destinations, renderer maps |
| Player-visible state | `GameView` and shared subviews | separately formatted GUI state or CLI-only calculations |
| Mechanical content | C# consequence/operation behavior plus data-authored templates | prompt prose or region-specific code |
| Semantic interpretation | provider proposals routed through validation | engine truth, critical progression checks |

## Complexity budget

### The representation ladder

For every requested feature, try these in order and stop at the first sufficient rung:

1. **New arrangement of existing content and rules.** Change data, placement, encounter
   composition, tuning, or return cadence.
2. **New packet of existing consequences.** Compose verbs the engine already owns.
3. **New reader of existing state.** Make a want, rumor, trait, deed, route, or relationship affect
   a decision that currently ignores it.
4. **Small extension of an existing component, record, view, or consequence family.** Add the
   minimum field needed by several sources and consumers.
5. **New consequence type.** Only when no existing narrow verb can express the mutation and the
   type will be useful to magic, content, dialogue, AI, promises, and/or factions.
6. **New durable state lane or subsystem.** Last resort. It must unlock a general category of play
   and name at least three readers, two writers, a player-facing surface, persistence behavior,
   replay behavior, and a removal plan if the experiment fails.

Most roadmap work should stop at rungs 1–3.

### Feature admission test

Before code, write a short slice contract answering:

1. Which player decision becomes newly interesting?
2. Which stage of **Act → Witness → Record → Circulate → Respond → Deliver** improves?
3. Which existing state does it read?
4. Which existing consequences does it submit?
5. Who or what carries the result back to the player?
6. How does the player answer or exploit it?
7. How do GUI, CLI, replay, save/load, and provider-off play reach it?
8. What old branch, helper, flag, or duplicate message path is deleted?

Reject or redesign a feature that cannot answer at least questions 1–6 concretely.

### Rules against both kinds of bloat

Avoid **monolith bloat**:

- Coordinators route; cohesive handlers decide. Do not keep adding unrelated private methods to
  `GameSession`, `WorldConsequenceApplier`, `InteractionSystem`, or `PromiseRealizationSystem`.
- Split by stable gameplay family, not by arbitrary line count and not one class per consequence.
- A file above roughly 1,000 non-generated lines is a review smell. A coordinator above roughly
  600 is a stronger smell. These are prompts to inspect responsibility, not mechanical gates.
- No existing hotspot above 1,000 lines may grow during a gameplay phase without an explicit
  explanation in the change note.

Avoid **abstraction bloat**:

- Do not create an interface for a single implementation unless it is a true replaceable boundary
  such as a provider, persistence adapter, audit sink, or renderer-facing view.
- Apply the rule of three before extracting a generic helper. Two similar pieces may remain clear;
  three genuinely identical policies should converge.
- Prefer explicit registration and ordinary composition over reflection, service locators,
  attribute magic, or a general event bus.
- Keep payload parsing and common transaction mechanics shared, but keep gameplay policy close to
  its cohesive handler family.
- Do not build plugin points, schema flexibility, or moddability beyond the next proven content
  use. Preserve clean data seams; do not implement hypothetical frameworks.

### Concept conservation

- No new ledger for a named story category. A safehouse is a place plus access/control/route facts,
  not a `SafehouseLedger`. An informant is an NPC with a want, bond, memories, knowledge, and a
  service, not an `InformantSystem`.
- No new consequence for a single fictional phrase. Collapse or alter a bridge with existing
  terrain/entity transformation and route consequences.
- No new command when an existing verb plus context can express the action cleanly. Add an explicit
  command only when agent/human intent would otherwise be ambiguous.
- No raw world flag that becomes an invisible rule. Prefer typed component/ledger state. If a flag
  is genuinely configuration or restoration state, expose its reader and provenance clearly.
- No state lane may be write-only. A writer and its first useful reader ship together.
- No player-facing mechanic may be GUI-only or CLI-only.

## Legibility is part of the rule

Every perceived consequence must answer three questions at the point it matters:

1. **What changed?** Concrete people, places, objects, conditions, and routes—not raw ids.
2. **Why did it change?** The deed, witness, want, rumor, cost, promise, rule, or faction
   expenditure that caused it.
3. **What can I do about it?** At least one visible response: flee, hide, bargain, investigate,
   fulfill, break, exploit, transform, fight, or wait deliberately.

Implementation rules:

- Every consequence carries source, evidence/reason, operation, visibility, and useful details.
- `StateDelta.IsPlayerVisible()` remains the filter for action messages. Audit wrappers and hidden
  records do not leak or duplicate narration.
- One player-visible event produces one primary message. Child audit deltas do not restate it.
- Failure uses stable reason codes plus specific player text. "No target" must distinguish missing
  selection, invalid type, out of range, hidden requirement, ambiguity, or stale reference.
- Views derive qualitative language from authoritative state. Exact bond/faction/resource numbers
  remain debug-only where the design calls for relationships and pressure rather than meters.
- Objectives show giver, destination or uncertainty, current status, and a state-sensitive next
  step. They never reveal hidden promise machinery.
- Slow model work always has visible purpose, status, cancellation/retry behavior, and instant
  alternatives where appropriate.

## Fun gates

Every gameplay slice is scored on:

- **Authorship:** did the player's particular wording, object, route, or relationship matter?
- **Wonder:** was there at least one specific, sensory, culturally situated outcome?
- **Mischief:** could the player trick, redirect, disguise, frame, steal, or exploit—not only kill?
- **Tactics:** did positioning, intent, terrain, timing, or resource choice matter without a model?
- **Responsiveness:** did an earlier fact return through a carrier or changed place?
- **Legibility:** could a documentation-blind player explain what happened and why?
- **Pace:** did ordinary play stay immediate, and was model latency used only for high-value
  interpretation?

A slice that scores correctness but not at least one of the first five should not merge as a
standalone feature. Pair it with the smallest player-facing use that proves its value.

## Golden gameplay proofs

These are regression scenarios for the interlocking machine, not scripted story content. Maintain
mock/replay episodes for all of them and periodically play them live through Godot.

1. **The quiet escape:** concealment, cover, or distraction changes witnesses and attribution; the
   player escapes without the same rumor/warrant path as a public massacre.
2. **The deed that walks:** a witnessed local spell becomes a rumor, travels along a real road in a
   named carrier, changes an NPC decision, and creates new material to act on.
3. **The gift that becomes a place:** an imbued item becomes a gift memory; dialogue reads it; a
   specific claim binds; travel delivers the promised person/site/item.
4. **The borrowed face:** a body swap or disguise changes institutional attribution while
   soul-bound legend and a deep relationship remain coherent.
5. **The price that comes back:** an ambitious cast creates a real curse, debt, vulnerability, or
   claimant; the cost later supports bargain, service, fulfillment, evasion, or conflict.
6. **The wanting stranger:** an NPC acts because of a want, rumor, memory, or promise; the cause is
   named; satisfying or violating it affects bond, service, faction, or future initiative.
7. **Three roads to the emperor:** force/resource depletion, identity/infiltration, and
   alliance/promise/legitimacy all reach the same ordinary emperor entity through shared state.
8. **The good ending to failure:** death selects the right treatment, the chronicle accurately
   reads deeds/rumors/bonds/promises, and one input starts a materially fresh run.

## Work-package protocol for Opus

For every numbered package below:

1. **Reconnaissance:** read the named docs and current files completely; inspect `git status`; map
   the current command → system → consequence → view path; do not trust stale line numbers or old
   plans over live code.
2. **Contract:** write or update focused tests for current behavior and the package's player-facing
   acceptance before refactoring or adding mechanics.
3. **Smallest vertical change:** implement one end-to-end path, including GUI/CLI/view/message
   access, before variants.
4. **Delete the duplicate:** remove superseded mutation helpers, message builders, fallbacks, or
   special cases in the same package.
5. **Prove it:** run focused tests, the full test suite, a mock CLI episode, save/replay when
   durable, and the named human feel check.
6. **Review the diff as design:** list new public types, durable fields, consequence types,
   commands, flags, and provider calls. Each needs an explicit justification. Report what was
   removed and which existing systems gained a new reader.
7. **Update durable docs:** architecture/subsystem docs and this plan's status in the same change.

If a package reveals that its proposed representation is wrong, stop and amend the plan before
stacking compatibility layers on it.

---

## Phase 0 — Converge the current hotspots without changing the game

Goal: create room for the roadmap without trading the existing monoliths for dozens of tiny
abstractions. This phase is behavior-preserving except for removal of confirmed duplicate
messages or dead branches.

Current review smells, not accusations:

- `WorldConsequenceApplier` is roughly 7,600 lines and owns every handler plus shared parsing,
  narration, composite transactions, and utility policy.
- `GameSession` is roughly 4,800 lines, mostly because dialogue proposals, claim binding, pending
  casts, repertoire, persistence, run completion, and post-action orchestration accumulated there.
- `PromiseRealizationSystem` and `InteractionSystem` each mix planning, content selection,
  transactional application, and player messaging.
- Two characterization-test files carry much of the suite. They are valuable safety nets, but new
  behavior needs focused homes rather than further growth in those files.

Line count is not the bug. Mixed reasons to change are. Preserve the public spines while making
cohesive policy independently readable.

### 0.1 Baseline and dependency map

**Status: complete — 2026-07-13. See [PHASE_0_BASELINE.md](PHASE_0_BASELINE.md).** Baseline pinned
(927 tests green, incl. new `ConsequenceCatalogTests` exhaustive dispatch/alias/reject contract);
reference scenario/seed, save size, durable-lane counts, architecture inventory, and an honest
eight-proof coverage map are recorded there. Next: 0.2.

- Pin the reference seed and produce golden transcripts for the eight gameplay proofs where the
  current engine supports them; mark genuinely missing links rather than faking success.
- Record current full-suite result, save size, long-episode state counts, and floor-model p50/p95 by
  purpose.
- Add characterization coverage around:
  - every consequence type and alias dispatching to its current behavior;
  - compound rollback for cast, service, rescue, world turn, promise realization, dialogue claim,
    and run completion;
  - `GameSession` post-action order: apply completed extraction → run completion check → actor
    turns → objective progress → final completion check;
  - player-visible message filtering and absence of duplicate child narration;
  - save/replay of pending casts and dialogue materializations.
- Create a short checked-in architecture inventory or test fixture mapping commands, consequence
  families, durable lanes, views, and provider calls. It exists to reveal duplicates; it must not
  become a second registry of behavior.

**Exit:** behavior is pinned strongly enough that structural movement cannot silently alter turns,
messages, rollback, replay, or provider semantics.

### 0.2 Decompose consequence application by cohesive family

**Status: substantially complete — 2026-07-13 (commits `69d170a`, `b81cd0d`, `08a7dd8`).**
`WorldConsequenceApplier.Apply` is now a 195-line facade (normalize → timing route → registry
lookup → unknown-type reject). The former ~72-case switch is an explicit static `FamilyDispatch`
registry; `WorldConsequenceTypes.IsKnown` is one canonical set; handlers live in eight cohesive
family partial files (`WorldConsequenceApplier.{Tactical,Space,Entities,Trade,Social,Scheduling,WorldRun,Compound}.cs`)
plus a shared-helper partial (`.Shared.cs`). Adding a consequence now touches its family file and
the registry, not a 7,600-line switch. `ConsequenceCatalogTests` pins the dispatch/alias/reject
contract and asserts the constants, `IsKnown` set, and registry are one set.

**Design choice vs. the recommended shape below:** partial classes were used instead of separate
handler classes + a `ConsequenceContext`. Rationale (per the complexity budget's "minimum private
machinery"): partial classes give each family its own cohesive file and a single explicit registry
while keeping every handler's direct access to the ~40 shared helpers and instance state, so the
move is pure relocation with zero behavior risk — no large context surface re-exposing state, the
child-apply sink, entity/reach lookup, and payload/delta helpers. The child-consequence sink stays
`this.Apply` under the existing snapshot rollback (see `Compound.cs` `ApplyFreeCaptive`).
**Remaining:** `Trade` (1462), `Entities` (1224), and `Social` (1169) partials are still >1000
lines — cohesive, not dumping grounds, but candidates for later intra-family subdivision.

Keep `WorldConsequenceApplier.Apply(WorldConsequence)` as the internal facade used by the existing
guard. Its only jobs after this package are normalization, explicit timing routing, family lookup,
and unknown-type rejection.

Recommended shape:

- One explicit `ConsequenceFamilyRegistry` maps normalized types to family handler delegates.
  Reflection and assembly scanning are unnecessary.
- A small shared `ConsequenceContext` supplies authoritative state, the injected apply sink for
  child consequences, entity lookup/reach helpers, payload reading, and delta/message helpers.
- Group handlers by cohesive policy, approximately:
  1. tactical resources/damage/status/resistance/delayed damage;
  2. space/terrain/movement/flow/doors/routes;
  3. entity lifecycle/spawn/transform/tags/behavior/control/souls;
  4. inventory/equipment/trade/services;
  5. social records/memory/bond/want/claim/rumor/deed/legend/suspicion/canon;
  6. promises/scheduled events/triggers/persistent effects/background jobs;
  7. world state/messages/selection/exploration/faction resources/run status/world-turn audit;
  8. compound interaction outcomes such as `free_captive` that intentionally transact several
     ordinary consequences as one player-visible event.
- Do not create one class and interface per consequence type. Family boundaries should correspond
  to shared invariants and shared reasons to change.
- Centralize payload coercion in the existing payload builder/reader seam. Prefer small typed
  internal payload records for complicated handlers while keeping the serialized envelope
  backward-compatible. Do not big-bang rewrite every dictionary merely for type purity.
- Centralize compound packet execution behind one internal transaction runner. Child mutations
  still use the injected shared consequence sink; this is not a second public apply point.
- Move subject/object grammar and visibility filtering to one shared narration helper used by
  handler families. It formats already-decided truth; it does not decide rules.
- Delete migrated methods from the original facade immediately. Do not leave forwarding methods
  or compatibility aliases unless saved public data requires them.

Migrate incrementally, never as a single rewrite: add shared context/registry under
characterization; move one simple family; remove its old methods; run focused and full tests; then
repeat. Move compound interaction/service/promise families last, after primitive child handlers are
stable. Every intermediate commit must compile and preserve the public apply facade.

**Tests:** all dispatch/alias characterization cases; each family directly; nested child rejection;
exception rollback; invalid resulting state; visibility; save/replay compatibility.

**Exit:** adding a consequence touches its family handler and the authoritative type/metadata
registry, not a 7,600-line switch. The guard/apply point, serialized contract, deltas, and behavior
remain stable. No handler family becomes a dumping ground for unrelated features.

### 0.3 Make `GameSession` an orchestrator again

Keep the public constructor/factory, `ExecuteAsync`, `View`, and `Observation` stable. Keep the
typed command switch explicit; it is a readable route table, not the source of bloat.

Extract cohesive collaborators:

- **Pending cast coordinator:** begin/await/cancel/materialize/save state, with mutation only when
  the materialized resolution re-enters the ordinary magic apply path.
- **Repertoire coordinator:** charter and echo lookup, replay, fatigue/drift policy, and echo
  recording. It uses existing magic application and cost paths.
- **Dialogue turn orchestrator:** route context, call/validate provider, maintain bounded ephemeral
  conversation history, and audit. It emits spoken text plus proposals.
- **Dialogue proposal applier:** normalize supported proposals into existing consequences or
  explicit command-like actor actions. Delete bespoke `ApplyDialogueX` branches when an existing
  consequence already expresses the result.
- **Claim intake/binding system:** validate support/provenance, record claim/memory/rumor, bind or
  update promises, and apply claim affordances transactionally. It is source-agnostic enough for
  dialogue, books, signs, props, and services to use one path.
- **Run lifecycle coordinator:** victory/defeat detection, run-status packet, chronicle assembly,
  and restart/checkpoint hooks.
- **Persistence coordinator:** flush non-authoritative pending materializations, then call the
  existing save service. It does not own state serialization rules.

Extract one collaborator per change in this order unless current dependencies prove otherwise:
pending casts/repertoire, run lifecycle/persistence, dialogue turn orchestration, proposal
application, then claim intake/binding. Remove migrated private methods each time and run the
session-order golden tests. Do not create all empty types first.

Make the post-action pipeline an explicit, named sequence with tests. Do not hide it in middleware
or an event bus. `GameSession` should visibly answer: route command, resolve slow work if any,
finalize accepted action, return result.

**Exit:** `GameSession` is a comprehensible public facade; dialogue, claim, cast, repertoire, and
run lifecycle policies have focused homes; command behavior and result ordering are unchanged.

### 0.4 Separate planning from applying in interactions and promises

- `InteractionSystem` resolves player intent and eligibility for `talk`, `give`, `read`, `examine`,
  `open`, `enter`, `leave`, `recruit`, trade, and service requests. Actual changes are consequence
  packets.
- `PromiseRealizationSystem` selects a realization archetype and builds a plan against current
  authoritative state. A plan contains proposed child consequences and a final promise update. It
  never writes ledgers or entities directly.
- Extract pure planners for item/person/site/route/service/threat realization only where they share
  enough policy to be independently testable. Keep simple variants together.
- Claims from documents, fixtures, NPC speech, and generated handoffs enter the same claim intake
  path. Remove any source-specific binding rules that merely duplicate that lifecycle.
- Detached generation sandboxes continue to start from real global ledgers, stage zone-local
  entities, and commit global state only after all children succeed.

**Exit:** interactions and promises differ in how they choose a packet, not in how they mutate
state. Golden promise/save/replay behavior remains unchanged.

### 0.5 Re-home tests by contract

- Preserve broad characterization episodes as regression assets.
- Put new and migrated focused tests beside domains: consequence families, dialogue turns, claim
  intake, promise planning, session finalization, run lifecycle, and view legibility.
- Prefer small builders for authoritative setup through consequences. Direct component mutation is
  acceptable only for scenario setup or restoration and should be obvious.
- Do not create a giant test framework. Extract fixtures only after repeated setup proves stable.

**Phase 0 exit gate:** full suite and golden transcripts are behaviorally stable; save/replay is
compatible; the four hotspots have cohesive boundaries; total production code need not shrink
dramatically, but duplicated branches and helpers do. The implementer can point to one place for
command routing, one apply point, one claim lifecycle, one promise-planning lifecycle, and one
message-visibility policy.

---

## Phase 1 — Close the current roadmap program through the new seams

Goal: finish the known top gaps before adding another gameplay category. This is
[ROADMAP_TO_1_0.md](ROADMAP_TO_1_0.md) Milestone 0.

### 1.1 One visibility and attribution policy

- Extend `PerceptionSystem` with one query/policy that accounts for line of sight, range, lighting
  if later present, concealment/status traits, body identity, and whether the witness saw actor,
  effect, both, or neither.
- Deed capture, suspicion attribution, hostile notice, crime investigation, and resolver-visible
  relation cards must consume that same policy or a projection of its result.
- Do not scatter checks for status ids such as `concealed`. Put semantic aliases and mechanical
  traits in `StatusRegistry`; policies read traits.
- Preserve the rule: hidden target knowledge may enter resolver context when useful, but renderer
  perception and witness attribution do not become omniscient.
- Produce a structured witness classification in debug state and concise in-fiction evidence in
  player messages.

**Proof:** the same escape performed publicly, behind cover, under concealment, and after body swap
produces four explainably different deed/suspicion outcomes. The quiet-escape golden proof passes.

### 1.2 Capital defense on real geography

- Represent Censor Gate, Archive Quarter, Inner Court, palace/archive thresholds, officials,
  credentials, and remaining defense resources using ordinary place, entity, faction, claim,
  access-tag, service, and route state.
- Derive a read-only capital-approach view from those facts. Do not add a plot-access meter or
  `FinaleSystem`.
- Implement the smallest force, identity/infiltration, and alliance/promise/legitimacy paths using
  shared consequences. Each path must be usable elsewhere in the world.
- The emperor remains an ordinary actor. Only run-completion observation knows the `win_condition`
  tag.

**Proof:** accidental two-travel regicide is impossible; three reference paths reach Odran; no
path calls a finale-only mutation.

### 1.3 One failure vocabulary

- Add or consolidate stable failure codes at command/reference/validation boundaries, retaining
  precise player messages and structured CLI errors.
- Missing target, ambiguous name, wrong type, stale id, out of range, blocked line, protected item,
  unpaid cost, unsupported consequence, provider failure, and intentional rejection must remain
  distinct.
- GUI hints, CLI JSON, transcripts, and resolver feedback read the same result fields.
- Technical failures remain non-turn-consuming; intentional rejections remain turn-consuming.

**Proof:** the rejection corpus contains each family and a documentation-blind tester can correct
the next command without debug state.

### 1.4 Latency and feedback baseline

- Consolidate per-purpose provider telemetry and resolver feedback already present in the worktree.
- Record routing, context size, queue time, generation time, parse/repair, validation, apply, and
  visible-result time without creating multiple telemetry models.
- Dialogue tiers, instant charter/echo alternatives, cancellation, and retry reuse the pending-work
  view.
- The telemetry path is audit/diagnostic only. It never alters runtime mechanics.

**Phase 1 exit gate:** roadmap Milestone 0 is genuinely empty; the hidden player is mechanically
hidden, capital access is geographic and systemic, rejection teaches, and latency has one measured
baseline. All four changes use the decomposed spines rather than adding new private paths.

---

## Phase 2 — Make a complete run before making more content

Goal: implement the 6–8 hour reference-transect structure that proves the final 8–12 hour arc.
Create `RUN_ARC.md` when this phase starts; keep pacing/tuning there, not in code names.

### 2.1 Four movements as derived state, never chapters

- Escape, Foothold, War, and Reach are pacing labels derived for debug/telemetry and possibly
  player-facing chronicle structure. They do not gate commands or own a chapter state machine.
- Derive progress from authoritative facts already present: escaped custody, known settlements,
  acquired repertoire/material/social capabilities, active pressure, opened routes, depleted or
  bypassed defenses, and capital location.
- If a small `RunArcView` helps testing and UI, keep it a read-only projection. Do not add a
  `RunArcLedger`.
- Instrument elapsed turns/real time, significant actions, capability gains, deaths, and capital
  route progress to expose pacing gaps.

### 2.2 Visible imperial escalation from finite expenditure

- Notices, patrols, warrants, and named hunters are data-authored response templates selected by
  the existing faction/world-turn machinery.
- Every response spends a finite resource, names its cause, materializes an entity/fixture/route
  condition where appropriate, and can be avoided, depleted, redirected, bribed, transformed, or
  exploited.
- Laying low uses existing quiet recovery/cooling rules. It must change actual available pressure,
  not merely hide a meter.
- Named hunters are ordinary archetyped actors with wants/knowledge/memories and behavior tags,
  not bosses with a private scheduler.

### 2.3 Progression as a capability portfolio

- Use existing lanes: charter forms, named echoes, equipment/foci/reagents, treasured items,
  bodies, followers, services, bonds, faction posture, routes, promises, scars, and rare typed stat
  changes.
- Add a read-only capability summary for telemetry/debug and a qualitative player journal/history
  surface. Do not add XP, levels, or a new progression currency.
- Tune the reference route so at least two capability lanes materially change problem-solving by
  the end of Foothold.
- Echo fatigue/drift protects fresh authorship. Charter forms cover routine reliability. Neither
  becomes a second spell game.

### 2.4 One economy serving travel, tactics, and social play

- Keep explicit deterministic buy/sell/service commands and one price policy.
- Add recurring sinks by composing existing state: transport, rest/lodging, information, bribery
  or credential access, reagents/equipment, and costly recovery.
- Merchants and providers remain normal NPCs with stock/services/wants. Do not add separate shop,
  inn, transport, or bribery systems when offers plus consequences suffice.
- Money, items, promises, memories, standing, and services may all be payment media when the
  existing consequence grammar can validate them. Do not ask a model to infer trade intent.

### 2.5 Persistence modes as one simulation

- Add a durable run-mode value: classic or checkpoint. The engine rules, content, RNG, economy,
  enemies, and victory remain identical.
- Save-on-quit is universal. Checkpoint mode stores an authoritative settlement-rest snapshot and
  restores it on death through the persistence/run-lifecycle boundary.
- Do not maintain a parallel checkpoint game state or fork action logic by mode. Only death
  handling differs.
- Chronicles record mode and restoration count without shaming or changing rewards.

### 2.6 Death, victory, chronicle, restart

- Select imperial/wild/other death treatment from authoritative killer, faction, region, and
  effect provenance. Deterministic templates always work; provider prose is optional enrichment.
- Assemble chronicles from deeds/rumors, unrealized bound promises, deepest bonds, capability
  portfolio, route, faction posture, and run conclusion. Do not accumulate a parallel narrative
  summary during play.
- Restore or start a new run through the same scenario/session factory. Cross-run memorials seed
  rumor/canon texture only; no power crosses.

**Phase 2 tests and proofs:**

- Each of the three capital routes completes under replay.
- Reference classic run wins in 6–8 hours; checkpoint death/restoration can continue to victory.
- Save/quit/load works during every derived movement and with pending materialized provider work.
- Economy has measured sources and recurring sinks; no required purchase can deadlock a seed.
- Chronicle names the actual route and omits facts the world/player never established.
- One input starts a different seed and surfaces a meaningful difference in twenty minutes.

**Phase 2 exit gate:** the game is a complete run-shaped object. It can still be mechanically thin,
but no remaining work is required merely to connect start, growth, campaign, capital, ending, and
restart.

---

## Phase 3 — Make the deterministic ten minutes excellent

Goal: Sorcerer remains a compelling turn-based game with providers disabled. The model should turn
good situations into singular ones, not rescue repetitive movement and combat.

Every encounter should pose at least two of these questions:

- Where is safety, cover, escape, leverage, or dangerous material?
- What will each actor do next, and how can that intention be disrupted?
- Who is watching, and what happens if they identify me?
- What can be stolen, transformed, recruited, bargained with, or used as spell grammar?
- Is winning the fight better than avoiding, redirecting, or ending it another way?

### 3.1 Data-driven actor archetypes over bespoke enemy classes

- Define a compact archetype record composed from existing stats, faction, equipment, behavior
  tags, charter forms, statuses/resistances, wants, and semantic description.
- Begin with roughly 10–14 slice archetypes spanning bruiser, ranged, caster, leader, beast,
  investigator, support, and elite hunter roles. Variants should remix loadout, terrain preference,
  faction, and behavior weights rather than subclass code.
- Keep one actor decision pipeline. Behavior tags and current state contribute scored candidate
  actions such as approach, retreat, seek cover, protect, investigate, use ability, attack, or
  call for help.
- Add a new AI action primitive only when at least two archetypes and a non-enemy actor can use it.
- A named hunter is an archetype plus identity, want, memories, knowledge, and equipment—not a new
  AI implementation.

### 3.2 Intent telegraphing through the shared view

- Compute the next tactical intention deterministically from the same candidate evaluation AI will
  use. Expose a player-safe summary through `GameView`; expose scores/reasons in debug observation.
- Intent is a forecast, not a reserved action. A changed board may change it when the actor acts.
- Use a small vocabulary with specific targets or places: advance, flank, brace, shoot, cast,
  protect, investigate, flee, call help, interact.
- Godot renders glyph/color/motion hints; CLI JSON exposes structured intent. Renderers do not
  recompute it.
- Do not reveal hidden actors or secret goals through intent views.

### 3.3 One tactical grammar

Deepen existing general rules before adding damage types or abilities:

- line of sight, range, chokepoints, forced movement, cover, hazards, destructible/transformable
  fixtures, retreat routes, and ally positioning;
- status traits that affect movement, action, perception, defense, vulnerability, or intent;
- terrain reactions using existing terrain/status/consequence families;
- persistent effects, auras, delayed harm, and flows only where encounters clearly teach and reuse
  them;
- ordinary items as tools, thrown/placed anchors, recovery, access, gifts, or spell material.

New status rule: a status must have a mechanical trait or tick, a clear source and expiry/clearing
rule, player/debug visibility, save/replay coverage, and at least two potential sources. Do not add
semantic status strings that no rule reads.

New terrain rule: prefer tags/reaction metadata consumed by the shared terrain systems. Do not add
`if region == ...` behavior for cultural texture.

### 3.4 Encounter grammar, not authored combat scripts

Author reusable ingredients in data:

- patrol crossing civilian witnesses;
- guarded threshold with credential, stealth, social, force, and magic approaches;
- two NPC wants contesting one object/person/place;
- environmental hazard surrounding valuable spell material;
- escort or follower under positional pressure;
- investigator arriving after evidence rather than before the deed;
- elite plus ordinary units whose roles interlock.

An encounter assembler chooses compatible ingredients from region, place, faction pressure, route,
and current consequences. It may reserve entities/fixtures and submit spawn/setup consequences. It
does not own encounter completion state; ordinary entity, want, deed, control, route, and promise
facts tell the world what happened.

Do not create an `EncounterLedger` or scripted room graph for the vertical slice.

### 3.5 Reliable tempo around authored magic

- Charter forms cover weak, precise, frequent tactical needs and interact with witnessing as
  licensed-looking magic.
- Echoes replay player-authored mechanics through current validation and target binding. Implement
  seeded drift only after fatigue and usage telemetry show echoes remain worth keeping.
- Targeting, item use, movement, surrender, fleeing, and charter/echo actions remain instant.
- Fresh wild casts should be tempting at pivotal moments, not optimal every turn. Measure fresh
  cast variety and echo/charter share rather than enforcing an arbitrary cooldown.
- Apply casting performance at the engine boundary using existing consequence severity and
  complication families. A poor score creates legible risk, not random unrelated punishment;
  skipping remains neutral.

### 3.6 Recovery, loss, and retreat

- Tune HP, mana, rest, services, consumables, travel, and money as one recovery economy.
- Retreat, surrender, concealment, bribery, and route escape must be real responses before lethal
  difficulty rises.
- Enemies telegraph severe actions; wild costs name what they took; provider failure never becomes
  danger.
- Avoid hunger, durability chores, inventory busywork, or attrition systems whose main function is
  extending the run.

**Phase 3 tests and proofs:**

- Provider-off ten-minute tests remain tactically varied across at least five encounter grammars.
- AI intent and actual action agree when state does not change and diverge explainably when it does.
- Each archetype has a systemic counter besides raw damage.
- Noncombat resolutions update the same authoritative facts used by combat outcomes.
- Terrain/status/AI rules are seed-stable, saveable, replayable, and represented in both views.
- No per-archetype class or per-encounter completion handler enters the engine.

**Phase 3 exit gate:** tactical feel, legibility, and pace score at least 4/5 in the reference
slice without relying on a live model. Adding a new archetype is predominantly data authoring.

---

## Phase 4 — Make mischief, identity, and attribution first-class play

Goal: stealth is more than hostile detection, and body swap is more than a control-pointer trick.
The player can hide, misdirect, impersonate, frame, and exploit the Empire's need to file a body
and name.

This phase justifies one new durable concept—**institutional identity**—because it has many writers
and readers. It does not justify separate disguise, evidence, investigation, warrant, or crime
engines.

### 4.1 Minimal identity model

- Legend remains soul-bound and unchanged.
- Add the smallest durable identity record needed to outlive a body/entity: stable identity id,
  public name/appearance reference, associated body or presented disguise, known signature
  fragments, status, and linked identity ids/evidence provenance.
- `DeedRecord` and `SuspicionRecord` gain or normalize worn/presented identity attribution while
  retaining soul provenance for legend and deep relationships.
- Existing body profiles seed identities. Body swap adopts the destination body's identity unless
  a disguise/pseudonym explicitly presents another.
- Keep institutional threat/warrants derived from identity-attributed deeds and claims. Do not
  copy the full faction or legend ledgers per identity.
- Add narrow `record_identity` / `update_identity` consequences only if existing entity, claim,
  deed, and suspicion updates cannot safely express creation and linking. If added, magic,
  dialogue, setup, and world turns must share them.

Writers: scenario/body creation, body swap, disguise/false-name actions, deed attribution,
Censorate correlation. Readers: warrants, patrol target choice, rumors, dialogue recognition,
bonds that know the soul, chronicles, and resolver context. This is the required combination
ratio for a new lane.

### 4.2 Disguise and credentials through existing capabilities

- A disguise changes presented identity through equipment/status/entity transformation plus an
  identity update. It does not change soul legend, promises, or deep bond truth.
- Uniforms, seals, warrants, invitations, and forged papers are items/readables/claims with access
  tags or service provenance. Thresholds use ordinary access policy.
- False names may create thin identities only when supported by context. Their credibility derives
  from matching body, clothing, papers, memories, or claims—not a new disguise meter.
- A magical signature can leak across identities through witnessed wild-magic deeds.

### 4.3 Evidence without an evidence subsystem

Represent evidence using existing objects and records:

- physical evidence: ordinary item/fixture entities with tags, readable provenance, memory/claim
  seeds, and ownership/location;
- testimony: witness memory plus claim/rumor provenance;
- institutional filing: canon/claim/warrant fixture tied to an identity;
- magical linkage: deed tags/signature fragments on identity records.

Framing, erasing, planting, stealing, transforming, or exposing evidence submits ordinary item,
memory, claim, rumor, deed/suspicion, identity, and faction consequences. Do not add an
`EvidenceLedger` until play demonstrates information that none of those can retain.

### 4.4 Investigation as bounded intent

- Investigators use the shared AI candidate pipeline with `investigate` behavior and visible
  intents: inspect a point/object, question a witness, follow a route, guard evidence, or call help.
- Off-screen correlation is one budgeted Censorate world-turn move that spends faction resources
  to link identity evidence. It does not simulate detective schedules.
- Identity merge/link rules are deterministic. The model may write the clerk's memo after the
  merge; it never decides the merge.
- The player can interrupt, misdirect, bribe, steal, transform, or deliberately feed the process.

### 4.5 Warrants and pressure become physical

- Warrants/notices are readable fixture/item entities generated from identity-attributed state and
  the recurring clerk voice.
- Patrols receive the identity description they are seeking, with uncertainty where appropriate.
- Wearing a wanted identity changes institutional reaction; changing bodies can misdirect it; deep
  allies and uncanny systems may still know the soul.
- UI and journal describe what the Empire believes, what it is uncertain about, and why—never a
  raw stealth percentage.

**Phase 4 tests and proofs:**

- Public, concealed, disguised, and body-swapped versions of the same deed attribute differently.
- Soul legend and soul-knowing bonds remain stable across identities.
- Evidence planting/removal changes a later bounded investigation and warrant target.
- Identity linking spends a faction resource, names its evidence, and is replay deterministic.
- A player can frame an NPC or stale identity without a bespoke quest or spell phrase.
- The borrowed-face golden proof passes through GUI and CLI.

**Phase 4 exit gate:** mischief scores at least 4/5; the Empire is powerful partly because it
records, and vulnerable because records can be manipulated. The implementation added identity as
one high-leverage lane, not five crime subsystems.

---

## Phase 5 — Spin the response flywheel faster, not wider

Goal: existing ledgers return to play often enough that the world feels opinionated. This phase
adds readers, prioritization, and deliveries—not a new simulation layer.

### 5.1 One bounded return planner

At each existing world-turn pump, gather candidate moves from current systems:

- rumor propagation or carrier approach;
- promise stirring/realization eligibility;
- notable NPC want or memory initiative;
- faction expenditure/recovery;
- scheduled/deferred consequence;
- identity investigation/correlation;
- narrator memo or cause-bearing local response.

Score candidates deterministically by salience, freshness, distance/route, relevance to local
actors, unmet response cadence, resource availability, and cooldown. Spend the existing bounded
world-turn budget. Keep family-specific eligibility in the owning system; centralize only
cross-family prioritization so five systems do not each produce unbounded surprise messages.

Do not add real-time jobs, daily schedules, or one hidden timer per feature.

### 5.2 Response cadence and orphan detection

- A significant public act should usually receive one immediate/local answer and one later answer
  within one to three zone transitions.
- Slow promises remain slow when their explicit timing/trigger calls for it.
- Instrument durable writes with no plausible reader, active promises with no realization route,
  rumors with no reachable carrier, wants no action can affect, and faction reactions with no
  physical manifestation.
- Fix orphaned writes by adding a reader or removing the writer. Do not solve them with generic
  narration that claims the world responded when state did not.

### 5.3 Social play uses the same tactical state

- Gifts transfer the real item and write memory in one packet.
- Services apply real consequences, payment, want completion, and memory.
- Recruitment changes faction/control/bond/want/memory using the existing composite outcome.
- Refusal, surrender, betrayal, departure, aid, and approach derive from want, bond, memory,
  knowledge, faction, identity, and current danger.
- Dialogue interprets ambiguous player speech and voices response. Critical eligibility and
  completion remain deterministic and provider-free.
- Deep relationships may key to soul identity when earned/witnessed; casual recognition keys to
  presented identity.

### 5.4 Promise delivery becomes ordinary world content

- Keep one realization lifecycle for NPC, item, site/fixture, route, service, threat/claimant,
  stock, memory/canon, and scheduled consequence archetypes.
- A promise plan reserves or finds real geography and produces ordinary entities/components.
- Realized content must have at least one tactical/social/material affordance and one possible
  onward handoff. A named rock with no use is not a delivery.
- Completion updates the source claim/want/memory/faction state through consequences. No separate
  quest completion record.
- The journal exposes evocative but actionable uncertainty. Debug shows the binding exactly.

### 5.5 Costs become future play

- Expand cost selection/guidance using the existing ladder: resource, item, strange status/trait,
  curse/debt/attention, severe body/stat/treasured cost.
- Every nontrivial cost lands in authoritative state and names its cause.
- Tier 3–4 costs ship with at least one general counterplay route: fulfill, clear, transfer,
  bargain, evade, exploit, transform, or endure for an upside.
- Debt claimants are ordinary promised NPCs. Curse clearing is a service/deed/promise condition.
  Reputation exposure is a deed/identity consequence. Do not create a private cost quest engine.
- A deterministic fallback chooses legal cost families when a high-severity resolution omits a
  meaningful cost; it prices by general severity/profile rules, never spell phrases.

### 5.6 Campaign verbs are compositions

Use these as acceptance examples, not new subsystem names:

| Player-facing verb | Representation | Shared consequences/readers |
|---|---|---|
| Establish a safehouse | place/fixture with access, route, provider, control and memory facts | spawn/transform fixture, create route, service, faction/control, memory |
| Interdict supplies | real stock, route fixture, convoy actors, faction resources | inventory/stock, route/terrain/entity transform, deed, faction resource |
| Build an informant network | NPC wants, bonds, knowledge, rumor carriers, paid services | bond/want/memory, trade/service, rumor, NPC initiative |
| Liberate or lose a settlement | control policy/faction posture on ordinary place actors and fixtures | faction/control, standing/resource, memory/deed/rumor, routes/services |
| Expose or frame an official | identity, evidence items, testimony claims, public deed | claim/memory/rumor, identity/suspicion, deed, faction response |
| Bind a prophecy route | promise against real place/person/access facts | claim/promise, route/site/person realization, access consequences |

If implementation proposes `SafehouseSystem`, `SupplyInterdictionSystem`, `InformantSystem`, or
`LiberationQuest`, stop and return to this table.

**Phase 5 tests and proofs:**

- The deed-that-walks, gift-that-becomes-a-place, price-that-comes-back, and wanting-stranger
  golden proofs pass.
- A public deed produces one local and one later response without message duplication.
- World-turn budgets remain bounded and rejected child packets roll back.
- At least one generated objective accepts two mechanically different solutions and completion
  reads resulting state rather than a privileged verb.
- Laying low, manipulating identity, or changing roads measurably changes response candidates.
- Provider-off play retains every mechanical handoff.

**Phase 5 exit gate:** most significant actions create a visible return within the intended
cadence, and the return creates a new decision. The number of durable concepts has barely grown;
the number of cross-readers has grown substantially.

---

## Phase 6 — Integrate and tune the complete vertical slice

Goal: make **Marble Containment Yard → Hollowmere Margin → Vigovian Capital** feel like a finished
game in miniature. Do not solve integration problems with reference-seed special cases.

### 6.1 The opening teaches by consequence

- In one glance, show custody, a guarded/locked threshold, at least one usable official fixture,
  a readable claim source, a pressured person, tempting material, witnesses, and regional texture.
- Within the first few turns, tempt but do not require a wild cast.
- The first accepted cast should normally create one visible fact beyond its immediate tactical
  effect: witness/deed, memory/bond, rumor, faction expenditure, cost, or promise.
- Rescue, stealth, violence, theft, body/identity play, social leverage, and magic all reach escape
  through ordinary systems.
- Lio remains a generated-system proving ground. No command, handler, or promise archetype checks
  Lio's id.
- One local promise pays off quickly; two larger hooks survive into Hollowmere.

### 6.2 Hollowmere proves density

- At least three distinct districts alter props, terrain, people, witnesses, services, voice, and
  encounter opportunities—not only names.
- Smaller sites/hamlets, road/transport, significant interior, merchants/services, journey
  families, local faction pressure, and folk culture all reuse content schemas.
- The region supplies tempting spell vocabulary: reeds, water, memories, household refuge,
  ferries, oaths, remedies, imperial seams.
- Every notable NPC satisfies the content-unit rule: want, knowledge boundary, posture,
  memory/bond surface, and a way to affect play.
- Every place has a reason to return or a deliberate one-time role.

### 6.3 The capital proves scale, not a finale exception

- Outer pressure, Censor Gate, Archive Quarter, Inner Court, palace/archive, and throne form a
  campaign of ordinary thresholds, actors, evidence, faction resources, services, claims, and
  routes.
- Prior deeds, identities, allies, promises, stolen/earned access, and depleted defenses arrive as
  real advantages or liabilities.
- At least one earlier NPC/object/rumor/cost returns inside the capital.
- Odran acts, takes damage, can be transformed or otherwise affected, and dies under ordinary
  entity/consequence rules. Run lifecycle observes the result.

### 6.4 Tune the connective tissue

- Remove empty travel, repeated low-value combat, duplicate narration, dead economy sinks,
  excessive model calls, and objective chains that do not change decisions.
- Use faster transport and compressed geography before adding content to solve pacing.
- Track turns and real time between authored action and response, capability gains, pressure
  escalations, healing/rest, money source/sink, wild casts, and route progress.
- Balance origins as different risk/expression profiles, including a forgiving and cruel start,
  not as classes or a global difficulty slider.
- Run the same route with provider disabled, floor local model, and one supported cloud provider.

### 6.5 Prove generality outside the slice

- Run a generated-thin world tour through at least three other cultures.
- Confirm that encounter grammar, NPC wants, claims/promises, region props, services, rumors,
  identity, faction responses, and presentation views work without code changes.
- If another region needs code, fix the shared content template or system seam. Do not begin
  densifying that region in this phase.

**Phase 6 release gates:**

1. A human can start cold, win or die, read a faithful chronicle, and restart without developer
   intervention.
2. Provider-off play is a coherent roguelike; live magic makes it singular.
3. Floor-model magic regularly uses local objects and prior facts within measured budgets.
4. A documentation-blind player reaches Hollowmere, explains why the world reacted, and retains
   at least two hooks.
5. Feel scores reach at least 4/5 for authorship, wonder, mischief, tactics, responsiveness,
   legibility, and pace across multiple testers.
6. Two runs differ in repertoire, material, relationships, identity/scars, route, and chronicle.
7. A tester voluntarily starts a second run and finds an early meaningful difference.

**Phase 6 exit gate:** the vertical slice is fun enough that the next dollar/hour belongs in
presentation and productization, not another engine mechanic or another region.

---

## Phase 7 — Make the simulation effortless to read and delightful to inhabit

Goal: illuminated ASCII, portraits, UI, motion, and audio expose the machine's meaning without
duplicating it. Presentation is allowed to be lavish; rules remain singular.

### 7.1 Semantic presentation events from authoritative deltas

- Keep `StateDelta` and action results semantic: damage, movement, status, summon, promise
  realization, rumor arrival, identity link, faction response, access change, death, and victory.
- Godot maps semantic events to animation, particles/glyph motion, camera emphasis, audio, and
  timing through presentation catalogs. Core state does not name colors, tweens, sounds, or scene
  nodes.
- Add missing delta details only when multiple renderers/audits need the meaning. Do not add a
  mechanical rule to support an animation.
- One action result owns event order. Presentation may stage that order visually but may not
  replay mutations or synthesize new outcomes.
- Reduced-motion mode maps the same semantic event to restrained emphasis; it does not disable
  information.

### 7.2 Illuminated ASCII through data, not region branches

- Extend region/style data with palette roles, ornament, light/ambient treatment, and restrained
  UI motifs. The renderer consumes roles such as threat, ally, wild, imperial, water, memory,
  promise, and highlight rather than hard-coded region ids.
- Keep glyph identity stable enough for learning. Use color, border, light, and motion to express
  region without making tactical information decorative noise.
- Wild casts receive choreography scaled from accepted consequence families/severity and cast
  performance. Charter magic receives exact geometric choreography. Backfires remain beautiful
  and legible.
- Status, intent, selection, line of sight, witness risk, interactable affordances, promise
  realization, and damage all need distinct but composable visual grammar.
- No weather simulation is implied by ambient rendering. Atmosphere is presentation until a real
  authoritative condition exists.

### 7.3 One UI hierarchy over shared views

At a glance, the player should see:

1. controlled body, immediate threats, terrain, and selected target;
2. available nearby interaction or targeting correction;
3. current objective/next step and urgent consequence in motion;
4. HP/mana/status/repertoire/material state;
5. provider/pending-cast state;
6. deeper journal, identity/standing, promises, rumors, inventory, followers, and atlas on demand.

- Build panels from `GameView` subviews shared with CLI formatting/JSON.
- Progressive disclosure may hide detail, not rules the player must know.
- Nearby verbs are derived from interactable components and command eligibility. The GUI does not
  invent actions absent from the command spine.
- Failure hints consume the shared failure vocabulary.
- Debug overlays consume `AgentObservation(debug: true)` or dedicated read-only debug views; they
  never reach into systems.

### 7.4 Audio as another view of consequences

- Create one presentation-side audio router from semantic action/delta events.
- Establish a compact SFX vocabulary for input/selection, movement, doors, items/coin, melee,
  impact, statuses, witnesses/alerts, promises, wild casts, charter casts, death, and chronicle.
- Region ambient beds and score motifs come from region/pacing presentation data. They do not
  advance simulation or reveal hidden threats.
- Wild-magic audio layers consequence family, severity, local tradition texture, cost, and
  complication. Avoid generating or selecting rules from audio.
- Every audio-only cue has a visible equivalent; the game remains playable muted.

### 7.5 Portraits without a second character identity store

- Attach portrait asset/provenance keys to existing profile/identity presentation data or an
  explicitly non-authoritative asset catalog.
- Notable NPCs, followers, named hunters/officials, Odran, death epitaph speakers, and character
  creation receive slice coverage.
- Portrait generation/import never changes NPC facts. Missing assets fall back cleanly to glyph,
  name, and descriptive text.
- Reuse the same portrait in dialogue, warrants, journal/standing context, and chronicle where the
  known identity matches. Disguises/identity uncertainty must not leak the hidden portrait.

### 7.6 Whole-run UX and accessibility

- Give character creation, title/main menu, new/continue, load/error recovery, travel/transition,
  rest/checkpoint, victory, death, chronicle, and restart coherent presentation.
- Surface remappable input, keyboard-only play, font scale, color-safe palettes, reduced motion,
  audio levels, minigame opt-outs, and neutral casting.
- CLI parity means the capability and information exist, not that the CLI imitates animation.
- Do not let a settings screen mutate simulation state directly; settings are product config
  consumed by hosts/views.

**Phase 7 proofs:**

- Thirty seconds of unedited play is recognizable as Sorcerer and understandable to a viewer.
- A player can state who threatens them, what is interactable, what the last consequence answered,
  and several next actions without debug state.
- Muting or reduced motion removes sensation but not information.
- A second renderer could consume the same semantic views/deltas without engine changes.
- No Godot script owns combat, targeting, interaction eligibility, provider legality, or turn use.

**Phase 7 exit gate:** the slice looks and sounds finished; presentation has multiplied clarity and
identity without creating a second game.

---

## Phase 8 — Turn the slice into a standalone product

Goal: a Windows player with no repository, terminal, Python runtime, or prior model knowledge can
install, configure, play, suspend, recover, update, and report the game.

### 8.1 One product configuration model

- Keep provider, background-job, audio/video, input, accessibility, privacy/telemetry, and path
  settings in one versioned product configuration service outside authoritative `GameState`.
- Expose the same effective provider/background settings to CLI flags and Godot settings, with
  explicit precedence: command-line override → saved user setting → safe default.
- Do not duplicate provider defaults across CLI, GUI scenes, and constructors.
- Secrets use OS-appropriate secure storage where available and never enter saves, transcripts,
  telemetry, or logs.

### 8.2 First-run wizard as a recoverable state machine

The wizard stages are explicit and resumable:

1. choose local, cloud key, or play demo now;
2. detect Ollama executable/service and supported model;
3. obtain consent before install or model pull;
4. show disk requirement, download progress, cancellation, and recovery;
5. validate one tiny structured request against the actual provider/model;
6. run a compatibility/latency check and explain the quality tier plainly;
7. enter character creation/opening or fall back to demo without losing configuration progress.

- Installation/pull work belongs to a product-side provider setup service, never the simulation.
- Detect external-state changes on relaunch; do not trust a stale completed flag.
- Cloud paths explain that player-written text/context leaves the machine, which provider receives
  it, and likely cost behavior.
- Model/provider validation tests the same structured call harness used by play.

### 8.3 Demo is the same engine, not a fake resolver

- Provider-disabled demo uses the ordinary world, commands, combat, stealth, economy, reactions,
  charter forms, and echo apply path.
- If demonstration echoes are needed, ship explicitly labeled pre-materialized resolutions and
  validate/apply them through the normal echo pipeline. Do not pretend arbitrary free-form text
  was resolved by a local regex/mock spell engine.
- Let the player open model setup later without restarting the run where safe; clearly explain
  which capability free-form casting adds.
- Mock providers remain test tools and do not silently power the public demo fantasy.

### 8.4 Provider failure loses time, not state

- Pending work preserves spell/dialogue input, purpose, context snapshot/materialized result where
  available, start time, failure category, and retry/cancel options.
- Provider transport failure, timeout, malformed output, and local process loss remain technical
  failures and consume no turn.
- Retry uses the ordinary resolve/apply seam and revalidates current state. If state has become
  stale, report that specifically.
- Instant deterministic actions remain usable whenever the turn contract permits; never mutate in
  a background callback.
- Kill the provider during every pending-work stage in tests and manual QA.

### 8.5 Save safety and compatibility

- Autosave at bounded safe points; save-on-quit; rotating backups; atomic replace; corruption
  detection and recovery UI.
- Versioned migrations are explicit and tested from checked-in golden public saves. No EA save
  wipes.
- Bound save size: archive/prune audit detail appropriately while retaining authoritative ledgers,
  provider materializations needed for replay, and the chronicle.
- Checkpoint snapshots use the same serialization/migration path as ordinary saves.
- A 20-hour soak verifies memory, entity/ledger/background queue growth, save time/size, and
  load/continue.

### 8.6 Reporting and trust

- Telemetry is off or opt-in according to the owner decision and explained in plain language.
- Aggregate operational metrics exclude raw spell/dialogue prose unless the player explicitly
  attaches a transcript to a report.
- In-game report bundling redacts keys, machine secrets, unrelated files, and private paths;
  previews exactly what will be submitted.
- Version, seed, provider/model tier, failure codes, recent audit ids, save schema, and optional
  transcript make reports reproducible.

### 8.7 Distribution

- Reproducible Godot/.NET Windows export with embedded validated content and no source-tree path
  assumptions.
- Clean install/uninstall/update behavior; visible version; documented save/config locations;
  credits/licenses; privacy/provider disclosure.
- Smoke matrix covers fresh standard user account, paths with spaces/non-ASCII, offline launch,
  missing/corrupt config, local setup, BYOK, demo, update, save recovery, and uninstall preserving
  user saves according to policy.

**Phase 8 exit gate:** fresh machine to first accepted wild cast is under fifteen minutes or the
player is already in a real demo; provider death cannot corrupt or consume a turn; a public-schema
update preserves a mid-run save; settings require no file editing.

---

## Phase 9 — Early Access and breadth by proven region packs

Goal: widen replay variety by authoring through the proven template. New regions should mostly add
data and assets, not engine concepts.

### 9.1 Early Access gate

- Closed alpha first: observe strangers, opening completion, provider setup, full-run blockers,
  death/restart, chronicles, support load, save migration, and model/hardware diversity.
- Public itch.io Early Access only after the four vertical-slice proofs in the roadmap remain
  green for people outside the project.
- Publish exact supported providers/model floor, expected run length, classic/checkpoint behavior,
  privacy, and the generated-thin state of non-slice regions.
- Alternate depth/fix releases with region-pack releases. Do not stack new systems and a new
  culture in the same update unless the culture exposed the general need.

### 9.2 Region-pack production template

One region pack includes:

- world-roll role and at least one tactical/campaign affordance;
- settlement districts, hamlets/sites, roads/transport, significant interior, and reason to
  return;
- population grammar and notable roles with wants/knowledge/services/stock/claims;
- prop/item families that support multiple uses;
- enemy archetypes assembled from shared tactical primitives;
- encounter and journey templates accepting systemic alternatives;
- faction/control pressures and integration with Empire/rival state;
- promise realization vocabulary and at least one long-return chain;
- lore/voice, palette/ornament, ambient bed, score motif reuse/variation, portraits where notable;
- provider context stress test, mock transect, live cast/dialogue feel test, save/replay and content
  validation.

Each pack must introduce a new **combination** of existing systems. Example emphasis matrix:

| Region character | Existing systems to recombine |
|---|---|
| Vint intrigue | identity/evidence + rumors + trade + woven objects + faction politics |
| Brall tale culture | witnessed deeds + rumor distortion + bone props + bonds + public halls |
| Ryolan honor | oaths/promises + public witnesses + duels/intent + legitimacy + blood costs |
| Stalnaz crystal | stored material/reagents + music/light terrain + court access + succession claims |
| Parn roads | transport + sound/ink traits + caravan stock + routes + traveling carriers |
| Gontark curses | cost/curse conditions + services + identity/reputation + claimant promises |

This matrix steers content; it does not grant per-region handlers.

### 9.3 Content validation and authoring velocity

- Validate schema version, ids/references, embedded resources, place-graph connectivity, promise
  destinations, population roles, services/stock, content voice, and seed stability in CI.
- Build small authoring reports/previews only where they remove repeated manual errors. Do not
  build a general editor before data iteration proves the need.
- Measure time to author and test a region pack. If code dominates, stop and repair the template.
- Rebalance the 8–12 hour critical path after every two packs. More map is optionality, not required
  distance.
- Add bestiary, interiors, dungeons, journeys, portraits, score, and minigames only as region packs
  need them and only through shared primitives.

### 9.4 Modding only on evidence

- Preserve loose JSON/Markdown overrides, schema versions, and code-owned operation behavior.
- If Early Access demonstrates real demand, document and stabilize the content surfaces players
  already use.
- Do not permit prompt content or mods to bypass validation, protected items, engine authority, or
  save safety.
- Adding a new operation remains code; mods compose existing operations/consequences.

**Phase 9 exit gate:** all planned 1.0 regions meet the content-unit rule and their culture
transects; no region owns private code; the complete world still supports the 8–12 hour win path;
two varied runs do not rhyme.

---

## Phase 10 — 1.0 release candidate and deletion pass

Goal: stop adding and make every remaining concept earn its place.

### 10.1 Freeze and matrix

- Freeze mechanics, content schemas, and save schema except for blocker fixes.
- Run the complete matrix from the roadmap: clean setup, demo/offline, floor local model, each API
  family, provider loss/recovery, both persistence modes, every run movement, three reach routes,
  victory/death/chronicle/restart, migrations, accessibility, common hardware/resolutions.
- Run seeded agent populations across origins, regions, identities, economy paths, and capital
  routes plus repeated 20-hour soaks.

### 10.2 Remove experiments and compatibility scaffolding

- Resolve feature flags: ship, remove, or make developer-only. Do not leave half-supported public
  lanes.
- Delete superseded aliases/helpers/adapters once public save/replay migration no longer needs them.
- Find state fields with no writer or reader, consequences with no source or consumer, commands the
  GUI cannot reach, views no renderer uses, and messages that duplicate another event.
- Remove unused content schema fields and provider prompt cards that no route selects.
- Review every public interface with one implementation and every large coordinator for accidental
  abstraction or monolith growth.

### 10.3 Fairness, resilience, and release operations

- Review death causes, technical failures, rejection reasons, economy exploits, dominant origins,
  echo usage, route diversity, objective deadlocks, and response cadence.
- Confirm no technical provider failure consumes a turn or mutates state; no mandatory flow lacks
  keyboard/neutral accessibility; no critical path depends on debug knowledge.
- Prepare rollback/update/support plan, final store assets, credits/licenses, disclosures,
  soundtrack delivery, and shareable chronicle format.

**Phase 10 exit gate:** the eight roadmap pillars are green at world scale; two release candidates
survive the full matrix without save-format change; there are no known ship blockers. Ship quality,
not date, ends the phase.

---

## Cross-cutting verification matrix

Every change uses the smallest relevant rows; every phase gate uses all of them.

| Layer | Required proof |
|---|---|
| Pure rule | focused unit tests for inputs, boundaries, deterministic RNG, and no mutation during validation/planning |
| Consequence handler | success, specific rejection, exception/invalid-state rollback, visibility, provenance, child packet behavior |
| Command/session | success/failure result shape, turn use, actor turns, objective/run finalization order, pending-work policy |
| Unified entity/state | body/soul ownership, targeting, serialization, zone snapshot, no orphan records/entities |
| View/legibility | player-safe state, debug exactness, cause and next action, no raw ids or hidden leaks, no duplicate messages |
| GUI/CLI parity | same typed command/session path and equivalent capability/readout; no renderer-owned rule |
| Provider-off | full mechanical path with deterministic fallback/instant actions; no hidden second resolver |
| Live provider | validity, specificity, consequence, surprise, tone, local-anchor use, p50/p95, cancellation/recovery |
| Save/replay | round-trip, migration where public, materialized provider results, deterministic final assertion |
| Agent episode | real player-facing discovery and action path, not debug-seeded completion |
| Human feel | ten-minute loop or full-run question appropriate to the phase; scored authorship/wonder/mischief/tactics/response/legibility/pace |
| Boundedness | entity/ledger/queue/save/memory growth and world-turn budgets under long episodes |

## Architecture enforcement

Use tests and review to protect the boundaries without building a bureaucracy:

- Project-reference tests/build rules keep Godot out of Core and providers out of deterministic
  rule assemblies.
- A consequence catalog test ensures every known type has exactly one family owner, metadata, and
  dispatch path; aliases normalize unambiguously.
- Persistence coverage enumerates all durable component/ledger families and fails when a new one is
  omitted.
- Command parity coverage enumerates typed commands and confirms CLI parsing/JSON plus a Godot or
  shared-command reachability declaration. Do not fake GUI parity with duplicated test-only maps.
- Content validation loads the same loose/embedded corpus used by shipping builds.
- Focused source review searches for direct ordinary ledger/component writes outside setup,
  restoration, guarded transactions, and consequence handlers. Do not rely on a brittle text scan
  as the only enforcement.
- Provider calls are purpose-labeled and audited; a test enumerates purpose/config routing.

## Change-budget report

Every work-package handoff reports:

```text
Player decision improved:
Flywheel handoff completed:
Existing state read:
Existing consequences reused:
New public types:
New durable fields/lanes:
New consequence types:
New commands:
New flags/config:
New provider calls:
Duplicate/dead paths removed:
Player-visible cause and counterplay:
GUI/CLI/replay/save evidence:
Feel result:
Remaining deferral:
```

Empty is the preferred answer for most “new” rows. A non-empty answer is not automatically bad;
it makes complexity a conscious cost instead of an accidental byproduct.

## Definition of done for every work package

- [ ] It creates a meaningful player decision or closes a named legibility/reliability gap.
- [ ] It names the flywheel stage and the downstream reader/carrier.
- [ ] It used the lowest sufficient rung of the representation ladder.
- [ ] World mutation uses `GameEngine.ApplyConsequence` or an injected guarded sink; compound
      children commit transactionally.
- [ ] No model, renderer, background job, or content source mutates authoritative state directly.
- [ ] The deterministic provider-off path works.
- [ ] GUI and CLI use the same `GameSession` command/capability path.
- [ ] Player-facing state comes from shared read-only views/deltas and includes cause/counterplay.
- [ ] Turn consumption, rejection, rollback, target binding, save/replay, seed stability, and
      boundedness are covered as relevant.
- [ ] Focused and full tests pass; a real agent episode exercises discovery and action.
- [ ] A human feel check answers the package's product question.
- [ ] Superseded branches/helpers/messages are deleted; no compatibility path remains without a
      stated saved-data reason.
- [ ] New concepts and hotspot growth are justified in the change-budget report.
- [ ] Durable docs change in the same patch.

## Execution order and stop rule

| Order | Phase | Roadmap owner | Status at this plan's writing |
|---:|---|---|---|
| 0 | Converge current hotspots | prerequisite protecting every milestone | 0.1 done; 0.2 applier split done; 0.3 (GameSession) next |
| 1 | Close current roadmap program | Milestone 0 | pending Phase 0 |
| 2 | Complete run structure | Milestone 1 | pending Phase 1 |
| 3 | Deterministic ten-minute game | Milestone 2 / P3 | pending Phase 2 structure |
| 4 | Mischief, identity, attribution | Milestone 2 / P3 | pending visibility policy and tactical intent |
| 5 | Response flywheel density | Milestone 2 / P2–P4 | pending shared identities/tactics |
| 6 | Vertical-slice integration | Milestone 2 gate | pending Phases 2–5 |
| 7 | Presentation | Milestone 3 | prototype long-lead work may run earlier; full pass pending slice feel |
| 8 | Productization | Milestone 4 | setup/save prototypes may run earlier; full pass pending slice feel |
| 9 | Early Access and breadth | Milestones 5–6 | pending four slice proofs |
| 10 | 1.0 release candidate | Milestone 7 | pending breadth freeze |

Start with **Phase 0.1 only**. After its evidence note, perform 0.2 as a behavior-preserving
structural change. Do not begin Phase 1 merely because part of Phase 0 compiles. Do not begin broad
content authoring merely because the schemas exist.

Stop and ask the owner only when a choice changes a product pillar, persistence promise, platform,
reference transect, run-length target, public content scope, or introduces a new durable concept
that fails the admission test. Ordinary code-shape and implementation details should be resolved by
the principles above and current repository evidence.

The plan succeeds when adding one good idea makes several existing systems more useful, the player
can see the resulting chain, and the code contains fewer ways for the world to become true—not
more.
