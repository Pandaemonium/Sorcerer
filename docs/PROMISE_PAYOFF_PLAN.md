# Promise Payoff Plan

This plan covers the layer between "a claim became a bound promise" and "the
player can actually do something with it."

The dialogue system can now turn high-salience NPC claims into structured
claims and promises. The opening sequence depends on those promises paying off
quickly enough to be felt. This document defines the general payoff system we
should build so the first 20 minutes sing by improving the whole game, not by
adding bespoke opening scripts.

## Core Principle

A promise payoff is complete only when the player gains a real affordance:

- a place they can visit or inspect
- a person they can meet, talk to, trade with, recruit, fear, or betray
- an item they can find, buy, steal, equip, cast with, or protect
- a threat they can avoid, fight, bargain with, or suffer
- a route, door, rule, service, memory, debt, or omen that changes later engine
  behavior

Flavor text is allowed as a first signal, but it is not enough. The bound
promise must eventually become engine state.

## Current Foundation

The current implementation already has useful bones:

- `WorldPromise` records kind, text, status, salience, subject, claimed place,
  bound place, bound target, trigger hint, realization kind, and `realizedIn`.
- `PromiseAnchorComponent` lets entities own promise hooks without involving
  renderer code.
- Generated dialogue can propose claims/promises, and the engine validates them
  before mutating state.
- `ClaimSourceComponent` lets readable and examinable entities carry authored
  claim seeds. `read` and `examine` surface those seeds through `record_claim`;
  high-salience visible seeds mint rumors through `record_rumor`; seeds marked
  as buildable hooks bind through `create_promise`, then the claim is updated
  through `update_claim`.
- `PromiseRealizationSystem` is the first shared payoff service. Travel
  generation, anchored interactions, and the first ambient player-turn trigger
  now route through it instead of keeping separate realization code in
  `GenerationSystem`, `InteractionSystem`, or command handlers.
- Travel generation can realize region-bound promises as sites, items, people,
  threats, routes, or merchant/service payoffs and emits a hidden
  `promiseRealizationPlan` audit delta, then a standard `realizePromise` delta,
  before the concrete payoff delta. Destination-zone
  payoffs now run ordinary `WorldConsequenceApplier` handlers against a detached
  destination-state snapshot. The staged zone entity map stays separate from
  the currently loaded zone, but global ledgers such as canon, memories, rumors,
  scheduled events, world flags, RNG, and entity serial commit back only after
  successful application. Compound generated payoffs stage every child
  consequence in that detached snapshot and commit once, so a rejected
  `offer_trade`, `offer_service`, or `add_canon` cannot leave behind an orphaned
  provider or partial lore receipt. Site, item, person, threat, and route payoffs use `spawn_fixture`,
  `spawn_item`, `spawn_entity`, and `create_route`; merchant and service
  payoffs spawn the provider through `spawn_entity`, then attach `offer_trade`
  or `offer_service`. Promise-spawned
  people, merchants, and service providers receive `WantComponent`s from the same
  spawn payload so first contact has an active desire without bespoke scripting.
- Anchored `talk`, `read`, `open`, and `inspect`/`examine` interactions can
  realize promises as memory, threat, item, route, quest/canon, site/canon, or
  omen/canon.
- The `wait` command can realize one explicitly time-triggered bound promise
  per use through the same service. This is intentionally narrow: `wait`,
  `rest`, `linger`, `bellfall`, `nightfall`, and similar trigger hints can
  mature omens, debts, threats, door rules, items, people, services, or routes,
  but ordinary travel leads do not cash in just because the player spent a turn.
- Commerce commands can wake anchored merchant-stock promises. `wares`, `buy`,
  and `sell` resolve a nearby merchant or promise-backed provider, ask
  `PromiseRealizationSystem` for commerce payoffs, and then inspect, purchase,
  or sell through the ordinary `MerchantComponent` and `execute_trade`
  consequence. A promised seller can therefore become a normal merchant at the
  moment the player asks for wares, buys promised stock, or offers goods for sale.
