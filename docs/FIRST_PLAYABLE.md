# First Playable

The first playable build should prove the heart of Sorcerer: combat with an excellent live
wild-magic resolver, through both Godot GUI and JSON-first CLI.

It should be small, but it should be honest. Do not build a large world before the magic
feels worth building around.

## Goal

The first playable should let a human and an AI agent:

- start a run
- see a small ASCII test chamber
- move and target with the same backend
- fight a few entities
- inspect entities and objects
- cast live wild magic through a provider
- see validated effects, costs, and messages
- protect treasured inventory
- create or inspect at least a simple promise
- inspect audit records after casts
- run the same scenario through CLI

## Required Pieces

- Godot 4 C# project skeleton.
- Plain C# core with `GameSession`, `GameEngine`, `GameState`, `GameCommand`,
  `ActionResult`, `GameView`, and `AgentObservation`.
- Separate JSON-first CLI executable referencing the same core.
- ASCII Godot renderer consuming `GameView`.
- Mouse targeting in the GUI.
- Test chamber scenario.
- Unified entity model for actors, props, items, and at least a few addressable fixtures.
- Basic movement, collision, combat, HP, mana, statuses, and messages.
- Live provider path for wild magic.
- Mock provider path for fast tests.
- Capability-card resolver architecture.
- Audit logs for every live model call.
- Protected/treasured inventory.
- Minimal promise ledger.
- Basic agent playtest script or harness.

## Magic Scope

The first wild-magic surface should be narrow enough to implement well:

- damage
- healing
- mana restore
- push/pull
- teleport
- status
- terrain/tile creation
- summon
- transform entity
- transform item/prop
- message
- create promise
- costs: mana, health, max health, max mana, item, curse, status

The priority is quality over breadth. A smaller operation set that resolves creatively is
better than a large set that produces brittle or boring results.

## Deferred From First Playable

- full spell economy auto-pricing
- learned spells and favorites
- saves
- deep faction simulation
- full body-swap content
- item identification
- trade LLM calls
- broad town generation
- elaborate replay and rewind
- casting minigames
- full promise realization across many regions

## Acceptance

The build is successful when:

- the GUI and CLI both use `GameSession`
- a player can finish a small fight using wild magic
- a live provider can produce memorable valid spells
- malformed provider output does not mutate state
- a protected item cannot be silently consumed
- every cast writes an audit record
- agents can run a scripted JSON episode
- renderer code does not own rules
- the team feels excited to make the resolver richer

