# Grand Vision

This is the central guiding document for Sorcerer: what the game should play like, the world
it builds, and how a handful of general systems collide to produce stories no one scripted.
It supersedes the earlier GAME_VISION and sits above the detailed design docs, which it points
into. It is intentionally not technical. It describes the game we are building toward.

## The Promise

> You can do anything, and it actually matters what you do.

Sorcerer is a long roguelike about wild, player-authored magic in a world that remembers. You
type what you want the world to do; a language model proposes what happens; a deterministic
engine decides what is real. The first half of the promise is freedom - you can reach into the
world and attempt almost anything. The second half is the one that makes it a game worth
playing: the world answers, and keeps answering, long after the spell has faded.

The feeling, in order of priority, is **authorship, wonder, and mischief**, then tactical
cleverness. Danger is real but not the point; this is not a punishing survival game. It is an
exploratory game where you drive a unique, meaningful story by acting on a world that is
unusually willing to be acted upon.

The power curve is **wildly swingy**. There are moments of feeling like a god and moments of
running for your life. The strongest moments are when a risky spell, a clever trick, a social
maneuver, or an environmental gambit changes the shape of the story.

## The World

The world is **color under marble**.

Most of it is wild and lush: jewel-toned bazaars, bone-singing holds, crystal queendoms,
desert caravans tattooed with sound-magic, drowned merfolk rites. Magic here is one raw, living
substrate, and the famous traditions - blood, bone, crystal, woven, song, curse - are not
different magics but different peoples' ways of reaching the same wild fire. (See
WORLDBUILDING.md for the canon and AESTHETICS_AND_TONE.md for the voice.)

Over all of it lies the Grand Empire of Vigovia: marble, brass, clean geometry, permits and
ledgers. The Empire is **genuinely not evil** - its peace is real, its charter magic really did
prevent catastrophes, and ordinary people credibly prefer it. It exterminates wild mages with
the bored competence of pest control. You are the pest. The menace is that it is reasonable,
and that the more you fight it, the more certain it becomes that wild magic must be destroyed.

Two textures we will protect:

- **Wonder with teeth.** Wild magic is ecstatic, sensory, and feral, and it does not entirely
  love you back. Backfires can be gorgeous. Costs are strange and consequential, never merely
  punitive.
- **Every object is potential grammar.** The world is physically legible and temptingly usable.
  A brass brazier, a posted notice, a loose pearl, a corpse, a tariff-scale - anything you can
  see, you can try to make part of a spell. The richness of the world is not decoration; it is
  raw material.

Weirdness scales with wild-magic saturation: imperial provinces feel surveyed and sensible;
deep wild places go dreamlike. Strangeness is a map gradient and a navigational signal.

## What It Plays Like

**Moment to moment.** You explore a specific, culturally-situated place. You win or avoid a
fight; you cast at least one memorable wild spell; you talk someone into something, steal
something, or imbue something; you catch a rumor, take a quest, earn a reward, find an object or
a clue that might matter later. Combat is common enough to invite wild spells but not so
constant that it becomes a chore of waiting on the model - and it carries social risk: who saw
it, which faction was harmed, what the Empire now knows.

**The long arc.** Runs are long. You win by killing the emperor, and the path there is a mix of
growing personally powerful, weakening imperial control, and maneuvering into reach - by force,
trickery, diplomacy, reputation, prophecy, alliance, or some stranger route. None of it is
handed to you on a quest marker; it accretes from what you do.

**No meta-progression.** Each death is a real ending, and the reward for it is a wholly new
world - a fresh geopolitics to read and master. Past runs may leave grave markers or
chronicles, but never inherited power.

**Avoiding combat is real play, not a consolation.** Diplomacy, stealth, trickery, bribery,
body swap, terrain manipulation, and faction play are all supported through the same engine and
the same state, never through prose-only shortcuts.

**Charter magic** is the reliable counterpoint: precise, repeatable, weaker, and far less open
than wild magic. It gives you tools between wild casts without ever replacing the main fantasy.
The strange, powerful, world-changing things should require wild magic.

## The One Engine Idea: Prose Becomes Mechanics

Here is what makes Sorcerer different from a roguelike with a spell list.