- Service commands can wake anchored service promises. `services` and `request`
  resolve a nearby service provider or promise-backed provider, ask
  `PromiseRealizationSystem` for service payoffs, attach an ordinary
  `ServiceComponent` through `offer_service`, and then list or request the
  service through the normal validated service path.
- Realization produces state deltas, player-facing messages, durable canon or
  memory where appropriate, and promise-anchored entities for concrete payoffs.
  Anchored route and canon payoffs now apply through `create_route` and
  `add_canon`, so their audit deltas match the shared consequence grammar.

The next step is not to invent a separate quest engine. It is to make promise
payoff selection and application deeper, more general, and more consistent.

## Design Goals

- **One lifecycle.** Dialogue claims, spell prophecies, documents, deeds,
  faction threats, and bargains should all flow through the same promise ledger.
- **Narrowest authoritative state.** Preserve the claim with the smallest
  binding that makes it useful. A memory is cheaper than a promise; stock on an
  existing merchant is cheaper than a new shop; a nearby stash is cheaper than a
  new town.
- **Always honor bound promises.** A reported claim can be wrong. A bound
  promise must eventually resolve or visibly transform.
- **Real affordances over quest text.** Payoffs should create actions, targets,
  inventory, routes, threats, or prompt context.
- **General triggers.** Travel, talk, read, inspect, open, buy, trade, wait,
  cast, and faction turns should all be possible apply points.
- **Engine authority.** The model may propose. The engine validates, prices,
  applies, audits, and rolls back on failure.
- **Reusable outside dialogue.** The same payoff grammar should serve wild
  magic, books, signs, rumors, world generation, faction moves, and NPC AI.
- **Debuggable for agents.** CLI/debug views can expose exact promise state;
  the normal GUI can present it as rumor, omen, journal text, or discovery.

## Lifecycle

1. **Capture.** A source creates a claim, prophecy, omen, bargain, debt, or
   similar future hook.
2. **Record provenance.** The engine stores who said it, who heard it, where,
   when, confidence, salience, and whether it was player-authored.
3. **Bind.** The engine accepts a buildable subset as a promise with kind,
   realization kind, trigger hint, subject, claimed place, and bound target or
   region when known.
4. **Wait.** The promise stays active. It may be visible in the journal or only
   visible to debug tools.
5. **Scan.** At explicit apply points, the engine asks whether active promises
   are eligible for this context.
6. **Plan.** The payoff system builds a small validated realization plan:
   create an entity, add stock, attach a service, write memory, spawn a threat,
   add canon, reveal a route, or schedule an event.
7. **Apply transactionally.** The engine applies the payoff, marks the promise
   realized or advanced, records `realizedIn`, and emits deltas as one commit.
   If either payoff application or final status update rejects, staged state and
   visible payoff messages roll back and the audit trail records
   `promiseRealizationSkipped`.
8. **Surface.** The player gets a diegetic message or journal movement. Debug
   views get exact ids, kinds, triggers, and created state.
9. **Continue.** Realized content can carry memories, anchors, traits, faction
   ties, services, or new promises.

## Realization Context

Promise payoff should use one shared context shape no matter which system
triggered it. The first `PromiseRealizationContext` now covers travel, ambient
turn hooks, and anchored interactions. Travel contexts carry trigger,
destination zone, region, entry direction, and placement origin; anchored
contexts carry trigger, region, and anchor entity id. Selected payoffs now become
`PromiseRealizationPlan`s with a normalized handler, target, `realizedIn`,
selection score, and selection reasons. `promiseRealizationPlan` and
`realizePromise` audit deltas include these fields so debug views can explain
why a promise paid off in this place at this moment.
Concrete plans preflight basic capacity before status mutation: spawned
item/threat/person/site/provider payoffs need an open placement tile, and
door-rule payoffs require a real door anchor. If preflight fails, the promise
stays bound and receives a hidden eligibility failure such as
`no_open_adjacent_tile` instead of being spent.
Concrete handler application now also precedes the final status update, but the
handler output and `promiseStatus` update are committed together. If the
registered handler returns a rejected consequence, a skipped payoff, or a final
status-update rejection, the promise stays bound, staged payoff content and
visible payoff messages roll back, rejection diagnostics remain in audit deltas,
and the last eligibility failure records why the promise did not pay off.
Plans now dispatch through registered archetype handlers for travel and
anchored/ambient payoff families. Each handler owns its preflight and concrete
application step, so adding a new promise payoff type should mean registering a
handler rather than adding another caller-specific switch.
Claim-derived promises now preserve source claim id, source speaker id, source
listener soul id, and source confidence on `WorldPromise`. `create_promise`
deltas, `PromiseCard`, and `promiseRealizationPlan` audit deltas expose that
chain so later dialogue, debugging, and payoff evaluation can trace a realized
thing back to the claim that birthed it.

