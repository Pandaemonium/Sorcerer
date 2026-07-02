# Systems Vision - The Interlocking Machine

Status: proposed 2026-07. Companion to [GRAND_VISION.md](GRAND_VISION.md), which owns intent and
always wins on it. This document sits one level below: it names the single loop all systems serve,
the hand-off discipline that makes emergence cheap, and the four unification moves that close the
weakest seams in the current interaction map. Detailed designs live in the system docs this
document points into; when a move here matures, it should graduate into those docs.

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
validation rules. When there is one grammar, every new verb is instantly available to every
source, and every new source instantly speaks every verb. Combinations multiply; complexity
adds.

The acceptance test for any new mechanic: adding `collapse_bridge` as a consequence type should
mean a spell can do it now, a prophecy can promise it later, a faction can spend for it, and an
NPC can threaten it - with zero additional wiring. Fast consequences are fast because their
`timing` is immediate; slow consequences are slow only when the consequence explicitly says so.

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
- Existing `IOperation` code can survive as the first implementation detail behind immediate
  tactical consequence handlers while the public contract migrates to consequences. The long-term
  goal is one proposal/validation/application lifecycle, not two grammars that bridge forever.
- Promise realization plans, dialogue extractors, books, services, AI plans, faction
  expenditures, and wild magic all submit consequences rather than owning private mutation
  helpers.
- Every consequence carries enough cause to be narratable and debuggable regardless of who
  requested it.

### 2. Rumor as a first-class transport layer

The signature vignette - a villager sees the whale-bones hum, tells a friend, *the tale grows in
the telling*, and days later a stranger recruits himself - cannot happen yet. Deeds currently
jump straight to legend and standing. The missing piece is a **rumor record**: a deed's story as
a propagating object.

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
- **Combat-as-latency.** The pending-cast seam must become true background resolution before
  combat frequency rises, or the crown jewel becomes a loading spinner.

## Relationship to Other Docs

[GRAND_VISION.md](GRAND_VISION.md) owns intent and the interaction map; this document names the
loop and the convergence moves. [MATURITY_ROADMAP.md](MATURITY_ROADMAP.md) owns sequencing; the
four moves above slot into its phases roughly as: consequence grammar (ongoing consolidation
across Phases 4-6), rumor transport (Phase 4/7 seam), wants (Phase 4), world-turn (Phase 3/7
seam). As each move is designed in detail, its specification
belongs in the relevant system doc, with this document updated to point there.
