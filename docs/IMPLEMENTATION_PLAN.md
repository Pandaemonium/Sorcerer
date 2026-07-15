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

Execute this plan as a sequence of playable vertical changes, not as one blind rewrite. A numbered
package is the smallest reviewable unit, but its exit gate is an evidence check rather than a
permission barrier. Keep moving through the next package while the build is sound and the next
highest-value change is clear.

## Owner calibration — binding product direction

These decisions were settled on 2026-07-13 for the overnight implementation sprint and future
work. When several technically sound interpretations exist, choose the one that best matches this
section.

### Overnight authority and operating mode

- **The morning result must be player-visible gameplay improvement.** Architectural cleanup is
  valuable when it makes that improvement easier to implement, understand, or extend; cleanup by
  itself is not the sprint outcome.
- Continue autonomously through **Phase 7**. Do not stop at a phase boundary merely to request
  review. If a package is already substantially met, close its smallest real gap and move on.
- Very large refactors are authorized when they reduce duplication and leave a more legible route
  to gameplay. Keep the build runnable at coherent checkpoints; do not preserve a bad seam merely
  because changing it makes the diff large.
- If a genuinely necessary durable concept, command, component, or consequence survives the
  feature-admission test, implement the feature end to end. Do not leave an abstract scaffold or
  wait for owner approval. The new concept must ship with writers, readers, persistence, both
  interfaces, tests, presentation, and deletion of the superseded representation.
- **There is no pre-Early-Access compatibility obligation.** Break internal saves, transcripts,
  APIs, test fixtures, aliases, and content schemas when doing so simplifies the game. Update or
  regenerate current fixtures and delete the old path in the same change. Do not add migrations,
  fallbacks, dual reads/writes, deprecated wrappers, or legacy branches for unreleased builds.
  Public-save compatibility begins only when an Early Access build creates that promise.
- Do not stop because the diff is large, a product choice is locally uncertain, or a test exposes
  more work. Diagnose, simplify, fix, delete an incomplete approach if needed, and continue. A true
  external blocker such as missing credentials or an unavailable service should be recorded, then
  work should continue on every unaffected lane rather than spawning fallback machinery.

The overnight implementation loop is:

1. Play the current build manually through the real CLI.
2. Choose the most damaging player-visible break in curiosity, casting, travel, tactical mastery,
   organic direction, relationship, or legibility.
3. Implement the smallest general end-to-end improvement through the shared command,
   consequence, time, and view spines.
4. Delete the duplicate, dead code, obsolete fixture, and superseded representation immediately.
5. Run focused tests, then manually replay the affected route with the live resolver.
6. At stable checkpoints, run the full suite, record the evidence and complexity delta, and
   continue with the next highest-value break.

Live playtests are first-person implementation work, not delegated agent simulations. Run them
frequently—after every player-facing vertical change and at least once per substantial structural
increment—using the CLI with `--provider gemini --model gemini-3.5-flash --effort medium`. Keep
useful transcripts and latency/result notes. Mock and replay providers remain fast regression
tools, but they are not evidence that tonight's casting, dialogue, or overall game feels good. Do
not expose API keys in commands, transcripts, logs, or documentation.

### The desired play texture

- The model-free ten minutes are about **curiosity**: traveling across a legible map, discovering
  culturally specific scenery and usable props, exploring interiors and roads, and occasionally
  fighting with reliable charter magic. Play should mix permissive flow with deliberate reading
  when a real commitment deserves it.
- Combat is meaningful and **medium-infrequent**, not the filler between locations. It should be
  only medium-lethal before the capital. Death feels fair when the game clearly showed danger and
  the player wasted time, ignored an escape, or failed to use the opportunity to author a better
  spell—not when an opaque enemy or surprise damage erased them.
- Because wild resolution contains uncertainty, deterministic play must supply mastery. Enemies
  have learnable attack patterns, ranges, intents, rhythms, defenses, weaknesses, terrain
  preferences, and counters. Inspection and prior encounters teach these facts; telegraphs are
  generous; actual behavior follows them unless changed state explains the divergence.
- Retreat, surrender, bribery, disguise, theft, avoidance, social leverage, transformation, and
  victory by force are all strongly viable. Do not quietly make killing the universal efficient
  answer.
- Inventory is extremely permissive. Prefer generous or effectively unbounded carrying,
  automatic stacking, concise sorting, protection for treasured objects, and low-friction use over
  slot puzzles, encumbrance, durability, identification chores, or frequent comparison cleanup.
  Scarcity belongs in desirable consumable spell components and distinctive gear, not in making
  the player manage a backpack.
- Generic rat and wolf fights are explicitly outside the content target. A fight earns its place
  by teaching a specific enemy, culture, terrain interaction, relationship pressure, or useful
  material.

### Power, progression, and wild magic

- A typical route should offer a worthwhile fresh authored cast at least every three minutes.
  This is a cadence target, not a cooldown: dense circumstances and player taste may produce more.
- Thirty seconds is the maximum acceptable wait for an excellent local-model cast. The current
  sprint uses Gemini to judge resolution quality; later local-model tuning must meet the same
  experiential ceiling through routing, context control, progress presentation, and instant
  charter/echo alternatives.
- Wild magic should resolve **very strongly**. “Yes, at a price” may shortcut or solve a major
  problem and may exact a devastating durable cost, including something on the scale of 15 maximum
  HP. Prefer an audacious accepted transformation with legible consequences over timid numerical
  effects or routine refusal. Exact severity balance can follow once the power fantasy is real.