A deeper `PromiseRealizationContext` should continue growing toward:

- triggering verb: `travel`, `talk`, `read`, `inspect`, `open`, `buy`, `trade`,
  `wait`, `cast`, or `faction`
- actor id and controlled entity id
- anchor entity id, if any
- region, zone, position, and turn
- promise id and source claim id, when available
- source speaker/listener ids, when available
- available capacity: open tiles, region affordances, merchant stock limits,
  nearby containers, faction pressure, and pacing budget
- deterministic RNG/replay seed

The output should be a validated realization plan rather than direct mutation.
That keeps the same engine safety pattern as magic and dialogue: parse,
normalize, validate, preflight, apply, audit.

## Payoff Archetypes

### Site, Town, Or Landmark

Use for claims like "there is a town south of here," "the old bell bridge marks
the pilgrim road," or "the blue shrine waits past the quarry."

Preferred triggers:

- `travel`
- `inspect` map/sign/road
- `read` a document with a place claim

Payoff:

- Create or reserve a site entity, region feature, settlement stub, or landmark.
- Attach `PromiseAnchorComponent`.
- Write canon with promise provenance.
- Add tags that future generation, dialogue, and magic can see.
- If the exact claimed location is impossible, bind a nearby viable location and
  preserve the mismatch between `claimedPlace` and `boundPlace`.

Complete when the player can inspect it, travel through it, talk about it, or
use it as an anchor for later systems.

### Person

Use for named relatives, witnesses, merchants, healers, rivals, prisoners,
officials, and promised allies.

Preferred triggers:

- `travel`
- `talk` to an anchored source
- `inspect` a roster, notice, or family object

Payoff:

- Create an entity with actor/profile/memory/faction/interactable components.
- Add a memory explaining why they exist: who named them and what was claimed.
- Set tags for role, source promise, faction, and relation when known.
- Preserve whether the source was confident, mistaken, lying, or uncertain.

Complete when the person can be talked to or otherwise acted on, not merely when
their name appears in canon.

### Item, Stash, Or Container

Use for promised blades, keys, charms, reagents, confiscated goods, medicine,
and named tools.

Preferred triggers:

- `travel`
- `open`
- `inspect`
- `read`

Payoff:

- Create an item through the ordinary item/entity system, or place it in an
  existing container, merchant stock, corpse, shrine, cache, or room feature.
- Add promise tags and a description grounded in the original claim.
- Prefer existing item definitions when possible; generate a simple unique item
  only when no ordinary definition fits.

Complete when the player can pick up, buy, steal, use, inspect, or cast with the
item.

### Merchant Stock, Trade, Or Service

Use for claims like "Jimmer can sell you a fine blade," "I can mend that cloak,"
or "the midwife keeps fever-salt."

Preferred triggers:

- `talk`
- `buy`
- `trade`
- `services`
- `request`
- `inspect` merchant or stall

Payoff:

- If the merchant exists, add or reserve stock through the ordinary inventory or
  merchant component.
- If the merchant does not exist, a travel-bound `merchant_stock` promise can
  create an ordinary merchant entity with `MerchantComponent`, the promised
  ware, interactable trade verbs, canon provenance, and a promise anchor.
- Services are explicit NPC affordances, not automatic free effects.
  `ServiceComponent` exposes service id, cost, provider, target hint, and result
  effect. `services [target]` lists revealed services; `request <service> [from
  <provider>]` attempts one through validated engine consequences.
