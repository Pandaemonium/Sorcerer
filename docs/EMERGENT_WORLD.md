# Emergent World - How the World Responds to the Player

Status: vision-for-later. The first playable defers this; what matters now is reserving the
ledger lanes so it can grow without rework. Companion to
[WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md) (arc, tone, moral texture, and
the state lanes) and [CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md) (the pump points this
hooks into). Adapted from the Wild Magic prototype: the philosophy is kept, the Python phase
structure is dropped.

The promise: the player drives an emergent story by acting, and the world answers - rumors
spread, factions respond, allies rise, the Empire escalates. Not a life simulator; a world
that reacts to what the player does.

## The one principle: the model interprets and narrates, it never simulates

> Deterministic ledgers hold world truth. Deterministic rules decide mechanical
> consequences. The model is used only to read the *meaning* of an ambiguous action and to
> *narrate* the world's reaction in lush prose.

The deterministic skeleton must stand complete with zero model calls. The model makes the
world beautiful and legible, never functional. This guarantees replay, testability, and
graceful degradation when the provider is cold or absent.

Who places which block:

| Job | Owner |
|---|---|
| Hold world state (who, where, standing, goals) | deterministic ledgers |
| Decide mechanical consequences (standing deltas, who spawns, who flees) | deterministic rules |
| Read the meaning of an ambiguous player action | model (interpreter) |
| Decide an NPC's heart on the cusp (join/flee/betray) | rules first; model only near a threshold |
| Generate long-form lush text (rumors, memos, the run chronicle) | model (narrator) |

## Cadence: event and pause-driven

World reaction runs at explicit engine pump points - after a significant deed, on rest, on
zone transition - not per turn, not on a real-time clock, and never from a background worker
mutating state directly. This is the same pump discipline as background jobs and pending
casts in the RFC: durable change enters state only at controlled apply points.

This restriction is for social/world reaction: deeds, factions, promises, rumors, narration,
and geopolitics. Tactical mechanics such as status ticks, auras, terrain reactions, and delayed
effects can run through explicit engine turn-tick systems when the player needs immediate,
legible consequences.

(This replaces Wild Magic's once-per-in-game-day tick. The daily clock was an arbitrary
heartbeat; pump points tie reaction to what actually happened.)

## Deeds -> Legend -> Reputation

- A **deed** is the atom: what the player did, plus who saw it. Recorded the instant it
  happens; applied at the next pump point. **Bound to the soul, not the body** - body swap
  does not launder reputation.
- **Visibility gates everything.** A deed is secret, suspicious, witnessed, public, or mythic, and
  only enters the legend through one of those channels. A secret deed shapes only those who know.
  A suspicious deed is one where someone saw the effect or harmed target but not the caster; it
  becomes attributed to the player only if that witness gets line of sight to the player within
  20 turns. If they never see the player in that window, they do not magically know who caused it.
- **Legend** is a few weighted tags (defiant, butcher, merciful, uncanny) that dialogue,
  rumors, and NPC feeling all read. Keep it in two forms: bounded tags that systems reason
  over, and prose notes that only prompts read.
- **Reputation is multidimensional, never one score.** An open set of axes, each driving a
  distinct consequence: notoriety, fear, gratitude, legitimacy, uncanniness, imperial
  threat. One deed lands on several at once - burning an imperial barracks raises rebel
  gratitude, townsfolk fear, notoriety, and imperial threat together.

## Factions spend finite resources

A reaction is an expenditure, not a threshold trip. A crackdown is "the Empire spends a
patrol and an informant," not "fear > 70." Because resources are finite, an overspent faction
goes quiet and pressure ebbs and flows instead of ratcheting forever. This is also how
escalation stays bounded and the game stays winnable. Exact resource pools are developer/debug
state; player-facing views should show pressure, mood, warrants, rumors, and visible consequences
rather than raw numbers.

## Bonds, organizations, followers (reserved lane, built late)

The richest part, and the one to build last on a settled spine. Build general primitives,
not a party system:

- **Individual bonds**, per NPC, separate from any organization.
- **Organizations** are first-class and plural - the player can found several or climb
  existing ones; the Empire is one too.
- **Three orthogonal layers, never conflated:** combat allegiance, organization membership,
  and personal bond to the player.
- Devotion, drift, departure, and betrayal **emerge** from thresholds; the model voices the
  moment; consequences are written back as durable traits/notes that color all future
  behavior. Keep the math invisible - this must read as relationships, not stat bars.

## A fresh world each run

No meta-progression. Each run rolls a new geopolitics from the seed - which kingdoms are
conquered or defiant, who rules them, where traditions survive, how hard the Empire grips
each province. **Procedural rolls the structure** (deterministic, replay-safe); the model
**names and flavors it**. Every rolled feature must imply at least one tactical affordance the
player can act on - a safer region, a recruitable tradition, a conflict to exploit - not just
lore. The win condition resolves within a single run: kill the emperor. The emperor should exist as
a killable character even before the elaborate systems for reaching him are fully built.

## Legibility is first-class

Emergence the player cannot perceive is indistinguishable from randomness. Budget as much
design effort on *showing* the reaction (rumor lines on zone entry, NPCs greeting you by
legend, a standing readout, named voices, the run chronicle) as on computing it.

## Reserved lanes (what to keep ready now)

Per [WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md), reserve the ledger lanes -
deed, promise, faction, memory, canon, semantic - so this can grow without a rewrite. None of
this is built in the first playable; the goal now is only that the architecture leaves clean
room for it.