- Player control over cost category and the frequency of beyond-scene consequences are both
  medium: preparation, wording, chosen components, and casting performance may bias them without
  making the result deterministic. Costs must remain causally legible and must not invalidate the
  premise of using powerful magic without being crippled every time.
- Strong resolutions are the primary feeling of becoming a sorcerer. By Foothold, a player should
  be able to deploy very strong wild magic without every cast demanding a crippling price, while
  charter forms retain the reliable tactical floor.
- Within-run stat growth is a normal recurring progression lane. Rare culturally specific items
  may become permanent stat growth when deliberately consumed as part of an authored spell; this
  must use general item/material, cost, and stat-change consequences rather than item-id recipes.
- Body swap should be materially transformative in a successful run. Bodies change physical
  stats, inventory context, access, identity, and tactics while the soul retains its appropriate
  continuity.
- A player may comfortably gather roughly **ten followers**. Use autonomous behavior, formations
  or standing orders, group movement, concise status summaries, and low-maintenance recovery so a
  following feels like social power rather than ten inventories and ten turn menus. The player
  chooses whether the resulting life is broad, specialized, socially embedded, scarred, or mixed.

### Mischief, relationships, and response

- Temporary false identities should be relatively easy to establish, and keeping two identities
  unlinked should also be achievable with competent play. The Empire is dangerous because its
  evidence and institutions are real, not because it receives hidden omniscience.
- Deep friends do not automatically pierce every body change. Soul recognition may require
  evidence such as a shared memory. The player receives a clear log message naming who witnessed a
  deed and which identity they attributed it to.
- Physical objects, testimony, magical signatures, shared memories, and paperwork are equally
  valid evidence. Let the player get away with audacious deceit if they execute it well. If they
  slip, the resulting institutional and human consequences should be causal, legible, and capable
  of becoming emotionally uncomfortable without moralizing through hidden punishment.
- NPCs should approach or interrupt reasonably often. Follower devotion, departure, and betrayal
  are reasonably predictable from known wants, bonds, memories, and circumstances. Relationships
  may crystallize around a few memorable acts or accumulate gradually, depending on the people.
- A return may arrive almost immediately or much later according to its fiction. The player must
  have good affordances to track promises, relationships, identities, rumors, and pressures.
  Journals may be explicit about wants and possible satisfaction; preserve mystery only where it
  creates curiosity without hiding the next actionable step.
- Conversation is a major portion of playtime, not merely a vending interface. Keep individual
  exchanges purposeful and state-bearing even when their writing is leisurely.

### Campaign, persistence, and slice scope

- Components and distinctive gear are the economy's recurring temptation. Scarcity should force
  meaningful choices reasonably often because components are consumed in spells, while avoiding
  survival chores and hard-locking essential routes.
- A foothold means a real safe haven and potentially a growing following. The War movement should
  support creative combinations of relationships, spells, routes, identity, promises, sabotage,
  liberation, and transformed places rather than generated errands.
- Force, infiltration, and coalition/prophecy routes into the capital are legibly discoverable and
  roughly equal in difficulty. One spectacular spell may shortcut a large part of the campaign,
  but doing so should be difficult and should leave commensurate state for the world to answer.
- After Odran dies, do not stage a symmetrical debate about the value of imperial order. The people
  eager to approach the sorcerer are mostly those expressing gratitude for freedom; those who
  valued the old peace are generally absent, wary, grieving, or recorded indirectly. The chronicle
  can retain the world's full truth without forcing a post-victory lecture.
- **Classic mode:** saving is suspension only. The single current save is consumed/locked when
  loaded, cannot be used to retry outcomes, and is deleted on death. Death is permanent.
- **Roleplay mode:** the same simulation and difficulty, with ordinary freely created and loaded
  saves. It replaces the previously planned checkpoint-restoration mode. Delete checkpoint
  snapshots, restoration counters, checkpoint UI, tests, and branches rather than retaining them
  beside Roleplay mode. Neither mode has meta-progression.
- After death, one input starts a fresh world immediately. The chronicle should celebrate the
  authored story, report what the world believed, and confront unresolved promises and
  relationships—all from authoritative state.
- The full-density target transect is now **Marble Containment Yard → Hollowmere Margin → Brall →
  Vigovian Capital**. Brall is the first major old kingdom to receive slice density. Its memorable
  cultural mechanism is warm, collaborative boasting about other people's deeds: witnessed acts
  become increasingly extravagant shared tales without turning every Brall character into a
  joke.
- The smallest complete Sorcerer is a game where casting is powerful, fun, and rewarding; quests
  arise organically from wants, claims, promises, and places; and relationships become meaningful
  mechanics. When scope must contract, protect those three outcomes and the deterministic
  curiosity/tactical floor that supports them.

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
5. **Prove it:** run focused tests, a manual live-Gemini CLI episode, save/replay when durable, and
   the named feel check. Run the full suite at each stable checkpoint and before declaring a phase
   substantially met. Use mock/replay episodes as regression coverage, not as the feel check.
6. **Review the diff as design:** list new public types, durable fields, consequence types,
   commands, flags, and provider calls. Each needs an explicit justification. Report what was
   removed and which existing systems gained a new reader.
7. **Update durable docs:** architecture/subsystem docs and this plan's status in the same change.

If a package reveals that its proposed representation is wrong, amend the plan, delete the failed
representation, and continue from the simpler design. Never stack a compatibility layer on an
unreleased implementation.

---

## Phase 0 — Converge the current hotspots without changing the game