- Dialogue can reveal or offer a trade, but explicit buy/trade commands complete
  the transaction.

Complete when the player can see the stock/service through inspect, talk, buy,
trade, services, or request and can attempt the relevant command.

### Threat, Debt, Or Patrol

Use for promises of future cost: bounty hunters, imperial patrols, debt
collectors, betrayed spirits, collapsing roads, or faction pressure.

Preferred triggers:

- `travel`
- `wait`
- `open`
- `cast`
- faction turn

Payoff:

- Spawn a hostile or wary entity, schedule an event, increase faction pressure,
  add a hazard, or attach a looming status to a region.
- Threats do not always need to attack instantly. A debt collector can speak
  first; a patrol can block a road; a shrine can demand repayment.
- Time-triggered `debt` realization now dispatches through the threat handler,
  so a debt that comes due creates an actionable collector/threat instead of
  only adding abstract canon.
- Preserve the source promise so the player understands why the consequence
  arrived.

Complete when the threat changes player options, movement, safety, reputation,
or resource pressure.

### Door Rule Or Escape Route

Use for "the east door opens for children of ash," "there is a tunnel behind the
cistern," or "the west grate leads out after bellfall."

Preferred triggers:

- `inspect`
- `open`
- `travel`
- `wait`

Payoff:

- Attach a rule, condition, route, or hidden exit to an existing entity when
  possible.
- If no anchor exists, create a modest fixture or passage in a viable location.
- Use the same world-action grammar as ordinary door opening. Avoid special
  cases for one door, one NPC, or one phrase.
- Use the same validated effect/resolution mechanics as wild magic where the
  payoff modifies terrain, routes, doors, hazards, or map topology. An escape
  route promise is another engine-authorized terrain/action change, not a
  separate narrative-only shortcut system.
- The first route payoff creates an ordinary route fixture with tags,
  description, position, promise anchor, material, and interaction verbs through
  `create_route`. Anchored door-rule payoffs on actual doors unlock/open through
  `open_or_unlock`; service payoffs reveal `ServiceComponent` affordances through
  `offer_service`.

Complete when the player can discover, satisfy, violate, or exploit the route
or rule.

### Memory, Lore, Or Canon

Use when the claim is meaningful context but does not need immediate physical
content.

Preferred triggers:

- `talk`
- `read`
- `inspect`
- `realize` as fallback for abstract omens

Payoff:

- Write world memory, entity memory, canon, or journal state with provenance.
- Make sure future dialogue/magic/worldgen context can see it.

Complete when the memory can influence later generated dialogue, claims,
relationship interpretation, or resolver context.

### Quest, Bargain, Or Debt

These should be promise kinds, not separate ledgers.

Payoff:

- A quest is a promise with an objective, counterpart, reward, and failure or
  abandonment handling.
- A bargain is a promise with obligations on both sides.
- A debt is a promise whose payoff is future collection, pressure, or cost.

The first implementation can treat these as structured promise records plus
canon/journal text. Later, they can gain an `ObjectiveComponent` or objective
record without splitting away from the promise lifecycle.

### Prophecy Or Omen

Prophecy is often a wrapper around another realization kind.

Payoff:

- Route concrete prophecies to site, person, item, threat, door rule, event, or
  memory.
- For abstract prophecies, use staged realization: first an omen message or
  dream, then a concrete payoff once a suitable trigger appears.

Complete when the prophecy has altered future possibility, not when it merely
prints poetic text.

## Selection And Pacing

The engine should not simply realize promises in insertion order forever.
Travel selection is now scored with deterministic randomness. The current first
slice still keeps a small per-zone budget, but eligible travel promises are
ranked by salience, trigger fit, archetype, opening pressure, stacks, and RNG
jitter instead of raw ledger order. Travel selection also reads the realization
context: clear "north/south/east/west of here" style directions gate payoff
eligibility, while softer route prose such as "a road south of the yard" remains
available unless it names an exact zone contradiction. Ambient `wait` selection
uses the same scored promise path, but only for promises with explicit
time-shaped trigger hints, so waiting cannot accidentally empty the journal of
travel leads. This lets early runs exercise the system without forcing a
guaranteed payoff every time or cashing a major lead in a plainly wrong
direction.