In a normal game there is a hard wall between flavor and mechanics. A "righteous,
imperial-hating dagger" is just a string until a designer writes a rule for it. Sorcerer has no
such wall, because the thing that resolves spells and reads the world is a language model that
**re-reads descriptions at the moment of decision**. So a description is not flavor and not yet
mechanics - it is a **dormant mechanic**, waiting for the context where it becomes relevant.
(See SEMANTIC_EFFECTS.md.)

This is the membrane the whole game lives on:

> Purely semantic content - a description, a trait, a rumor, a secret, a name - becomes
> mechanical when the model, resolving an action, decides the situation squarely calls for it.

The player improvises in language; the model translates intent and meaning into proposed
mechanics; the engine validates, prices, and applies them as authoritative truth. Freedom on
the surface, coherence underneath. We call it **structured freedom**: you can reach into the
world and do something impossible, but the engine always knows what changed, who saw it, what it
cost, and what might answer later.

Two disciplines keep this from collapsing:

- **The engine owns truth.** Model output is an untrusted proposal until it is normalized,
  validated, priced, and applied transactionally. The model never mutates the world directly.
- **Semantic by default, mechanical on demand, never on the critical path.** Flavor is free and
  promises nothing; it costs only when cashed into a real effect; and the player must never
  *need* a semantic payoff to progress. This lets us be lavish with weird, specific content
  without making reliability hostage to it.

## The General Systems

We do not build stories. We build a small number of general systems and let them interact. Each
is simple on its own; the game is in the collisions. Every system is described here by what it
unlocks for play, not how it is coded.

- **Wild magic** is the crown jewel: type almost anything, and the world tries to make it real
  through reusable operations. **Casting performance** (a quick optional minigame) tilts a cast
  between power and wildness; **costs** are usually revealed only after casting, and reaching too
  high can demand something terrible. Overreach is answered with severe cost, not a refusal - the
  instinct is "yes, at a price."

- **Charter magic** is the reliable, weaker counterpoint, for tools between wild casts.

- **Everything is an entity.** Bodies, enemies, NPCs, items, props, books, doors, corpses,
  shrines, and summoned things are all entities with components, so wild magic can address any of
  them through one reference system. **Items** are fuel, focus, sacrifice, or target; a prized
  pearl can power a spell or be kept to color future ones. **Treasured inventory** protects what
  you refuse to risk. **Body swap** is rare and severe: personhood follows the soul, the body
  carries inventory and stats.

- **Traits** are the semantic layer made concrete: descriptions hung on entities that the
  resolver may weigh, and occasionally promote into standing mechanics.

- **Promises and prophecy** let the world commit to a future it will keep. A spell, a rumor, a
  secret, or a quest can write a durable obligation - "somewhere north, a blade waits with your
  name on it" - and the world honors it. Not everything said binds, but everything bound comes
  true. Promises stay mysterious to the player and inspectable to the engine.

- **Deeds, legend, and reputation.** What you do is recorded, gated by who saw it, and distilled
  into a legend - a few weighted tags the whole world reads. Reputation is multidimensional:
  notoriety, fear, gratitude, legitimacy, uncanniness, imperial threat. One act lands differently
  on each. Your legend follows your soul, not your body.

- **Factions and the Empire.** Powers hold multidimensional standing and finite resources, so
  their reactions are expenditures - a crackdown is the Empire spending a patrol, not a meter
  crossing a line - and pressure ebbs and flows. The Empire is the slow antagonist whose heat
  rises with your legend and whose defenses must be spent down before the emperor is reachable.

- **Relationships, bonds, and organizations.** Every NPC carries a personal bond to you, separate
  from their combat side and any guild. You can found organizations or climb them. Devotion,
  drift, departure, and betrayal emerge from the bond model and your legend; they are never
  scripted, and the math stays invisible - it should read as relationships, not approval bars.

- **Regions, traditions, and a fresh world each run.** Every run rolls a new geopolitics from the
  seed - which realms are conquered or defiant, who rules them, where each tradition survives -
  and every rolled feature implies a tactical affordance you can act on. The structure is
  procedural; the model names and flavors it.

- **The narrator** is the lush voice: rumors that greet you by reputation, the weary Censorate
  clerk's escalating memos, situation reports, the run's closing chronicle, and
  consequence-bearing detail (a wanted poster bearing your legend, a shrine raised to what you
  did). The model interprets meaning and narrates reaction; it never simulates - the
  deterministic skeleton stands without it.