Goal: create room for the roadmap without trading the existing monoliths for dozens of tiny
abstractions. The completed characterization work was behavior-preserving; remaining cleanup may
break pre-release contracts and delete obsolete behavior when that directly enables the gameplay
phases.

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
  internal payload records for complicated handlers. If the serialized envelope is needlessly
  tangled, replace it cleanly and regenerate current fixtures; do not retain dual formats for
  pre-release compatibility. Do not big-bang rewrite every dictionary merely for type purity.
- Centralize compound packet execution behind one internal transaction runner. Child mutations
  still use the injected shared consequence sink; this is not a second public apply point.
- Move subject/object grammar and visibility filtering to one shared narration helper used by
  handler families. It formats already-decided truth; it does not decide rules.
- Delete migrated methods from the original facade immediately. Do not leave forwarding methods
  or compatibility aliases; no public save contract exists yet.

Migrate incrementally, never as a single rewrite: add shared context/registry under
characterization; move one simple family; remove its old methods; run focused and full tests; then
repeat. Move compound interaction/service/promise families last, after primitive child handlers are
stable. Every intermediate commit must compile and preserve the public apply facade.

**Tests:** all current dispatch/alias cases that remain intentional; each family directly; nested
child rejection; exception rollback; invalid resulting state; visibility; current-format
save/replay behavior.

**Exit:** adding a consequence touches its family handler and the authoritative type/metadata
registry, not a 7,600-line switch. The guard/apply point, serialized contract, deltas, and behavior
remain stable. No handler family becomes a dumping ground for unrelated features.

### 0.3 Make `GameSession` an orchestrator again

**Status: in progress — 2026-07-13 (commit `fbabccb`).** First increment done: `GameSession` is a
partial class whose base file (`GameSession.cs`, ~928 lines) is now the orchestrator — public
surface, `ExecuteAsync` command switch, `View`/`Observation`, persistence/run-lifecycle, the
post-action pipeline, and shared helpers. Its three large accumulated policy regions moved to
cohesive partials: `GameSession.Magic.cs` (pending casts, charter, echoes), `GameSession.Dialogue.cs`
(dialogue turn + proposal application), `GameSession.Claims.cs` (claim intake/binding). Same
partial-class technique and rationale as 0.2 — pure relocation, behavior identical, 928 tests green,
`ExecuteAsync` untouched. **Remaining:** `Dialogue` (2030) and `Claims` (1491) are still >1000
lines; the plan's true collaborator extraction below (dialogue turn orchestrator / proposal applier
/ claim intake system with the state each owns teased out) is the next step now that each policy has
a legible home. Making the post-action pipeline an explicit named sequence with tests is also
pending.

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
  Classic/Roleplay save authority, and immediate-restart hooks.
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

**Status: done — 2026-07-13 (commits `870ed6d`, `c228400`).** Both systems already had the
planning/applying separation this package targets — `InteractionSystem` routes every change through
`_engine.ApplyConsequence`, and `PromiseRealizationSystem` selects/builds a plan then applies it via
the injected `_applyConsequence` sink under snapshot rollback — so the remaining work was cohesive
boundaries. Both are now partial classes: `PromiseRealizationSystem` (3317→863 base) split into
`.Realizers.cs` and `.Helpers.cs`; `InteractionSystem` (2797→621 base) split into `.Verbs.cs`,
`.Resolution.cs`, `.Objectives.cs`. Behavior identical, 928 tests green. Deeper pure-planner
extraction (item/person/site/route/service/threat) remains optional per this package's "only where
they share enough policy to be independently testable."

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

**Phase 0 exit gate:** full suite and golden transcripts are behaviorally stable; current-format
save/replay works; the four hotspots have cohesive boundaries; total production code need not shrink
dramatically, but duplicated branches and helpers do. The implementer can point to one place for
command routing, one apply point, one claim lifecycle, one promise-planning lifecycle, and one
message-visibility policy.