Anchored interactions now use the same discipline. `talk`, `read`, `inspect`,
`open`, `wares`, `buy`, `sell`, `services`, and `request` collect matching
anchor-bound promises, score them by salience, trigger fit, bound target,
anchor suitability, realization kind, and stacks, then wake a small budgeted
set. This prevents a dialogue-heavy NPC or object from dumping every broad omen
at once, and it lets a lower-salience but exact service/door/trade promise beat
an earlier generic claim when the player invokes the matching verb. Anchored
`realizePromise` audit deltas now include the selection score.

Eligibility gates:

- Promise is active and bound.
- Trigger hint matches the current apply point, or the promise has no stricter
  trigger.
- Bound target, exact zone claim, or explicit directional claim is compatible
  with the current context. Source-region stamps are scoring context, not hard
  destination locks.
- The realization kind has a buildable archetype.
- There is capacity: open tile, valid entity, valid merchant, valid region
  affordance, or valid event slot.

Scoring signals:

- salience
- trigger fit
- deterministic RNG jitter
- proximity to claimed/bound place
- source confidence
- corroboration by multiple claims
- opening/first-payoff priority
- current world pressure and pacing budget
- novelty, so one zone is not filled with three identical promise sites
- danger/reward balance

Pacing rules:

- The opening should expose several promise opportunities early, but it does
  not need to guarantee that one pays off before or at first travel.
- A normal new zone should usually realize at most one or two promises.
- A threat can be paired with a reward or discovery, but a zone should not feel
  like a ledger dump.
- If the world cannot currently honor a promise, keep it active and record why
  in debug/audit state rather than silently discarding it.

Eligibility diagnostics are now part of the promise lifecycle. Failed travel,
ambient, or anchored checks submit hidden `update_promise` consequences that
store the last eligibility failure, context, and turn on `WorldPromise`.
Player-facing action deltas stay quiet, while `PromiseCard` and debug surfaces
can explain why a promise is still waiting. Realization clears the diagnostic
through the same `update_promise` status change so stale failure reasons do not
survive a successful payoff.

## Relocation And Contradiction

Reported claims can be wrong. Bound promises must be honored.

When the exact claim cannot be used:

- Preserve `claimedPlace`.
- Bind the nearest or most coherent viable `boundPlace`.
- Surface the difference when it matters: "The road lies farther south than Lio
  believed."
- Prefer reinterpretation over deletion.

When promises conflict:

- Prefer the older or higher-salience bound promise.
- Let new claims corroborate, complicate, or challenge earlier claims.
- Do not realize mutually exclusive states without a canon explanation.
- If a newer claim is only reported, it can be wrong without changing the
  already-bound promise.

## Player Surface

Ordinary players should feel promises as living rumor and omen, not as a debug
task list.

Useful surfaces:

- an `Objective:` section in the journal for high-salience actionable promises such as
  "there is a magical sword in a burned-out oak tree north of here"
- ordinary promise/journal text for lower-pressure omens
- brief messages when a promise binds, stirs, relocates, or realizes
- inspect text on promised entities
- NPC memories that refer back to the source claim
- map hints for places only when appropriate

Low-salience social facts should not become leads. "Ricky has a brother named
Taylor" can remain dialogue memory or debug-visible claim context unless later
events make Taylor actionable.

Agent/debug surfaces should expose exact ids, status, kind, trigger,
realization kind, source, bound target/place, and realized content.

## Opening Slice

The opening should prove the payoff system by using several different source
types and making early realization likely, but not mandatory.

Minimum target:

- At least one dialogue-sourced claim binds as a promise.
- At least one non-dialogue source, such as a notice, object, door, or spell,
  can bind a promise.
- A gift writes memory; later dialogue can use that memory to produce a more
  specific claim or bond shift.
- One promise can visibly stir or realize before or immediately after first
  travel.
- A second promise remains pending so the player leaves with a hook.

Good opening payoff patterns:

- A prisoner, helped or gifted, names a person or refuge. Travel can realize the
  person/site later.
