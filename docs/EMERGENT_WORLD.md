# Emergent World - How the World Responds to the Player

Status: first deterministic spine implemented. Deeds are captured through the engine,
classified by witness visibility, and applied by the turn pump into soul-bound legend tags
and multidimensional standing. Faction resources, richer rumors, and model-assisted
ambiguous interpretation remain later layers. Companion to
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

Owner cadence decision (2026-07-09): notable NPCs may proactively approach, interrupt, seek out,
or follow the player when a want, rumor, memory, promise, or deed gives them a reason. These moves
still occur only through bounded pump-point initiative and must name their cause. A significant
player action should usually earn one visible answer within one to three zone transitions, while
larger consequences may remain in motion for much longer.

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

The current implementation keeps this intentionally small. `WorldReactionSystem` captures
deeds for accepted wild magic, attacks/kills, prisoner rescue, and body swap; classifies
them as secret, suspicious, witnessed, public, or mythic; applies unapplied deeds from
`TurnSystem.AdvanceTurn`; and writes deterministic faction standing plus bounded legend
tags. `standing` and `journal` show the resulting reputation and legend instead of raw deed
rows. Suspicious magic can raise suspicion without attaching legend when only the effect was
seen.

**Concealment gates who counts as a witness of the actor, not of the effect.**
`PerceptionSystem.CanPerceiveSubject` is the one shared concealment rule: a bearer of an
active conceals-bearer status (the canonical `concealed` status and its aliases -
`hidden`, `shadowed`, `veiled`, `mist_cloaked`, `camouflaged`, and others in
`StatusRegistry`) is not counted as witnessed by anyone farther than
`PerceptionSystem.ConcealedNoticeRadius` (2 tiles), unless an active `revealed` status
counters the concealment. This same rule gates both deed/witness capture
(`GameEngine.PlanDeedCapture` filters *actor*-witnesses through it, in
`PerceptionSystem.WitnessesOf`) and hostile AI targeting (`AiSystem.CanNoticeTarget`
delegates to it), so a concealment spell that hides the caster from soldiers' notice also
hides the caster from their deed-witness memory - one mechanic, not two coincidentally
similar ones. Effect-witnesses are never filtered by concealment: an effect that is loud or
visible is still seen even when its caster is not, which is what turns a concealed public
cast into a `suspicious` deed (unattributed - the world remembers "someone unnamed worked
wild magic," not the sorcerer by name) rather than a fully `secret` one. This is deliberately
about *identity*, not *visibility of the location itself*: disguise/misattribution (a visible
actor whose identity is wrong) is a distinct, not-yet-built axis that this rule does not
touch.

**Region "cover" traits are narrative only today, not a line-of-sight mechanic.** Affordance
cards such as `imperial_cover` ("Marble walls create hard lines of sight and official blind
spots.") or `reed_cover` are text-and-tags: they surface in `atlas`, feed lore-routing subjects,
and tag generated zones, but `PerceptionSystem.BlocksSight`/`HasLineOfSight` reads only
`GameState.BlockingTerrain` and per-entity `PhysicalComponent.BlocksSight` - never a region's
affordance ids. Standing behind an actual wall or blocking fixture blocks sight; standing in a
region whose prose says it has "blind spots" does not, by itself, block or reduce anyone's
perception. Don't design a spell, an encounter, or a doc passage assuming a region's cover trait
does anything mechanical yet - if that changes, this note should move or be removed.

## Factions spend finite resources

A reaction is an expenditure, not a threshold trip. A crackdown is "the Empire spends a
patrol and an informant," not "fear > 70." Because resources are finite, an overspent faction
goes quiet and pressure ebbs and flows instead of ratcheting forever. This is also how
escalation stays bounded and the game stays winnable. Exact resource pools are developer/debug
state; player-facing views should show pressure, mood, warrants, rumors, and visible consequences
rather than raw numbers.

## Bonds, organizations, followers

The first deterministic bond/follower slice exists. Build general primitives, not a party system:

- **Individual bonds**, per NPC, separate from any organization.
- **Organizations** are first-class and plural - the player can found several or climb
  existing ones; the Empire is one too.
- **Three orthogonal layers, never conflated:** combat allegiance, organization membership,
  and personal bond to the player.
- Devotion, drift, departure, and betrayal **emerge** from thresholds; the model voices the
  moment; consequences are written back as durable traits/notes that color all future
  behavior. Keep the math invisible - this must read as relationships, not stat bars.

Current implementation covers gift memories, bond posture, bond-driven followers,
personal-bond hostility overrides, dialogue-extractor bond proposals, and trusted dialogue
secrets binding promises. Founding organizations, deep drift, departure, betrayal scenes, and
model-voiced dialogue remain later layers.

## A fresh world each run

No meta-progression. Each run rolls a new geopolitics from the seed - which kingdoms are
conquered or defiant, who rules them, where traditions survive, how hard the Empire grips
each province. **Procedural rolls the structure** (deterministic, replay-safe); the model
**names and flavors it**. Every rolled feature must imply at least one tactical affordance the
player can act on - a safer region, a recruitable tradition, a conflict to exploit - not just
lore. The win condition resolves within a single run: kill the emperor. The emperor should exist as
a killable character even before the elaborate systems for reaching him are fully built.

Current implementation is the first multi-zone spine, not the full world roll: `GenerationSystem`
stores inactive places as `ZoneSnapshot`s, lets the player travel across coordinate zones, lazily
generates region-flavored interiors, carries bond-followers, and exposes a world/atlas card with
region affordances. Those affordances also enter the resolver lens as soft context, so a place can
color free-form magic without bypassing engine operations. Bound travel/site promises can now
realize during generation as promise-site fixtures, canon records, journal updates, and action
deltas. A minimal seeded world roll now gives realms deterministic status, rulers, and imperial
grip variation. Region and tradition definitions now load from content: fourteen regions cover
every rolled realm and use seed-jittered data-authored anchors to form a reachable first regional
map. The roll selects exactly one free rival among the four old kingdoms and marks the other three
conquered. Border messages name the realm left and entered, making the first geography change legible.
Regional population grammars now add seeded density fields: crowds at authored centers, thinner
road/settlement shoulders, common empty wilderness, and rare outliers. Name forges and cultural
archetypes seed every resident with a want and knowledge while selected roles carry services or
wares through shared consequences. `WorldPlaceGraph` now gives every region a named multi-zone
center, hamlets, a landmark, and connected physical roads; district identity feeds terrain,
population, signature sites, atlas, travel, magic, and dialogue. The first generated journey lets
a resident's pressure become an inspectable claim and exact-place promise whose evidence is
realized at that real landmark. Organizations, significant interiors, return-delivery journeys,
alternate completion rules, and full geopolitical ownership remain later layers.

## Legibility is first-class

Emergence the player cannot perceive is indistinguishable from randomness. Budget as much
design effort on *showing* the reaction (rumor lines on zone entry, NPCs greeting you by
legend, a standing readout, named voices, the run chronicle) as on computing it.

## Growth lanes

Per [WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md), keep the ledger lanes -
deed, promise, faction, memory, canon, semantic - clean and general. Phase 2 has made the
first deed/legend/reputation lane real, Phase 3 made finite faction pressure real, and Phase 4
made the first bond/follower lane real. Future work should extend these with richer organizations,
organic model-voiced dialogue, rumors, and narration without bypassing the engine pump or reading
prose as authoritative state.
