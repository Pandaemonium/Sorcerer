# Game Vision

Sorcerer is a long-run roguelike about wild magic, player authorship, and meaningful
consequence.

The short pitch:

> A game where you can do anything, but it actually matters what you do.

## Primary Feel

Sorcerer should emphasize:

- wonder
- mischief
- narrative authorship
- tactical cleverness
- danger and survival pressure, but not punishing survival pressure

The player should feel that they are exploring a world that can be touched in almost any
way. The important promise is not merely "type anything." The promise is that strange
player choices become visible, durable, and consequential.

## Genre Shape

Sorcerer is primarily a roguelike:

- grid-based movement
- turns
- procedural runs
- tactical danger
- death as an ending
- fresh runs with new worlds

It is not mainly a survival game. It is not a life simulator. It is an exploratory,
emergent-story roguelike where the player drives a unique narrative through action.

## Power Curve

The power curve should be wildly swingy.

The player should sometimes feel overpowered. They should sometimes be scared for their
life. The game should not feel hopelessly deadly. The strongest moments are when a risky
spell, clever trick, social maneuver, body swap, or environmental use changes the shape of
the story.

Wild magic should be the most powerful force available to the player. It should also feel
like a dangerous collaborator. High Composure may make the player more controlled and
charter-like, but wild magic should never become completely tame by default.

## Run Structure

Runs should be long.

The long-term win condition is to kill the emperor. The path to that win should involve a
mix of:

- becoming personally powerful
- weakening imperial power
- gaining access to the emperor
- trickery
- diplomacy
- reputation
- promises and prophecy
- making allies or destabilizing enemies
- surviving the consequences of wild magic

These systems do not all need to exist at first. The architecture should leave space for
them from the beginning.

There is no meta-progression. Each run is a fresh start in a fresh world. Grave markers,
run chronicles, or other memorials can exist, but they should not grant power to future
runs.

## Ten-Minute Session Target

A good ten-minute session might include:

- winning or avoiding a fight
- casting at least one memorable wild spell
- receiving or completing a quest
- earning a reward
- talking to an NPC
- discovering an object, promise, clue, or place that might matter later

Combat should be common enough to give the player excuses to use wild spells, but not so
constant that local-model latency becomes a chore. Combat should also carry social risk:
who saw it, what faction was harmed, and what the Empire learns can matter.

## Noncombat Paths

Avoiding combat should be real play, not a consolation prize.

Supported paths should include:

- wild magic
- stealth or cover
- diplomacy
- misdirection
- bribery or trade
- promises and bargains
- body swap or possession, rarely
- terrain and prop manipulation
- faction manipulation

The same backend should support these paths through engine-owned actions and state views,
not through renderer-only or prose-only shortcuts.

## Charter-Style Magic

Reliable non-LLM magic can exist. It should feel charter-like: precise, repeatable,
weaker, and less creatively open than wild magic.

These spells give the player tools between wild casts, but they should not replace the
main fantasy. Most of the truly strange, powerful, world-changing actions should require
wild magic.

## What Failure Looks Like

The biggest design failures are:

- boring magic
- bad LLM outputs
- consequences that do not matter
- UI/CLI divergence
- a world that feels random instead of reactive
- combat that becomes repetitive latency
- hidden model behavior that mutates state outside the engine

Sorcerer should be engineered around preventing those failures.