- A confiscated-goods owner names an item, merchant, or container. Inspect,
  open, buy, or travel can make it reachable.
- A functionary reveals a lawful procedure, office, route, or ledger. The payoff
  can be a door rule, document, service, or faction consequence.
- A witness describes a landmark or threat. Travel can produce the landmark,
  patrol, beast, debt collector, or warning sign.

These are archetypes, not scripted beats. Starting NPCs should get personal
profiles that naturally push them toward concrete disclosures when the
conversation, fear, gratitude, gifts, or leverage make that plausible. The same
NPC profile and promise payoff machinery should work in later villages,
markets, shrines, roads, and prisons.

## Architecture Direction

The first consolidation slice is implemented: `PromiseRealizationSystem` now
owns the concrete realization handlers for travel promises, entity-anchored
promises, and the first ambient command trigger. `GenerationSystem` asks it to
realize travel hooks when a zone is generated. `InteractionSystem` asks it to
realize anchored hooks for `talk`, `read`, `open`, and `examine`/`inspect`.
`wait` asks it to realize one explicitly time-triggered promise before the turn
pump runs. `wares`, `buy`, and `sell` ask it to realize anchored merchant-stock
promises before listing stock or executing trade. `services` and `request` ask
it to realize anchored service promises before listing or submitting `request_service`.

Adjacent dialogue side effects have started the same consolidation. The
`WorldConsequence`/`WorldConsequenceApplier` slice now applies `record_memory`,
`update_bond`, `create_promise`, `update_promise`, `add_merchant_stock`,
`offer_trade`, `execute_trade`, `offer_service`, `request_service`, `open_or_unlock`, `create_route`, `free_captive`,
`spawn_fixture`, `spawn_item`, and `spawn_entity` through a shared engine path,
including destination-zone promise payoffs applied against detached generated-zone
state.
Generated dialogue, claim extraction, services, promise payoffs, books, AI
plans, factions, and magic should increasingly submit the same source-agnostic
typed consequences instead of owning separate mutation helpers. Immediate
tactical payoffs and durable social/world payoffs should share the envelope;
their handlers and `timing` fields decide whether they resolve now, after the
turn, at a world pump, or later.
Authored documents and props follow the same rule: `ClaimSourceComponent` is only a source of
candidate claims. The authoritative writes are still `record_claim`, `record_rumor`,
`create_promise`, and `update_claim`, with duplicate suppression handled by the claim and rumor
ledgers rather than by each object.

The next step is to deepen that service:

- Deepen preflight beyond the first capacity checks by validating all entity
  ids, stock targets, components, event slots, and handler-specific costs before
  mutation.
- Route simple payoff side effects through the typed consequence applier when
  they match an existing consequence type.
- Route broader trade/service aliases, magic, and faction-turn payoffs through
  the same service; the first `wait`, `wares`, `buy`, `sell`, `services`, and
  `request` triggers are live behind explicit trigger guards.
- Deepen merchant-stock and service selection, then add door-rule,
  staged-prophecy, and objective handlers behind the same context shape. Route,
  service, and durable-canon anchored payoffs have the first consequence-backed
  handlers.

This keeps "open a door because dialogue proposed it" from needing separate
door-specific promise logic. The dialogue action, the player command, and a
future spell can all request the same world action, and the shared payoff
service can attach or realize any relevant promises.

The same consolidation rule applies one level down to adjacent dialogue side
effects. A bond shift proposed directly by generated dialogue and a bond shift
proposed by later claim extraction are the same authoritative action: resolve
the entity, clamp the deltas, mutate the bond, emit an audit delta, and report a
structured skip if the entity is gone. They should share one helper even if they
keep distinct delta names such as `dialogueBondShift` and `claimBondShift` for
debugging. Promise payoff work should look for these neighboring split paths
and fold them into shared engine-side helpers as part of the consolidation pass.

## Data Model Additions To Consider

The current `WorldPromise` can support the first payoff slice. Deeper payoffs
will probably want:

- realization stage, for promises that stir before they fully realize
- payoff tags separate from display text
- objective payload for quest/bargain/debt promises
- deterministic realization seed or replay token

