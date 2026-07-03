# Systems Vision - The Interlocking Machine

Status: active implementation 2026-07. Companion to [GRAND_VISION.md](GRAND_VISION.md), which owns
intent and always wins on it. This document sits one level below: it names the single loop all
systems serve, the hand-off discipline that makes emergence cheap, and the four unification moves
that close the weakest seams in the current interaction map. Detailed designs live in the system
docs this document points into; when a move here matures, it should graduate into those docs.

## The One Loop Everything Serves

The game is a flywheel. Every system is either a stage of it or friction on it:

> **Act -> Witness -> Record -> Circulate -> Respond -> Deliver -> new material to act on.**

- **Act.** Wild magic, dialogue, gifts, theft, rescue, travel. The player reaches into the world
  in language; the resolver translates; the engine validates and applies.
  ([CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md))
- **Witness.** Visibility gates everything. Secret / suspicious / witnessed / public / mythic is
  the single most load-bearing classifier in the game - it is what makes stealth, disguise, and
  body swap real play instead of flavor. ([EMERGENT_WORLD.md](EMERGENT_WORLD.md))
- **Record.** Deeds, claims, memories, traits, promises - the ledgers. Nothing matters unless it
  lands in a ledger, because ledgers are what future model calls read.
  ([SEMANTIC_EFFECTS.md](SEMANTIC_EFFECTS.md), [PROMISES_AND_PROPHECY.md](PROMISES_AND_PROPHECY.md))
- **Circulate.** Rumors spread, legends distill, bonds shift, faction standing moves. The
  transport layer between "what happened" and "who cares."
- **Respond.** Factions spend finite resources; NPCs recruit, drift, betray; the clerk files
  another exasperated memo. ([WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md))
- **Deliver.** Promises realize into sites, people, items, threats; worldgen honors what was
  bound; the narrator makes the reaction visible.
  ([PROMISE_PAYOFF_PLAN.md](PROMISE_PAYOFF_PLAN.md))
- **New material.** Every delivery is itself grammar - a promised blade is fuel, a realized town
  is a stage, a spawned debt collector is someone to talk to.

The lushness the game promises is not a content budget; it is this flywheel spinning fast enough
that the player cannot tell where authored ends and emergent begins. Each design question below
reduces to: *which stage of the loop does this strengthen, and does it hand off to the next stage
for free?*

## The Hand-Off Discipline

GRAND_VISION's thesis is that a new system should create more combinations than it adds
complexity. The mechanical form of that thesis:

> **Everything that changes the world speaks one typed consequence grammar, differing by
> `type`, `source`, validation policy, and timing - not by subsystem.**

A door opens the same way whether the player, a spell, an NPC's dialogue action, a promise
payoff, or a faction move requested it. Damage, terrain edits, memory writes, bond shifts,
rumor creation, and promise binding all use the same envelope; their handlers enforce different
validation rules. A witnessed deed becoming an NPC memory is just `record_memory` with deed
provenance, the same memory lane dialogue and rumors already read. When there is one grammar, every new verb is instantly available to every
source, and every new source instantly speaks every verb. Combinations multiply; complexity
adds.

The acceptance test for any new mechanic: adding `collapse_bridge` as a consequence type should
mean a spell can do it now, a prophecy can promise it later, a faction can spend for it, and an
NPC can threaten it - with zero additional wiring. Fast consequences are fast because their
`timing` is immediate; slow consequences are slow only when the consequence explicitly says so.
In practice, bridge collapse now rides the broader `transform_entity` lane for local props and
fixtures: the same typed payload can rename/rematerialize a bridge, change blocking, retag its
fixture type, alter rendering, and add interactable verbs whether it came from direct engine code,
wild magic, or generated dialogue.

## The Four Unification Moves

These are the highest-leverage missing or half-built hand-offs, in rough build order. Each obeys
the standing disciplines: the deterministic skeleton stands with zero model calls; the model
interprets and narrates, never simulates; durable change enters state only at engine apply
points.

### 1. One typed consequence grammar (finish the convergence)