- **Character.** You are an unusually strong sorcerer, not a chosen one. Three stats - **Vigor,
  Attunement, Composure** - shape your body, your potency, and how hard wild magic bites back;
  Composure is the wildness dial. Origins tie you to a tradition and seed how the world first
  reacts.

## The Engine Room: Richness From Interaction

The thesis of the whole game: **a new system should create more combinations than it adds
complexity.** We get richness not by writing content but by letting general systems hand off to
each other. The stories below are not features we coded; they are what falls out when the systems
above touch.

### Vignettes

**The spell that recruited a stranger.**
In an occupied Brall hold you cast something small and wild - you make the tavern's hanging
whale-bones hum a name. A villager sees it. He says nothing to you, but that night he tells a
friend, and the tale grows in the telling, the way Bralli tales do. Days later the friend finds
you: he has an Empire grudge of his own, he has heard what you can do, and he wants in. You never
sought him. The chain did: *wild magic -> a witness -> a rumor that propagated and distorted -> a
legend -> a personal bond -> a recruit for your cause.* Several systems, no script.

**The gift that became a secret that became a place.**
You imbue a plain Vintan scarf with a small wild charm - it warms when someone nearby lies - and
give it to a fence you have been cultivating. The gift lifts her bond to you. Trusting you now,
she lets slip a secret: her brother is held at an imperial checkpoint two valleys east. That
secret does not stay talk. It binds as a promise, and the world honors it - the next time you
travel east, the checkpoint is really there, her brother really inside, the guards really
expecting no trouble. *Wild magic on an object -> a trait -> a gift -> a bond -> a confided secret
-> a bound promise -> a realized place you can act on.*

**The mercy that made you a monster.**
You wild-magic a whole imperial squad to death in a crowded market to save one prisoner. The
rebels toast you; the townsfolk who watched are afraid of you; the Empire files you as a priority
and spends a second squadron. And a follower who loved you for your mercy is quietly unsettled by
your butchery - the deed writes a note on her that every future bond check reads, and weeks later,
at a worse moment, it is part of why she leaves. *One act -> gratitude and fear and imperial
threat at once -> faction heat -> a durable mark on a relationship -> an eventual, earned
betrayal.* The same systems that recruit can also cost you.

### The interaction map

The recurring hand-offs that produce emergence. Each is a general capability, not a special case:

- wild magic on an ordinary object -> a minted trait -> something the world can later use
- any visible act -> a deed gated by witnesses -> a rumor -> a multidimensional legend
- legend -> faction standing -> recruitment, hostility, and the Empire's rising heat
- items and traits -> gifts and leverage -> personal bonds
- bonds + dialogue -> confided secrets and commitments -> bound promises
- promises -> world generation and timed events -> futures the world actually delivers
- deeds -> the Empire spends finite resources -> escalation that ebbs, and a path to the emperor
- bonds + your legend + your deeds -> devotion, drift, and betrayal, none of it scripted
- body swap -> control moves, but legend and reputation stay with the soul
- region roll -> tactical affordances -> a different game to read every run

When two systems can hand off like this, a third kind of story appears that neither could tell
alone. That third story is the game.

## What We Are Building Toward

This document describes the **final** game, not the next build. We get there incrementally (see
FIRST_PLAYABLE.md and IMPLEMENTATION_PLAN.md), always with a playable slice, always with the
resolver excellent before the world grows around it.

The disciplines that keep the dream coherent as it grows:

- **The engine owns truth;** the model proposes meaning and paints reaction, and never simulates
  or mutates directly. The deterministic skeleton must stand with zero model calls.
- **General systems over scripted content.** When tempted to handle one spell or one story, build
  the smallest general capability that handles ten.
- **Legibility is a feature.** Emergence the player cannot perceive is indistinguishable from
  randomness; we budget as much on showing the world react as on computing it.
- **One backend, fully agent-playable.** Everything humans can do, an agent can do through the
  CLI, so the game can be played and tested at scale.

We will know we have it when wild magic is memorable more often than it is merely valid, when a
stranger approaches you over something you barely remember doing, and when the world feels less
like a level and more like a place that has decided to have opinions about you.

### What failure looks like

The dream dies if we get these wrong, so we engineer against them:

- boring magic, or bad model outputs
- consequences that do not actually matter
- a world that feels random instead of reactive
- combat that becomes repetitive waiting on the model
- reputation or relationships that read as stat bars to grind
- the GUI and CLI drifting apart
- the model mutating state outside the engine
