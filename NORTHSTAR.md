# Sorcerer North Star

Sorcerer is a standalone Godot 4 C# roguelike about wild, player-authored magic.

The player can type nearly any spell idea. A local language model may propose what
happens, but the deterministic game engine owns legality, cost, validation, state
mutation, and the final truth of the world.

> The model may interpret magic. The engine decides what is real.

Sorcerer is a clean-slate spiritual successor to Wild Magic. It should learn from
that prototype without preserving compatibility for compatibility's sake. This is
the chance to build the shape the game should have had if it had started with a
renderer-agnostic, agent-playable, distributable Godot architecture.

Short pitch:

> A game where you can do anything, and it actually matters what you do.

## First Principles

- The player can try anything, and it might work, but might have a terrible cost.
- The world responds to the player's actions.
- The world is unbelievably, shockingly lush and rich.
- The engine is authoritative.
- The renderer is replaceable.
- The CLI and GUI use the same backend.
- The game is fully playable by AI agents.
- The CLI is JSON-first and can expose perfect debug state for agents.
- Strange prompts become reusable mechanics, not one-off handlers.
- Rich world state is exposed through narrow read-only views.
- LLM output is untrusted until normalized, validated, and applied transactionally.
- Background generation enriches the world but must not steal control of the turn loop.
- Entity unification is central: bodies, NPCs, enemies, props, items, doors, books,
  corpses, shrines, summoned creatures, and carried objects are all entities with
  different components.
- Body swap is not a special case. The controlled actor changes; the rest of the
  simulation follows that pointer.
- Promises and prophecy are foundational, not late decorative systems.
- Costs are normally discovered after casting.
- Wild magic is more powerful and more dangerous than reliable charter-style magic.

## The Core Fantasy

You are not selecting spells from a list. You are improvising with a living, dangerous
world.

You can ask for blue glass teeth, a debt collector from tomorrow, a shrine that
remembers your name, a brass moth familiar, an enemy's oath turned into vines, or a
door that opens only for a lie. The game should not hard-code those prompts. It should
own a growing set of mechanics that can express them:

- damage and healing
- movement and forced movement
- terrain and tile fields
- statuses and curses
- summoning and animation
- body swap and possession
- item and prop transformation
- memory, reputation, and faction changes
- promises, prophecies, and delayed consequences
- durable traits and world notes
- triggers, auras, and timed events

The game becomes broader by adding general primitives, not by adding spell-specific
exceptions.

The intended feel is wonder, mischief, and narrative authorship first, then tactical
cleverness. Danger and survival pressure should exist, but Sorcerer is not primarily a
punishing survival game. It is an exploratory roguelike where the player drives a unique,
meaningful narrative through action.

Runs should be long. The eventual win condition is to kill the emperor by growing in power,
weakening imperial control, and finding a way to reach him through force, trickery,
diplomacy, prophecy, or some stranger route.

There is no meta-progression. Future runs may remember past runs through grave markers or
chronicles, but not through inherited power.

## Tone

Sorcerer should keep the best tonal lesson from Wild Magic: wonder with teeth.

Wild magic is ecstatic, sensory, alluring, and feral. It is not generic dark fantasy.
Backfires can be beautiful. Costs should feel strange and consequential, not merely
punitive.

The world should contrast vivid folk magic, regional weirdness, and creaturely
specificity against ordered systems of control. The exact setting can evolve, but the
central texture should remain:

- wild magic: colorful, improvisational, bodily, joyous, unstable
- institutional magic: precise, legalistic, beautiful, cold, repeatable
- dungeons and wild places: peculiar, culturally specific, discoverable
- consequences: persistent enough to matter, open enough to surprise
- the Empire: marble, order, law, and containment
- the rest of the world: color, tradition, argument, and unruly life

## Non-Negotiable Design Contracts

### One Backend

Human GUI play, headless CLI play, automated AI playtests, and tests must all call the
same session/action layer.

Any player-facing action available in the GUI must be reachable through the CLI. A
feature that exists in only one interface is a bug.

### Renderer-Agnostic State

Game rules do not import renderer code. Renderers consume read-only views:

- map view
- actor/entity cards
- inventory/equipment views
- message log
- target/selection state
- pending LLM/debug state
- world and journal summaries

ASCII is the first renderer, not the only renderer.

### Agent Playability

AI agents must be able to play the full game through a stable CLI without screen
scraping. The CLI must expose enough state for navigation, tactical decisions, spell
experiments, bug reproduction, and long unattended playtests.

The agent interface should prefer structured observations and action results over
human-only prose, while still keeping text commands pleasant for human debugging.

### Engine-Owned Magic

The model returns proposed structured JSON. The engine:

1. parses and repairs common shape mistakes
2. normalizes aliases and references
3. validates the operation surface
4. checks costs, bounds, targets, and curse limits
5. applies effects transactionally
6. advances the turn only when appropriate
7. logs what happened

Technical failures do not consume a turn. Intentional magical rejections do.

Overpowered spells should usually happen with severe cost rather than be rejected by
default. Full engine-side spell-budget pricing is deferred until the game has enough feel
to price magic well.

### Clean-Slate Simplicity

Do not port complexity just because it exists. Keep the ideas that strengthen the game:

- entity unification
- agent-playable backend
- props and items as one interactable model
- resolver context and operation contracts
- prophecy and promise systems
- background generation with resource controls
- auditability

Rebuild them in simpler forms where the prototype grew tangled.

## Long-Term Shape

The ideal Sorcerer architecture lets the team add a new mechanic by touching a small,
predictable set of places:

- operation metadata
- validator
- applier
- resolver card/prompt guidance
- state view context, if needed
- CLI/GUI presentation, if player-facing
- tests and agent playtest scripts

It should not require branching through renderer code, prompt monoliths, command
special cases, and hidden state mutations.

## Success Criteria

Sorcerer succeeds if:

- a human can play it in Godot
- an AI agent can play the same game through CLI
- weird spell ideas usually become coherent mechanical outcomes
- unsupported prompts fail gracefully and visibly
- the engine remains deterministic enough for debugging
- a new renderer can be written without changing rules
- local model configuration is user-controlled
- local and API-key providers both fit behind one interface
- the codebase feels cleaner after each new system, not more brittle
- the magic is memorable more often than it is merely valid