> **Substantially met — 2026-07-13.** The full suite (928) is green throughout and save/replay is
> unchanged (`EpisodeRunnerTests`/`PersistenceTests` all pass). The four hotspots now have cohesive
> boundaries (base files: applier 195, `GameSession` 928, `PromiseRealizationSystem` 863,
> `InteractionSystem` 621, each with family partials). One apply point (`GameEngine.ApplyConsequence`
> → guard → `FamilyDispatch` registry), one claim lifecycle (`GameSession.Claims.cs`), one
> promise-planning lifecycle (`PromiseRealizationSystem` select/build/apply), and one
> message-visibility policy (`StateDelta.IsPlayerVisible` + the applier's shared narration helper)
> are each locatable. Deferred to future increments: true collaborator objects (vs. partials) for
> `GameSession`, intra-family subdivision of the remaining >1000-line partials, and 0.5 test
> re-homing beyond the new `ConsequenceCatalogTests`. These are refinements, not blockers for
> Phase 1.

---

## Phase 1 — Close the current roadmap program through the new seams

Goal: finish the known top gaps before adding another gameplay category. This is
[ROADMAP_TO_1_0.md](ROADMAP_TO_1_0.md) Milestone 0.

### 1.1 One visibility and attribution policy

**Status: core done — 2026-07-13 (commit `b3bba03`).** `PerceptionSystem.ClassifyEffectWitnesses`
is the single policy — one pass classifying each witness as saw-actor / saw-effect / both / neither
using the shared line-of-sight, range, and `StatusRegistry` concealment rules, returning structured
`WitnessObservation` rows (including the observed body id as the identity-model seed). Deed capture
(`PlanDeedCapture`) and suspicion attribution (`PlanEffectSuspicion`) both project from it, replacing
their two parallel witness passes; behavior-preserving, 932 tests green. `VisibilityAttributionTests`
pins the four-way classification, the suspicion projection, the debug readout, and witness-named
messages. Done since: the structured classification is surfaced in `AgentObservation(debug)`
(`WitnessDebugCard`, commit `f06e976`), and deed reaction messages now name the carrier who noticed
instead of "someone" (commit `8c76062`, converging with the tone directive). Hostile notice
(`AiSystem`) already shares the concealment rule via `CanPerceiveSubject`. **Remaining (minor):**
route the resolver relation cards (`EngineViewBuilder`) through the same projection; the body-swap
attribution variant of the proof lands with the Phase 4 institutional-identity model (the
`WitnessObservation.WitnessEntityId` seed is in place for it).

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
- For the sprint, measure every live spell pass with Gemini 3.5 Flash at medium effort. Later local
  model acceptance is p95 at or below the thirty-second experiential ceiling; first optimize
  routing, prompt/context size, queueing, and presentation rather than weakening resolutions.
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
  - _Status (893264d):_ the heat-gated response ladder now has three rungs plus a graceful
    blocked-memo -- `empire_patrol` (heat >= 3, spawns a hunter), `empire_warrant` (heat >= 5,
    legend pressure), and `empire_cordon` (heat >= 7, commits two capital defenses, reinforcing the
    organic capital approach). Each spends a finite resource, names its cause via a Censorate memo,
    shares one 8-turn cooldown, and degrades gracefully when the resource is short. Remaining: more
    authored templates (named hunters, bribery/redirection routes) and richer materialization.
  - _Status (2026-07-14, FREE_FOLK_MOVEMENT S0):_ owner-directed pacing rework. Heat is now
    **report-borne** (`empire_report` scheduled events with living witness carriers; overdue
    audits for silent imperial losses), responders arrive at map-edge road tiles outside the
    player's sight on a 12-turn fuse the journal forecasts, patrols/warrants regenerate on a
    slow turn-modulo cadence, and the warrant rung dispatches a **named witchhunter** with a
    road-talk trace rumor ahead of arrival. `ImperialResponsePacingTests` pins the contract;
    see docs/FREE_FOLK_MOVEMENT.md "The marble answers slowly."

### 2.3 Progression as a capability portfolio

- Use existing lanes: charter forms, named echoes, equipment/foci/reagents, treasured items,
  bodies, followers, services, bonds, faction posture, routes, promises, scars, and typed stat
  changes.
- Add a read-only capability summary for telemetry/debug and a qualitative player journal/history
  surface. Do not add XP, levels, or a new progression currency.
- Tune the reference route so at least two capability lanes materially change problem-solving by
  the end of Foothold.
- Make within-run Vigor, Attunement, and Composure growth a meaningful recurring lane. Author rare
  items whose semantic/material traits make them tempting spell components; when a player
  deliberately spends one in a fitting authored cast, the resolver may propose an ordinary durable
  stat change as part of the validated cost/outcome packet. Never key this to a specific item name
  or phrase.
- Body swaps must be capable of changing tactics, access, risk, physical capacity, carried
  inventory, and institutional identity—not merely portrait or control pointer.
- Tune follower control for about ten simultaneous allies: group-follow and transition behavior,
  simple stance/formation orders, autonomous action, obstruction handling, and a concise shared
  view. Do not add per-follower equipment chores or a second party-combat ruleset.
- Echo fatigue/drift protects fresh authorship. Charter forms cover routine reliability. Neither
  becomes a second spell game.

### 2.4 One economy serving travel, tactics, and social play

- Keep explicit deterministic buy/sell/service commands and one price policy.
- Add recurring sinks by composing existing state: transport, rest/lodging, information, bribery
  or credential access, spell components/equipment, and costly recovery. Components and distinctive
  gear should be the strongest recurring temptations.
- Merchants and providers remain normal NPCs with stock/services/wants. Do not add separate shop,
  inn, transport, or bribery systems when offers plus consequences suffice.
- Money, items, promises, memories, standing, and services may all be payment media when the
  existing consequence grammar can validate them. Do not ask a model to infer trade intent.
- Carrying capacity is not the scarcity lever. Use permissive capacity, automatic stacking and
  sorting, low-friction pickup/use, and treasured-item protection. Create choices through which
  consumable materials to spend on magic, trade, or keep for later semantic leverage.
  - _Status (31ebe59):_ the economy system is complete and graceful -- buy/sell (`ApplyExecuteTrade`)
    are the sources, services/purchases the sinks (`PayServiceCost` consumes gold transactionally),
    and a payment the sorcerer cannot afford rolls the request back rather than deadlocking a seed.
    Added the missing "measured" half: a read-only `EconomySummaryView` (gold, sellable stacks, paid
    services in reach + total cost) on the debug/telemetry surface. Remaining is content/tuning:
    authoring enough recurring service sinks with meaningful prices across a run that gold pressure
    is felt, and a run-scale no-deadlock replay proof.

### 2.5 Persistence modes as one simulation

- Keep one durable run-mode value: **classic** or **roleplay**. Engine rules, content, RNG, economy,
  enemies, difficulty, and victory remain identical; only save authority differs.
- Classic has one suspension save. Saving exits the live run. Loading consumes or locks that save
  before play continues so copying/reloading is not an in-game retry path. Death ends the run and
  deletes its suspension save.
- Roleplay exposes ordinary manual save and load with multiple slots/autosave as product UX allows.
  Loading restores the entire authoritative snapshot; there is no partial rewind, remembered
  death, imperial foreknowledge, restoration tax, or altered chronicle.
- Delete the checkpoint-restoration implementation, `GameStateSnapshot` rest checkpoint,
  restoration count, checkpoint-specific tests/UI/messages, and any `RunMode` aliases. Rename the
  mode cleanly; do not deserialize or migrate unreleased checkpoint saves.
- Both modes use the same serializer and session factory. Do not fork commands, action logic,
  balance, content, or world state. Chronicles record the chosen mode without shaming or changing
  rewards.

_Owner decision superseding the 2026-07-13 checkpoint experiment:_ commits `21ba8ec`/`9b99a2e`/
`d670d7d` proved that the simulation could restore and count snapshot restorations, but settlement
restoration is no longer the desired product. Treat that code as deletion inventory, not a
compatibility obligation.

### 2.6 Death, victory, chronicle, restart

- Select imperial/wild/other death treatment from authoritative killer, faction, region, and
  effect provenance. Deterministic templates always work; provider prose is optional enrichment.
  - _Status (410516c, 620862d, 9a0864b):_ the death-treatment loop is closed. Killer provenance is
    recorded at the moment the body is struck (`GameState.LastControlledDamageProvenance`:
    imperial/wild/mortal); a shared `DeathTreatment` helper maps it to a treatment consumed in three
    places -- `RunChronicle.Treatment`, the defeat narration (`DeathTreatment.Disposition`, read in
    the killer's register), and the cross-run memorial (an imperial death leaves a Censorate
    incident-marker, a wild death an unquiet stone, else the weathered memorial).
    `DeathTreatmentTests` pins all of it. Delete pre-`Treatment` fallback reads and regenerate old
    fixtures under the pre-EA compatibility policy. Restart already exists through the session
    factory (`CreateImperialEncounter` / Godot `StartNewRun`); make it a one-input path from every
    death surface.
- Assemble chronicles from deeds/rumors, unrealized bound promises, deepest bonds, capability
  portfolio, route, faction posture, run conclusion, what the world believed, and important
  unresolved consequences. Do not accumulate a parallel narrative summary during play.
- Restore or start a new run through the same scenario/session factory. Cross-run memorials seed
  rumor/canon texture only; no power crosses.

**Phase 2 tests and proofs:**

- Each of the three capital routes completes under replay.
- Reference classic run wins in 6–8 hours; death deletes its suspension save and one input starts a
  fresh seed. A Roleplay run can save, reload, and continue to victory.
- Classic save/exit/resume and Roleplay save/load work during every derived movement and with
  pending materialized provider work.
- Economy has measured sources and recurring sinks; no required purchase can deadlock a seed.
- Chronicle names the actual route and omits facts the world/player never established.
- One input starts a different seed and surfaces a meaningful difference in twenty minutes.

**Phase 2 exit gate:** the game is a complete run-shaped object. It can still be mechanically thin,
but no remaining work is required merely to connect start, growth, campaign, capital, ending, and
restart.

---

## Phase 3 — Make the deterministic ten minutes excellent

Goal: Sorcerer remains a compelling turn-based game with providers disabled. Curiosity, travel,
specific scenery, usable props, and reliable charter magic carry ordinary play. The model should
turn good situations into singular ones, not rescue repetitive movement and combat.

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
- Every archetype needs a learnable tactical identity: a recognizable approach pattern, at least
  one dangerous commitment, one defense or immunity, one meaningful weakness/counter, and one
  relationship with terrain, allies, props, or timing. Do not spend slice slots on generic rats,
  wolves, or interchangeable melee sacks.

### 3.2 Intent telegraphing through the shared view

- Compute the next tactical intention deterministically from the same candidate evaluation AI will
  use. Expose a player-safe summary through `GameView`; expose scores/reasons in debug observation.
- Intent is a forecast, not a reserved action. A changed board may change it when the actor acts.
- Use a small vocabulary with specific targets or places: advance, flank, brace, shoot, cast,
  protect, investigate, flee, call help, interact.
- Godot renders glyph/color/motion hints; CLI JSON exposes structured intent. Renderers do not
  recompute it.
- Do not reveal hidden actors or secret goals through intent views.
- `inspect` and the equivalent GUI surface expose currently knowable attack ranges, charter forms,
  resistances, weaknesses, status interactions, and observed behavior. First contact may show
  obvious anatomy/equipment; surviving or observing an action can add remembered knowledge. This
  is player mastery, not a debug-only bestiary.
- Telegraph severe actions early enough that the player can reposition, retreat, disrupt, use a
  known counter, or spend the turn authoring wild magic. If the forecast changes, the result must
  name the board change that caused it.

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

**Status: first slice implemented 2026-07-15.** `content/encounters/initial_encounters.json`
authors four ingredients (guarded cache, keeper-holds-it, restricted threshold, rival claimant);
`EncounterAssembler` (pure, stable-seeded) picks by stakes tier — promise salience + regional
imperial presence + faction pressure, tier 0 = simple find — and the item-promise realizer plus an
opt-in ambient pass in zone generation stage the casts through ordinary spawn/want consequences.
The invariant held: no `EncounterLedger`, no scripted room graph; completion falls out of
`ObjectiveProgressSystem`/promise/want facts, and every staging is resolvable by force (corpse
looting voids inventory protection), stealth (shared concealment/LOS rules), or persuasion
(cast wants/stakes text surfaces in dialogue context). One new AI behavior: a `"guard"` policy
that holds an anchor and breaks pursuit beyond a short leash.

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

Combat cadence is medium-infrequent: travel and exploration should contain many useful discoveries,
interactions, and navigational decisions without a fight. Do not insert an encounter merely because
a road has been quiet. Routine fights should resolve briskly once the player reads the pattern;
longer fights earn their length through changing terrain, social stakes, reinforcements, or an
elite interaction.

### 3.5 Reliable tempo around authored magic

- Charter forms cover weak, precise, frequent tactical needs and interact with witnessing as
  licensed-looking magic.
- Echoes replay player-authored mechanics through current validation and target binding. Implement
  seeded drift only after fatigue and usage telemetry show echoes remain worth keeping.
- Targeting, item use, movement, surrender, fleeing, and charter/echo actions remain instant.
- Fresh wild casts should be tempting whenever curiosity or danger produces a rich situation,
  averaging at least one worthwhile authored opportunity per three minutes. They need not be
  optimal every turn. Measure opportunity cadence, fresh-cast variety, resolution strength, and
  echo/charter share rather than enforcing an arbitrary cooldown.
- Prefer a strong, specific resolution that substantially changes the current problem. A major
  problem may be solved outright if the validated price and resulting world state are equally
  substantial. Do not make routine crippling costs the price of feeling powerful.
- Apply casting performance at the engine boundary using existing consequence severity and
  complication families. A poor score creates legible risk, not random unrelated punishment;
  skipping remains neutral.

### 3.6 Recovery, loss, and retreat

- Tune HP, mana, rest, services, consumables, travel, and money as one recovery economy.
- Retreat, surrender, concealment, bribery, and route escape must be real responses before lethal
  difficulty rises.
- Enemies telegraph severe actions; wild costs name what they took; provider failure never becomes
  danger.
- Before the capital, tune ordinary combat to be only medium-lethal. A lethal sequence must expose
  enough warning and counterplay that the lost turns are retrospectively understandable. Avoid
  undodgeable burst, opaque immunity, unavoidable pursuit, or attrition deaths caused chiefly by
  travel filler.
- Avoid hunger, durability chores, inventory busywork, or attrition systems whose main function is
  extending the run.

**Phase 3 tests and proofs:**

- Provider-off ten-minute tests remain tactically varied across at least five encounter grammars.
- AI intent and actual action agree when state does not change and diverge explainably when it does.
- Each archetype has a systemic counter besides raw damage.
- A player who has fought or studied an archetype can accurately predict its core attack, range,
  dangerous tell, defense, and at least one counter without debug state.
- Encounter frequency leaves ten-minute routes where exploration and scenery stay interesting
  without combat, while combat samples remain consequential when they occur.
- Noncombat resolutions update the same authoritative facts used by combat outcomes.
- Terrain/status/AI rules are seed-stable, saveable, replayable, and represented in both views.
- No per-archetype class or per-encounter completion handler enters the engine.

**Phase 3 exit gate:** curiosity, tactical mastery, legibility, and pace score at least 4/5 in the
reference slice without relying on a live model. Adding a new archetype is predominantly data
authoring, and the player can explain why a death was avoidable.

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
- Ordinary guards should be relatively easy to fool with a coherent temporary identity. Maintaining
  two identities without a Censorate link should remain a practical skill path; difficulty comes
  from contradictory evidence, repeated signatures, careless witnesses, and use of the wrong
  credentials, not hidden detection bonuses.

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
- A deep ally's recognition across bodies is evidence-based rather than automatic. Shared memories,
  secrets, promises, mannerisms, or a deliberately offered proof may establish soul continuity;
  absent evidence, even intimacy may begin in uncertainty.

### 4.5 Warrants and pressure become physical

- Warrants/notices are readable fixture/item entities generated from identity-attributed state and
  the recurring clerk voice.
- Patrols receive the identity description they are seeking, with uncertainty where appropriate.
- Wearing a wanted identity changes institutional reaction; changing bodies can misdirect it; deep
  allies and uncanny systems may still know the soul.
- UI and journal describe what the Empire believes, what it is uncertain about, and why—never a
  raw stealth percentage.
- Every witnessed suspicious act writes one concise player-visible log message naming the witness
  when known, what they saw, and the presented identity/body they attributed it to. Later
  correlation names the evidence that changed the file.

**Phase 4 tests and proofs:**

- Public, concealed, disguised, and body-swapped versions of the same deed attribute differently.
- Soul legend and soul-knowing bonds remain stable across identities.
- Evidence planting/removal changes a later bounded investigation and warrant target.
- Identity linking spends a faction resource, names its evidence, and is replay deterministic.
- A player can frame an NPC or stale identity without a bespoke quest or spell phrase.
- A well-executed deception can escape institutional punishment entirely; a failure produces
  traceable institutional and interpersonal consequences rather than an omniscient correction.
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

- During ordinary play, aim for a meaningful world or relationship return at least every three
  minutes: a changed person/place, approach, rumor, promise movement, response, access change,
  remembered act, or actionable discovery—not a generic notification.
- A significant public act should usually receive one immediate/local answer and a plausible later
  answer. The later answer may arrive in the next scene or much later when route, carrier, urgency,
  and salience support it; do not force every consequence into the same one-to-three-transition
  window.
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
- Followers act autonomously under a small set of player-chosen group/stance orders, travel as a
  coherent following, and expose legible reasons for aid, refusal, fear, departure, or betrayal.
  Support roughly ten followers without requiring individual inventory or turn micromanagement.
- NPC initiative should approach or interrupt the player reasonably often when a known want,
  relationship, rumor, promise, or danger gives a concrete reason. Do not manufacture interruption
  cadence with content-free greetings.
- Conversation may occupy a major share of playtime. It should remain grounded in current wants,
  knowledge boundaries, memories, available services/actions, and the physical scene so extended
  talk keeps creating or clarifying playable state.

### 5.4 Promise delivery becomes ordinary world content

- Keep one realization lifecycle for NPC, item, site/fixture, route, service, threat/claimant,
  stock, memory/canon, and scheduled consequence archetypes.
- A promise plan reserves or finds real geography and produces ordinary entities/components.
- Realized content must have at least one tactical/social/material affordance and one possible
  onward handoff. A named rock with no use is not a delivery.
- Completion updates the source claim/want/memory/faction state through consequences. No separate
  quest completion record.
- The journal exposes evocative but actionable uncertainty. Debug shows the binding exactly.
- The ordinary journal provides compact tracking for active promises, notable relationships,
  presented identities, rumors, faction pressures, and known wants. It may explicitly suggest
  plausible satisfactions when the character has evidence; mystery may hide outcome or truth, but
  not every available next step.

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
- Let preparation, chosen components, wording, and casting performance bias cost families without
  turning cost selection into a guaranteed menu. Strong casts should commonly be worth making;
  reserve crippling prices for commensurately audacious outcomes rather than normal power use.

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

For the reference run, a foothold must become a usable safe haven and may become the physical home
of the player's following. War content should be assembled creatively from this table: sabotage a
route by transforming its material, turn an official through a remembered favor, boast a deed into
a Bralli coalition, steal a supply's semantic ingredient for a spell, liberate a place, feed the
Censorate a false identity, or bind a promised access route. Do not generate interchangeable
errands with faction nouns substituted.

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

Goal: make **Marble Containment Yard → Hollowmere Margin → Brall → Vigovian Capital** feel like a
finished game in miniature. Hollowmere proves frontier density; Brall proves that the same machine
can carry a major old kingdom with a radically memorable social/magical voice. Do not solve
integration problems with reference-seed special cases.

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

### 6.3 Brall proves cultural mechanics, not a palette swap

- Add a connected Bralli route with at least one hold or public hall, one road/harbor or smaller
  site, meaningful interiors, bone/scrimshaw props, component scarcity, charter bone-working, and
  imperial occupation pressure. The trip must feel like entering another major kingdom, not a
  renamed Hollowmere district.
- Bralli tall tales are warm collaborative boasting about **other people's** deeds. A witnessed act
  can become a rumor whose retellings exaggerate concrete details, recruit a listener, alter
  hospitality or danger, satisfy a teller's want, or furnish spell language. Distortion retains
  provenance and never becomes random joke text.
- At least one player deed receives multiple Bralli retellings and then returns as an ordinary
  relationship, service, follower, faction, encounter, or access consequence. Implement this with
  the shared witness → deed → rumor → carrier → response chain, not a `BrallStorySystem`.
- Give Brall-specific enemies and officials learnable bone/charter tactics, weaknesses, and
  intentions. Do not fill its roads with generic rats, wolves, or culture-neutral bandits.
- Let the player form a safe haven, following, or coalition connection in Brall. The force,
  infiltration, and coalition/prophecy routes should each gain a different useful possibility
  there without making Brall a mandatory quest chapter.

### 6.4 The capital proves scale, not a finale exception

- Outer pressure, Censor Gate, Archive Quarter, Inner Court, palace/archive, and throne form a
  campaign of ordinary thresholds, actors, evidence, faction resources, services, claims, and
  routes.
- Prior deeds, identities, allies, promises, stolen/earned access, and depleted defenses arrive as
  real advantages or liabilities.
- At least one earlier NPC/object/rumor/cost returns inside the capital.
- Odran acts, takes damage, can be transformed or otherwise affected, and dies under ordinary
  entity/consequence rules. Run lifecycle observes the result.
- Capital approach surfaces force, infiltration, and coalition/prophecy affordances legibly and
  tunes them to roughly equal difficulty. A spectacular authored spell may bypass a major
  threshold, but not through a finale-only verb or automatic easy win.
- Post-victory approaches foreground gratitude from those who wanted freedom. Imperial loyalists
  and people who valued order may remain absent, wary, grieving, or present in the chronicle; do
  not force a symmetrical debate scene after the kill.

### 6.5 Tune the connective tissue

- Remove empty travel, repeated low-value combat, duplicate narration, dead economy sinks,
  excessive model calls, and objective chains that do not change decisions.
- Use faster transport and compressed geography before adding content to solve pacing.
- Track turns and real time between authored action and response, capability gains, pressure
  escalations, healing/rest, money source/sink, wild casts, and route progress.
- Balance origins as different risk/expression profiles, including a forgiving and cruel start,
  not as classes or a global difficulty slider.
- During the sprint, repeatedly run representative legs with Gemini 3.5 Flash/medium. Before this
  phase exits, also run the same route with provider disabled and the floor local model.
- Measure meaningful discovery/response and authored-cast opportunities in real time. Ordinary
  play should rarely go more than three minutes without one, while combat remains
  medium-infrequent.

### 6.6 Prove generality outside the slice

- Run a generated-thin world tour through at least two cultures beyond Hollowmere and Brall.
- Confirm that encounter grammar, NPC wants, claims/promises, region props, services, rumors,
  identity, faction responses, and presentation views work without code changes.
- If another region needs code, fix the shared content template or system seam. Do not begin
  densifying that region in this phase.

**Phase 6 release gates:**

1. A human can start cold, win or die, read a faithful chronicle, and restart without developer
   intervention.
2. Provider-off play is a coherent roguelike; live magic makes it singular.
3. Floor-model magic regularly uses local objects and prior facts within measured budgets.
4. A documentation-blind player reaches Hollowmere and Brall, can explain how each place changed
   play, why the world reacted, and retains at least two hooks.
5. Feel scores reach at least 4/5 for authorship, wonder, mischief, tactics, responsiveness,
   legibility, and pace across multiple testers.
6. Two runs differ in repertoire, material, relationships, identity/scars, route, and chronicle.
7. A tester voluntarily starts a second run and finds an early meaningful difference.
8. A Bralli retelling of a witnessed deed returns through a real carrier and changes an ordinary
   gameplay decision without culture-specific engine code.

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
- Make exploration inviting at a glance: discovered routes, inspectable scenery, usable props,
  interiors, travel choices, and nearby cultural texture need stronger hierarchy than decorative
  clutter. The atlas remembers meaningful places and known return hooks without revealing the
  whole generated map.
- The follower surface summarizes roughly ten allies by stance, health/risk, distance, and urgent
  desire; group commands are prominent and individual micromanagement is optional.
- Inventory presentation assumes permissive capacity: default to consolidated stacks, semantic
  search/filtering, protected treasures, and “use as spell material” affordances rather than a
  capacity-management dashboard.

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
  rest/safe haven, victory, death, chronicle, and restart coherent presentation.
- Present **Classic** plainly as permadeath with save-and-exit suspension only. Present
  **Roleplay** as the same game with free save/load. Delete every checkpoint-recovery label and
  screen. Classic death removes Continue for that run and places “Begin a new world” one input
  away; Roleplay retains its load surface without special death-rewind fiction.
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

### 8.5 Save safety and public compatibility

- Autosave at bounded safe points; save-on-quit; rotating backups; atomic replace; corruption
  detection and recovery UI.
- Before public Early Access, replace schemas cleanly and regenerate fixtures; do not ship
  compatibility scaffolding. Once a public EA build creates a save promise, versioned migrations
  are explicit and tested from checked-in golden public saves, with no wipes.
- Bound save size: archive/prune audit detail appropriately while retaining authoritative ledgers,
  provider materializations needed for replay, and the chronicle.
- Classic suspension and Roleplay manual saves use the same authoritative serializer. Enforce their
  different load authority outside the simulation rather than maintaining different snapshot
  formats.
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
- Publish exact supported providers/model floor, expected run length, Classic/Roleplay behavior,
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
- Delete superseded aliases/helpers/adapters immediately before EA. After EA, keep migrations at a
  narrow serialization boundary and delete them as soon as the public support window permits; do
  not let old schemas infect current gameplay code.
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
- [ ] Focused and full tests pass; a manual live-Gemini CLI episode exercises discovery and action.
- [ ] A first-person feel check answers the package's product question.
- [ ] Superseded branches/helpers/messages are deleted; before public EA, no compatibility path
      remains.
- [ ] New concepts and hotspot growth are justified in the change-budget report.
- [ ] Durable docs change in the same patch.

## Execution order and continuation rule

| Order | Phase | Roadmap owner | Status at this plan's writing |
|---:|---|---|---|
| 0 | Converge current hotspots | prerequisite protecting every milestone | 0.1–0.4 done: four hotspots have cohesive boundaries, 928 tests green (see phase notes) |
| 1 | Close current roadmap program | Milestone 0 | 1.1 core done (one visibility policy); 1.2–1.4 next |
| 2 | Complete run structure | Milestone 1 | pending Phase 1 |
| 3 | Deterministic ten-minute game | Milestone 2 / P3 | pending Phase 2 structure |
| 4 | Mischief, identity, attribution | Milestone 2 / P3 | pending visibility policy and tactical intent |
| 5 | Response flywheel density | Milestone 2 / P2–P4 | pending shared identities/tactics |
| 6 | Vertical-slice integration | Milestone 2 gate | pending Phases 2–5 |
| 7 | Presentation | Milestone 3 | prototype long-lead work may run earlier; full pass pending slice feel |
| 8 | Productization | Milestone 4 | setup/save prototypes may run earlier; full pass pending slice feel |
| 9 | Early Access and breadth | Milestones 5–6 | pending four slice proofs |
| 10 | 1.0 release candidate | Milestone 7 | pending breadth freeze |

Phase 0.1–0.4 and the core of 1.1 are already substantially complete. Resume from live repository
state, close only the remaining structural issue that directly blocks gameplay, and continue
through Phases 1–7 without waiting at boundaries. Favor a playable end-to-end improvement over
exhaustively perfecting a completed refactor. Do not begin broad content authoring merely because a
schema exists, but do build the Hollowmere/Brall reference content needed to prove each general
system as it lands.

Do not stop for ordinary product judgment, code shape, test repair, large diff size, or a newly
necessary durable concept that passes the admission test. Apply the owner calibration, implement
the general feature, record the choice, and continue. If an external dependency truly blocks one
lane, leave precise evidence and advance every unaffected lane; never fill the gap with a fake
resolver, compatibility branch, or disconnected placeholder.

The plan succeeds when adding one good idea makes several existing systems more useful, the player
can see the resulting chain, and the code contains fewer ways for the world to become true—not
more.