There are currently four parallel "proposal -> validate -> apply" grammars: spell `IOperation`s,
dialogue `WorldConsequence`s, promise realization plans, and faction pressure expenditures.
[PROMISE_PAYOFF_PLAN.md](PROMISE_PAYOFF_PLAN.md) and [DIALOGUE_SYSTEM.md](DIALOGUE_SYSTEM.md)
already point at consolidation; this document elevates it from cleanup to architectural
commitment. Direction of travel:

- Replace the split between tactical operations and world consequences with one typed
  consequence envelope. Tactical verbs such as damage, movement, terrain edits, summoning, and
  door changes are immediate consequence families; social/world verbs such as memory, bond,
  rumor, stock, service, route, faction, trigger, and promise changes are consequence families
  with their own validation.
- A consequence carries `type`, `source`, `timing`, provenance, audit context, visibility, and a
  typed payload. `timing: immediate` is the default; `after_turn`, `world_pump`, and `deferred`
  are explicit choices, not separate architectures.
- The first immediate tactical and general-state handlers now exist in
  `WorldConsequenceApplier` (`damage`, `heal`, `restore_mana`, `adjust_actor_resource`, `move_entity`, `set_terrain`, `update_terrain`,
  `apply_status`, `remove_status`, `spawn_entity`, `spawn_item`, `spawn_fixture`, `create_promise`,
  `update_promise`, `message`, `modify_inventory`, `transfer_item`, `update_equipment`, `record_memory`, `update_bond`, `update_want`, `add_merchant_stock`, `offer_trade`, `execute_trade`, `offer_service`, `request_service`, `open_or_unlock`, `create_route`, `free_captive`, `add_tags`, `remove_tags`, `change_faction`, `update_control`, `set_controlled_entity`, `swap_souls`, `set_world_flag`,
  `update_run_status`, `set_selected_target`, `queue_background_job`, `update_background_job`,
  `schedule_event`, `update_scheduled_event`, `create_trigger`, `update_trigger`, `transform_entity`, `set_resistance`, `set_weakness`,
  `delay_incoming_damage`, `release_delayed_damage`, `accelerate_status`, `edit_memory`, `create_persistent_effect`, `update_persistent_effect`,
  `set_behavior`, `update_behavior`, `create_flow`, `update_flow`, `record_claim`, `update_claim`, `record_rumor`, `update_rumor`, `adjust_faction_standing`,
  `adjust_faction_resource`, `record_suspicion`, `update_suspicion`, `record_deed`, `update_deed`, `add_legend`, `add_canon`, and `record_world_turn`), and high-use
  combat, magic, and world-reaction operations route through
  `GameEngine.ApplyConsequence` or the same applier over `GameState`.
  Existing `IOperation` code can survive as implementation detail while the public contract
  migrates to consequences. The long-term goal is one proposal/validation/application lifecycle,
  not two grammars that bridge forever.
- Engine-owned flows use the shared apply point, not just the shared schema. Turn pumps, world
  reactions, scheduled events, travel narration, rumor propagation, and background jobs submit through
  `GameEngine.ApplyConsequence` or a caller-provided consequence sink; local appliers must be
  wrapped by `WorldConsequenceGuard` and are reserved for tests, standalone helpers, and
  deliberately detached generated-state sandboxes. Ordinary zone generation now uses that sandbox pattern for terrain texture, fixtures, curios, residents, resident stock, and
  capital actors. Destination-zone promise payoffs use the same pattern but start from the real
  global ledgers, stage zone-local entities separately, and commit global ledger changes such as
  canon/memory/rumor/scheduled-event writes together with serial/RNG updates only after successful
  application. Rejected generated-zone child consequences become hidden audit deltas and skipped
  content rather than exception paths, player-visible failure narration, orphan entities, or partial
  lore receipts.
- Public engine shortcut helpers for ordinary mutations should stay retired: status changes,
  promise creation, deed recording, and similar effects should be requested as typed consequences,
  with only narrow test/system helpers wrapping that envelope when direct setup would be noisy.
