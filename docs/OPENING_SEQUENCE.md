# Opening Sequence

This document describes the target shape for Sorcerer's first 20 minutes. It is a design
brief, not a script. The opening should make the game sing by concentrating the general
systems Sorcerer is already built around: wild magic, entity unification, deeds, witnesses,
relationships, factions, and especially promises.

The guiding rule:

> The opening is not a bespoke tutorial. It is a dense proving ground for reusable systems.

If we make the first 20 minutes wonderful by adding one-off events, we only improve the first
20 minutes. If we make them wonderful by deepening promises, NPC memory, witness-gated deeds,
region generation, and consequence legibility, the whole game improves.

## Design Goals

The opening should teach the player the game's promise without explaining it as a feature:

- You can improvise with language.
- Ordinary objects and people are real material for magic.
- The world remembers what happened.
- NPCs carry rumors, secrets, debts, relationships, reported claims, and future hooks.
- Bound promises come true later, but not always where or how the player expects.
- The Empire is competent, readable, and frightening without being cartoonishly evil.
- Escaping by talk, trickery, theft, body control, or magic all count as real play.

The desired feeling is authorship, wonder, and mischief under immediate pressure. The player
should leave the opening with at least one story they feel they authored and at least two
future hooks they care about.

## Current Seed

The existing opening seed already has useful bones:

- a small imperial containment encounter
- two soldiers
- a brass containment brazier
- a readable containment notice
- loose items, including a tincture and cell key
- a locked cell door
- Lio of Hollowmere as a prisoner and possible follower
- a visible promise that realizes when the cell opens
- faction pressure and patrol response when public magic escalates

Keep this shape, but make it less like a small combat chamber and more like a compressed
Sorcerer situation: a place full of people, objects, claims, and unstable futures.

## The Promise-Rich Opening

The opening scene should deliberately create several promise opportunities from different
sources. These should not pre-place their rewards or destinations on the starting map.
Instead, they should write structured promise records that later generation can realize.

The player should be able to collect, reject, distort, or magically alter these hooks.
The exact content can vary by seed and origin, but the opening should usually expose three
to five promise vectors:

| Source | Example Claim | Likely Realization |
|---|---|---|
| Prisoner or local ally | "There is a reed-town east of here that still remembers wild names." | a generated town or settlement |
| Captive trader, thief, or clerk | "A pearl-handled knife was taken from me and tagged into evidence." | an item site, evidence cache, or merchant debt |
| Readable imperial record | "Unauthorized shrine activity has been observed near the walking stone." | a landmark, patrol route, or shrine site |
| Prop or fixture | the brazier records a contained name, oath, or prior sorcerer | a memory, ghost, claimant, or future debt |
| Player spell | "Let this door remember me." | a later door, witness, rumor, curse, or returning motif |

These are archetypes, not required exact beats. The important part is coverage: the opening
should show that promises can come from dialogue, documents, props, deeds, and spells, and
that all of them flow through the same ledger.

## First 20-Minute Beat Shape

### 1. Arrival Under Marble

The player begins in or near a containment yard, checkpoint, holding room, or evidence annex.
The situation should be legible in one glance: the Empire has procedure, names, keys, custody,
and rules; the player has unstable magic and a chance to make trouble.

The map should include several usable anchors:

- one official fixture, such as a brazier, seal, ledger, scale, or charter lattice
- one readable notice or incident record
- one locked or guarded threshold
- one visible captive or pressured NPC
- one tempting object that can be stolen, protected, traded, or spent as reagent fuel
- one environmental affordance tied to the region, such as Hollowmere reeds, marble blind
  spots, drainage water, market cloth, dust, bells, or bone charms

The opening must not require a single solution. Fighting, hiding, bribing, talking, stealing,
summoning, transforming terrain, or body control should all be plausible routes.

### 2. First Magic That Matters

Within the first few turns, the player should be tempted to cast a wild spell. The resolver
should have strong local anchors so the spell feels specific:

- containment brass
- legal notices
- cell iron
- river reeds
- confiscated items
- uniforms, ledgers, warrants, names, locks, shadows

The first successful cast should usually write at least one visible consequence beyond the
immediate effect: a witness reacts, a deed enters the ledger, a rumor seed is born, a patrol
resource is spent, an NPC's bond changes, or a promise binds.

### 3. People With Futures

The opening should include more than one social promise source. They do not all need to be
fully present as complex NPCs at first; even one fully interactive NPC plus one readable
record and one prop promise is enough for a first implementation. But the target is a small
cluster of people who offer different kinds of future:

- **The local prisoner:** knows a town, a person, or a safe path.
- **The confiscated-goods owner:** knows an item, debt, or evidence cache.
- **The imperial functionary:** knows a landmark, procedure, warrant, or weakness in the
  bureaucracy.
- **The unreliable witness:** saw something but cannot safely say it unless trust, fear, or
  magic changes the situation.