Do not add all of these preemptively. Add them when a payoff archetype or test
needs them.

## Tests And Evals

Focused tests should cover:

- travel realizes site, item, person, and threat promises deterministically
- talk/read/open/inspect realize anchored promises without renderer access
- realized promises preserve source text and provenance
- impossible exact locations relocate instead of disappearing
- duplicate or corroborating claims do not create nonsense duplicates
- merchant stock promises add stock to existing merchants when possible
- anchored merchant-stock promises can realize through `wares`, `buy`, or `sell`
  and then use ordinary `execute_trade`
- anchored service promises can realize through `services` or `request`, create
  visible service affordances through `offer_service`, and then use ordinary
  service listing plus the shared `request_service` consequence
- anchored door-rule promises can realize through `open`, unlock/open actual
  doors through `open_or_unlock`, and then request broader follow-on outcomes
  such as `free_captive` through ordinary interaction consequences
- escape route promises use ordinary route validation
- explicit wait/time promises can realize through the `wait` command without
  realizing unrelated travel promises
- threats change actual state, not only messages
- generated-dialogue and claim-extraction bond proposals share clamp limits and
  missing-entity failure behavior
- save/load preserves active, waiting, stirred, and realized promises
- replay realizes without provider calls
- CLI and GUI observe the same resulting state through `GameSession`

Live evals should include:

- twenty dialogue claims across site, person, item, stock, service, threat,
  route, prophecy, and do-not-bind cases
- opening smoke tests where a gift leads to memory, later dialogue, a bound
  promise, and then a payoff
- long-run agent playtests checking whether old promises eventually resolve
  without flooding each new zone

## Implementation Sequence

1. **Characterize current payoff behavior.** Add tests around existing travel
   and anchored realization before refactoring.
2. **Centralize realization context.** Introduce a shared context/selection
   surface while preserving current behavior.
3. **Move existing archetypes behind handlers.** Site, item, person, threat,
   route, memory, and canon should use the shared payoff path. Travel payoffs
   apply ordinary consequence handlers against detached generated-zone state;
   merchant and service travel payoffs compose provider spawns with
   `offer_trade`/`offer_service`; service performance then uses
   `request_service`. Payoff canon receipts are returned as `promiseCanon`
   child deltas and roll the realization back if the receipt cannot be written.
4. **Add provenance depth.** Preserve source claim/speaker/listener in realized
   entities, memories, canon, and debug deltas.
5. **Deepen merchant stock and service payoffs.** The first typed payoff path
   exists; merchant and service payoffs can now carry costs and service-provider
   want completion metadata that resolves through `update_want` inside a
   successful `request_service`. Next, add richer prices and rumor provenance without
   leaving the shared consequence grammar.
6. **Deepen escape route and door-rule payoffs.** The first anchored door-rule
   payoff uses `open_or_unlock`; next, add richer conditions and route/door
   variants without leaving the general world action grammar.
7. **Add scoring and pacing.** Travel, ambient wait, and anchored interactions
   now use deterministic eligibility scoring, small budgets, explicit
   `promiseRealizationPlan` audit deltas, and persisted eligibility failure
   diagnostics. Concrete item/threat/travel placement and door-rule plans
   preflight basic capacity before status mutation. Travel and anchored/ambient
   payoffs dispatch through registered archetype handlers. Next, deepen
   handler-specific preflight.
8. **Wire the opening.** Give the opening two or three promise sources and one
   guaranteed early payoff using ordinary systems.
9. **Run evals.** Keep a small synthetic suite for binding quality and a
   playthrough suite for payoff quality.

## Done When

The payoff system is good enough for the opening when:

- a player can hear a specific NPC claim and later meet, find, buy, fear, or
  inspect the thing it named
- at least three promise categories can pay off without bespoke opening code
- the first travel out of the opening can realize a pending promise
- gifts can indirectly lead to better dialogue claims and later payoff
- promise realization is visible to the player and exact in agent/debug state
- save/load and replay preserve promise outcomes
- adding a new promise source does not require a new mini-engine