- `WorldConsequenceGuard` now enforces the shared mutation boundary: it snapshots state, restores
  on rejection, validates the resulting state after successful handlers, synthesizes a hidden
  `worldConsequenceRejected` audit when a handler returns `Applied=false` without one, rolls
  back accepted mutations that would leave the world invalid, and converts handler exceptions into
  rollback audit records with exception diagnostics. This makes state validation and failure
  signaling properties of the apply point, not courtesies each source adapter has to remember.
- Due scheduled events now deliver ordinary typed consequences at the turn pump. The payload and
  lifecycle update commit together; rejected due payloads roll back and preserve hidden rejection
  diagnostics beside `scheduledEventSkipped` rather than becoming a one-off delayed-effect
  interpreter.
- Due triggers can now deliver ordinary typed consequences too: the old status/damage/heal/message
  shortcuts remain, while `effectType: consequence` lets a ward, aura, or delayed spell submit any
  validated consequence payload through the same fire-plus-lifecycle transaction.
- Persistent combat hooks follow the same rule: shorthand thorns/venom/message effects remain,
  while `effectType: consequence` lets a hook submit a typed payload through the same
  fire-plus-consume lifecycle and rollback audit. Hooks are children of accepted base damage;
  rejected attack damage does not fire or consume them.
- World reactions apply each deed as one transactional consequence plan: rumor minting, witness
  memories, legend, faction shifts, heat/resource changes, visible narration, and `deedApplied`
  all commit together, or a rejected child rolls the plan back and leaves only a hidden audit-only
  `worldConsequenceRejected` diagnostic plus a `worldReactionSkipped` delta.
- Promise realization plans commit payoff content and the final `update_promise` status together;
  rejected handler output or status updates roll back staged payoff state and leave a hidden
  `promiseRealizationSkipped` audit. Dialogue extractors, books, services, AI plans, faction
  expenditures, and wild magic all submit consequences rather than owning private mutation helpers.
- Claim binding packets are staged too: authored claim seeds and dialogue claim promises commit
  claim, rumor, promise, and status changes together or roll back with `claimSeedSkipped` /
  `claimPromiseSkipped` audits.
- Generated dialogue now has a generic `consequence` action carrying `consequenceType`
  plus a typed payload, so local dialogue side effects can use the shared applier without adding
  another bespoke dialogue action helper.
- Wild magic has the same generic `consequence` operation, with `consequenceType`,
  optional `target` / `targetEntityId`, and `consequencePayload`, so a spell can submit already-owned
  social/world consequences through `GameEngine.ApplyConsequence` without adding a spell-only
  helper. The shared applier now honors non-immediate `timing` by scheduling the same typed
  consequence through the turn pump; explicit deferred primitives such as `scheduleEvent`,
  `createTrigger`, or persistent effects remain for broader events, repeating triggers, auras,
  wards, and hooks.
- Initial dialogue claim intake is also staged: `record_claim`, speaker `record_memory`, and the
  first visible-claim `record_rumor` commit as one intake packet before later promise, bond, canon,
  stock, service, or trade applications.
- Immediate claim applications are staged beside their status update: bond shifts, canon facts,
  merchant stock, service offers, and trade offers roll back with `claimApplicationSkipped` if the
  affordance or `update_claim` child rejects.
- Gifts are small social packets: the item transfer and gift memory commit together or roll back
  with `giftSkipped`, preserving the gift -> memory -> dialogue hand-off.
- Consumable item use is staged too: narration, heal/mana effect, and inventory spend commit
  together or roll back with `useItemSkipped`.
- Persistent combat hook records stage their fired child effect and lifecycle consumption together,
  so a thorn ward cannot damage now and fail to spend its use.
- Body swap is a single possession packet: soul swap, controllers, factions, statuses, control
  pointer, target clear, deed, and narration commit together or roll back with `possessionSkipped`.
- Run completion commits status, closeout narration, and chronicle canon together or rolls back
  with `runCompleteSkipped`.