NPC information should first become reported claims with provenance. Some claims bind as promises
when they map to a buildable archetype. Vague color remains dialogue or memory. Concrete, salient
claims can become obligations the world can honor.

The gift-to-secret loop should be present in the opening. Giving an item should write a memory and
become context for later dialogue; the bond shift itself should be proposed by the dialogue
extractor when the NPC's response supports it. A prisoner given grave salt, a merchant given back
their confiscated charm, or a clerk bribed with gold can move from generic dialogue into a more
specific claim: a person, town, item, debt, or landmark that the promise system may later realize.

### 4. Escape Creates Reputation

The opening should be a reputation fork, not just a tutorial fight.

Possible player-authored outcomes:

- spectacular public wild magic: high notoriety, high imperial threat, maybe local awe
- quiet escape with witnesses confused: suspicion without full attribution
- rescue with restraint: gratitude and legitimacy
- massacre to save one captive: gratitude, fear, and imperial pressure at once
- theft without violence: merchant debt, Empire embarrassment, lower immediate bloodshed
- body swap or disguise: identity confusion, possible delayed attribution

The game should not label these as morality choices. It should simply remember who saw what
and let later systems read the result.

### 5. The First Payoff

Before the opening fully ends, at least one promise should visibly stir or partially realize.
This does not need to be the largest promise. The point is to teach the player that the ledger
is not flavor.

Good first-payoff examples:

- opening a cell realizes a bond or gratitude promise
- reading an altered notice reveals a newly bound location
- talking to an NPC after a gift produces a concrete site promise
- traveling east materializes a small landmark tied to a prior claim
- a spell-written promise attaches to a door, item, or rumor and appears in the journal

The first payoff should be understandable, local, and fast. Larger hooks can remain pending.

## Promise Archetypes For The Opening

These archetypes should be implemented generally so they can recur throughout the game.

### Item Promise

A source names a concrete object that is not on the current map.

Examples:

- a confiscated knife, pearl, reed charm, ledger key, bone flute, or charter lens
- a stolen heirloom
- a reagent cache
- a dead sorcerer's focus

Engine shape:

- `kind`: rumor, quest, debt, or prophecy
- `realizationKind`: item or item_site
- `subject`: item identity plus tags/material/value hints
- `claimedPlace`: directional or regional hint when available
- generation later creates an item entity, evidence cache, merchant stock, or guarded prop

The important payoff is not "free loot." It is that a named thing becomes findable and
mechanically usable.

### Town Or Refuge Promise

A source names a settlement, safehouse, camp, ferry, shrine-town, rebel hold, market, or
family house.

Examples:

- "Hollowmere will hide you if the reeds hear my name."
- "The Saltmarket still trades in unlicensed oaths."
- "There is a Thursday ferry that only arrives for liars."

Engine shape:

- `realizationKind`: site or town
- generation reserves or creates a zone with resident NPCs, faction posture, wares, and
  at least one local affordance
- the atlas can show a partial or mysterious hint without revealing every coordinate

The payoff is a new place to act, not a quest marker.

### Landmark Promise

A document, NPC, or prop names a notable world feature.

Examples:

- a walking stone
- a drowned bell tower
- an imperial blind spot
- a shrine that records names in water
- a road where patrols refuse to step after sunset

Engine shape:

- `realizationKind`: landmark or site
- generation creates a promise-site entity or region feature
- the site exposes at least one tactical affordance: cover, trade, recruitment, curse
  clearing, reagent source, witness manipulation, travel shortcut, or faction pressure

Landmarks must do something. A beautiful named rock is not enough.

### Person Promise

A source names someone who may appear later.

Examples:

- a sibling held at a checkpoint
- a clerk who signs every warrant
- a deserter who knows the patrol cadence
- a witness who will vouch for, betray, or misidentify the player

Engine shape:

- `realizationKind`: npc, claimant, follower_candidate, witness, or threat
- generation creates an actor with memory, faction, bond defaults, and a reason to care
- the person can participate in ordinary talk, gifts, recruitment, hostility, trade, or
  witnessing

Do not create bespoke quest NPCs. Create ordinary entities with promises and memories.

### Threat Or Debt Promise

A promise names a future cost.

Examples:

- "If you spend the pearl, its owner will come looking."
- "The door remembers your name, and so does the warrant."
- "A debt collector from tomorrow has accepted the charge."

Engine shape:

- `realizationKind`: threat, debt, patrol, curse, claimant, or scheduled event
- it can spawn an ordinary hostile, attach a curse, alter faction standing, or create a
  future bargaining NPC

Threat promises are crucial because they make "yes, at a price" feel real.

## What Should Be Data-Driven

The opening should be authored as data wherever practical:

- opening region identity and voice
- NPC promise-offer pools
- readable document templates
- prop/fixture promise hooks
- item promise templates
- site/landmark realization templates
- origin-specific opening variants
- seed-weighted substitutions

The engine should own the lifecycle and realization. Content data provides candidate claims,
tone, tags, and archetype hints; rules decide whether and where they bind.

## Required General Capabilities

To make this opening work without scripts, prioritize these general improvements:

- **Dialogue claim extraction:** post-dialogue model calls can extract reported claims from organic
  dialogue, then the engine decides whether they become memories, canon, stock reservations, or
  bound promises.
- **Gift -> memory -> dialogue -> bond/claim:** gifts create memory. Later dialogue can use that
  memory to propose bond changes and more specific, higher-confidence NPC claims without creating
  a bespoke secret handler.
- **Promise offering from documents and props:** `read` and `examine` can surface claim
  candidates from data, not hard-coded object ids.
- **Site and item realization during generation:** travel/worldgen can materialize promised
  items, landmarks, people, and towns.
- **Partial player-facing journal hints:** players see evocative fragments; debug/agent views
  see exact bound records.
- **Follower pathing ergonomics:** followers yield, swap, or step aside so social success does
  not become movement friction.
- **Consequence messages with causes:** when deeds, bonds, promises, or faction pressure change,
  messages should make the cause legible without exposing raw math.
- **Opening feel eval:** scripted and live-provider playtests should inspect whether the
  sequence produced at least one memorable spell and at least two future hooks.

Each item above is useful across the whole game.

## Avoid These Failure Modes

- Do not pre-place every promised thing in the opening map. That teaches the wrong lesson.
- Do not make promises mere quest text. They are engine-owned obligations.
- Do not require rescuing Lio, killing soldiers, or taking the key as the single route.
- Do not hide all consequences in debug views. The player needs to feel the world answer.
- Do not make social NPCs approval meters. Their bond math can exist, but the surface should
  be memory, posture, rumor, fear, gratitude, and risk.
- Do not make the opening a lore dump. Claims should imply action.
- Do not solve opening quality with one-off handlers for Lio, a specific door, or a specific
  town. If a beat matters, build the reusable mechanic beneath it.

## First Implementation Slice

A strong first slice does not need all archetypes at full depth. Build enough to prove the
loop:

1. Keep the containment-yard opening but add two more promise sources beyond the current cell
   door promise.
2. Add post-dialogue claim extraction so Lio can state a place/person/item claim in ordinary
   dialogue, with binding decided by the ordinary promise ledger.
3. Make one gift create a memory that later dialogue uses to improve the specificity, confidence,
   or bond consequence of an NPC claim.
4. Give the containment notice or an imperial ledger one landmark/evidence claim on `read`.
5. Give the brazier or another fixture one memory/threat/debt claim on `examine` or via
   player spell.
6. Make travel to the next new zone eligible to realize one of those promises as a site,
   item, NPC, or landmark.
7. Add player-facing messages and journal lines that show one claim settling or one promise
   stirring/realizing.
8. Add CLI and Godot access through the same commands: `talk`, `give`, `read`, `examine`, `journal`,
   `travel`, and ordinary casting.
9. Add tests around extraction, binding, generation realization, save/load round-trip, and CLI
   visibility.

The acceptance target is simple: after 20 minutes, a player should be able to say, "I escaped
in my own way, the world noticed, and now I know three strange things that might actually be
waiting for me."

## Test And Eval Expectations

Add focused tests and playtest scripts for:

- dialogue claim extraction from an NPC
- gift-improved claim specificity or confidence
- dialogue promise binding from an extracted NPC claim
- read/examine promise candidates from data
- item promise realization in a later zone
- landmark or town promise realization in a later zone
- promise relocation when the claimed place cannot be used
- no binding for vague, non-buildable claims
- follower movement after rescue
- witnessed, suspicious, and secret opening outcomes
- save/load preserving all opening promises
- replay reproducing realized promise content without provider calls

Add live-provider feel prompts such as:

```text
cast make the cell door remember the name I refuse to say
cast ask the brass brazier what it has swallowed
cast hide Lio's promise inside the posted notice
cast turn the key into a rumor that opens eastward
cast let the next town recognize the color of this escape
```

These should be judged for validity, specificity, consequence, surprise, and whether the
outcome maps to real operations.

## Success Criteria

The opening is working when:

- a new player understands by experience that promises matter
- a live-provider cast produces specific mechanical outcomes from local anchors
- at least three different promise sources are possible in the first area
- at least one promise can partially realize before or immediately after first travel
- rescue, stealth, violence, theft, and magic all produce different remembered histories
- the Empire response feels like resource-backed procedure, not a spawn script
- the player-facing log and journal make consequences legible
- the same sequence is playable through GUI and CLI
- the systems improved for the opening are reusable in later towns, ruins, markets, roads,
  and capital approaches

When this lands, the opening should feel less like "the first room" and more like the first
place in the world that decided to have opinions about you.