- Successful recruitment uses the same hand-off discipline: explicit and dialogue recruitment
  commit faction, control, bond, memory, and message children together or roll back with a hidden
  `recruitmentSkipped` audit.
- Explicit service requests submit `request_service`, which commits effect, payment, optional
  want completion/memory, and player narration together; rejected children roll back with
  `serviceRequestSkipped`. The command wrapper, not the consequence applier, still decides that
  a successful request consumes a turn.
- Door-triggered rescue cascades are also staged: opening the door is one typed mutation, and the
  resulting faction/control/bond/want/deed/message package is requested through reusable
  `free_captive`, committing together or rolling the door state back with hidden
  `freeCaptiveSkipped` and `openOrUnlockSkipped` audits.
- Readable text, promise payoff receipts, background-generated details, and run chronicles now
  write durable lore through `add_canon` rather than direct canon-ledger mutation.
- Every consequence carries enough cause to be narratable and debuggable regardless of who
  requested it. High-salience journal leads and claims surface that provenance as player-facing
  fiction ("heard from..." and confidence), while low-salience facts remain quiet context.

### 2. Rumor as a first-class transport layer

The signature vignette - a villager sees the whale-bones hum, tells a friend, *the tale grows in
the telling*, and days later a stranger recruits himself - is the acceptance test for this layer.
The central object is a **rumor record**: a deed's story as a propagating object rather than a
one-time jump straight to legend and standing.

Shape (engine-level, deterministic):

- A rumor is minted from a witnessed/public/mythic deed (or a loud claim) with: source deed id,
  current text, carrier set (who has heard it), region, distortion history, and salience.
- **Propagation is deterministic** and runs at existing pump points (zone transition, rest,
  significant deed): a rumor may hop to plausible new carriers - same zone, same faction, along
  travel routes. Bounded hops per pump; salience decays; stale rumors fade.
- **Distortion is interpretive** and is the perfect place to spend background model calls: a
  queued job may retell the rumor in the region's voice, exaggerating along the teller's fears or
  admirations, per [AESTHETICS_AND_TONE.md](AESTHETICS_AND_TONE.md). The deterministic skeleton
  keeps the original text when the provider is absent.
- **Readers:** dialogue quotes rumors ("they say the bones sang a name"); bond checks and faction
  standing read what a given NPC has actually heard rather than global truth; a rumor that grows
  specific enough can bind a promise through the ordinary claim pipeline.

Rumor also solves a legibility problem beautifully: the player hears *versions* of their own
deeds, which is both delightful and informative about what the world knows - and what it has
wrong.

Current status: the deterministic skeleton exists. Dialogue claims and authored `ClaimSourceComponent`
seeds enter `ClaimLedger` through the shared `record_claim` consequence. `RumorLedger` stores source,
text, salience, carriers, region, hops, and distortion history. Non-secret deeds and high-salience
visible dialogue/document/prop claims mint rumors through the shared `record_rumor` consequence with
duplicate-source protection.
`RumorSystem.Propagate` performs bounded turn/travel hops into region and NPC
carriers through the shared `update_rumor` consequence, preserving carrier, hop, salience, status,
and history metadata. The spread budget counts successful new-carrier hops rather than saturated
records inspected, so an already-heard rumor cannot starve a live one behind it. Old rumors keep
full strength for their first couple of hops, then decay through the same `update_rumor` lifecycle;
when salience drops below the active threshold they become `stale` and fade out of player-facing
rumor views. A rumor that newly reaches a local NPC also writes a hidden `record_memory` child
consequence so later dialogue can draw from personal memory as well as carrier membership. Each
spread attempt stages the rumor update and heard-memory children as one packet; if a child rejects
or silently refuses to apply, carrier/hop/salience changes roll back and hidden rejection
diagnostics plus `rumorPropagationSkipped` escape.
Local NPC carrier selection now scores active wants, entity/faction tags, and profile text against
the rumor tags/text, so transport remains deterministic but tends toward the people who are
narratively primed to care about a procedure, route, service, debt, folk-magic secret, or
confiscated object rather than simply choosing the first entity id.
High-hop, high-salience rumors can now queue `rumor_distortion` background jobs; the
queue request itself goes through the shared `queue_background_job` consequence with a `rumor`
target kind, and job state transitions go through `update_background_job`. Retelling text can come
from the configured background provider or deterministic fallback, then applies through
`update_rumor`, appends `distortion:` history, and marks the record `distorted`.
Completed retellings remain candidate text until the turn pump applies the typed
consequence; rejected apply attempts roll back and mark the job failed instead of mutating the
rumor halfway. Completed-but-unapplied jobs resume at the next turn pump, and stale running jobs
fail visibly instead of blocking future duplicates. Journal and rumor views now expose player-heard rumor status and
recent retelling history, debug observations expose full carrier/history cards for all rumors,
save/load preserves the trail, and generated dialogue context can read heard rumors.

### 3. Every notable NPC carries one want

Bonds, memories, and claims exist, but NPCs are purely reactive. The smallest interiority upgrade
with the biggest payoff: each notable NPC carries **one want** - promise-shaped data ("free my
brother from the checkpoint," "restore the drowned bell," "get out of this posting alive").

- **The dialogue model reads it,** so conversations have direction without scripts.
- **Gifts and deeds are weighed against it,** so leverage reads as *understanding someone*, not
  paying a meter - protecting the "relationships, not stat bars" rule.
- **Satisfying or violating it** is the natural trigger for devotion, drift, departure, and
  betrayal thresholds in the bond model.
- **It is the organic quest source.** Helping a want binds promises through the ordinary
  claim/promise pipeline; no quest-giver machinery exists or is needed.

The first implementation is `WantComponent`: one active desire with salience, stakes, status, and
tags. Authored notable NPCs, from the opening prisoners and soldiers through late-game figures like
Emperor Odran, use authored wants; generated residents receive deterministic wants at instantiation
from their region role; promise-generated people, merchants, and service providers receive
promise-shaped wants through `spawn_entity` payloads. The shared `spawn_entity` applier now also
synthesizes a conservative default want for notable spawned NPCs when no explicit want payload is
supplied, with an `autoWant: false` opt-out for creature-like or deliberately blank spawns.
Dialogue participant cards include an active-want summary so the model can lean into it without
making the want player-facing or mechanically binding by itself.
If dialogue or another system materially satisfies, blocks, or redirects that desire, the update is
submitted as `update_want` and audited like the rest of the consequence grammar. That consequence
can also write a hidden `record_memory` child delta when the source should remain durable context:
dialogue want shifts, fulfilled services, and the opening rescue already use this so the flywheel
can later read not only *what* the NPC wants, but *why* it changed. Services that declare want
completion commit the effect, payment, request message, want update, and memory child together; a
failed want update rolls the service request back instead of leaving a mechanically completed but
socially incoherent service behind.

A want is one data field read by three systems. It stays orthogonal to the three bond layers
(combat allegiance, organization membership, personal bond) - it is the NPC's own vector, not
their relationship to the player.

### 4. The bounded world-turn (momentum, not simulation)

Pump points currently apply *reactions*. Extend the same discipline to *initiative*: at each
pump, the world may take a small number of its own moves - one faction expenditure, one promise
stirring a stage, one rumor hop, one clerk memo. Strictly bounded, deterministic, replay-safe,
and always through the same consequence grammar.

The feeling this buys is the finish line GRAND_VISION names: *a place that has decided to have
opinions about you.* A world that only ever answers feels like a mirror; a world that
occasionally moves first - a patrol reroutes, a promised blade stirs, a rival realm makes a
play - feels alive. The hard bound (a handful of moves per pump, budgeted like faction resources)
is what keeps this from becoming the life simulator
[WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md) rightly forbids.

Current status: the first budgeted service exists as `WorldTurnSystem`. Turn and travel pumps can
spend a small budget on audited `WorldTurnRecord`s through the shared `record_world_turn`
consequence; current move families include `faction_pressure`,
`faction_pressure_blocked`, `faction_recovery`, `rumor_spread`, `promise_stir`, and `want_stir`. The audit ledger is saved, restored,
and exposed in debug summaries. Faction pressure now chooses its patrol/warrant/resource-shortage
moves as bounded world-turn initiative, while the actual state changes still submit typed
consequences for faction resources, scheduled pressure, canon memos, and visible messages; those
typed sub-deltas are returned beside the wrapper `worldTurn` audit delta. On
compound world-turn moves, the system snapshots state and commits child consequences as a
unit; if scheduling, canon, cooldown, rumor updates, want memories, player messages, or audit
recording rejects, staged resource, memory, rumor, and message changes are rolled back and only
the rejection diagnostics plus a hidden `worldTurnSkipped` audit for the attempted move survive.
The transaction helper treats any child rejection delta as a failed move, so optional child effects
cannot accidentally commit beside a rejection.
On deed-free turn pumps, `WorldTurnSystem` also runs quiet faction-pressure recovery before spending
initiative budget, so laying low can refill patrol/warrant pools and cool heat without making deed
turns recharge the Empire for free; if recovery changes any resource, it records one hidden
`faction_recovery` audit move while the resource mutations remain ordinary
`adjust_faction_resource` consequences. Player-facing message logs filter those generated and
world-turn deltas through the same visibility rule as action results; audit-only wrappers remain
available to debug state and transcripts without becoming duplicate player narration. A `want_stir`
move spends budget to write a hidden `record_memory` for one high-salience active NPC want that has
not already been remembered in its current wording, giving later dialogue ordinary context rather
than a special quest channel.

## Legibility: Consequences Carry Their Causes

Emergence the player cannot perceive is indistinguishable from randomness. Make the standing
principle concrete with one rule:

> **Every consequence the player perceives carries its cause in-fiction.**

Not "Reputation +2" but "The ferryman won't meet your eyes - word from Hollowmere travels fast."
The journal is the *story of record*: deeds as the player understands them, leads, omens, and the
versions of rumors they have heard. Debug and agent views keep the exact math. When choosing one
legibility feature per phase, always pick the one that closes the loop from ledger back to prose.

## The Late Game Is the Same Machine at Scale

No bespoke finale. The capital is simply the most extreme region roll: maximal marble, dense
witnesses, the deepest faction resource pools, and the tightest paper architecture. Reaching the
emperor means the flywheel at full speed:

- imperial defenses are faction resources the player has drained through deeds elsewhere;
- access is bonds, legitimacy, and bound prophecies cashing in;
- infiltration is body swap and disguise riding the witness model;
- the geopolitical path is coalition bonds, faction pressure, and rival-realm play.

If killing the emperor requires any system that did not exist in the opening containment yard,
the anti-port principle has failed.

## What to Protect

The standing disciplines are right and this document defends all of them: the engine owns truth;
the deterministic skeleton stands with zero model calls; semantic effects are never on the
critical path; reactions are expenditures, not thresholds; no meta-progression. Given the current
trajectory, the two failure modes to watch hardest:

- **Ledger sprawl.** Ten lanes that each work but do not cross-read. The rumor layer and the
  one-grammar convergence are the antidotes: lanes earn their keep by feeding each other.
- **Combat-as-latency.** Wild magic now has a materialized resolve/apply seam, and pending casts
  start non-mutating provider resolution at submit time while applying only at `await_cast`. Before
  combat frequency rises, keep pushing this toward richer UI/minigame polling and interruption
  handling so the crown jewel does not become a loading spinner.

## Relationship to Other Docs

[GRAND_VISION.md](GRAND_VISION.md) owns intent and the interaction map; this document names the
loop and the convergence moves. [MATURITY_ROADMAP.md](MATURITY_ROADMAP.md) owns sequencing; the
four moves above slot into its phases roughly as: consequence grammar (ongoing consolidation
across Phases 4-6), rumor transport (Phase 4/7 seam), wants (Phase 4), world-turn (Phase 3/7
seam). As each move is designed in detail, its specification
belongs in the relevant system doc, with this document updated to point there.
