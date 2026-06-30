# AGENTS.md

This project is Sorcerer, a standalone Godot 4 C# roguelike about wild, player-authored
magic. It is a clean-slate spiritual successor to Wild Magic, not a compatibility port.

The core rule:

> The model may interpret magic. The engine decides what is real.

Your job as an agent is to help build a clean, renderer-agnostic, agent-playable
simulation where strange spell prompts become reusable mechanics.

## What We Are Building (read `docs/GRAND_VISION.md`)

The pitch is one line: **you can do anything, and it actually matters what you do.** Sorcerer
is a long roguelike where the player improvises wild magic in a living world that keeps
answering. The feeling, in priority order, is **authorship, wonder, and mischief**, then
tactical cleverness - danger is real, but this is not a punishing survival game. The arc is
long: grow in power, weaken the Empire, and eventually kill the emperor. No meta-progression;
each death rolls a new world.

The setting is **color under marble**: a lush, peculiar, culturally-specific world of folk
traditions, policed by the cold, reasonable, genuinely-not-evil Empire of Vigovia. You are the
pest. The tone is **wonder with teeth** - ecstatic, feral magic with strange, gorgeous costs,
never generic grimdark. See `docs/WORLDBUILDING.md` (canon) and `docs/AESTHETICS_AND_TONE.md`
(voice).

**The one engine idea: prose becomes mechanics.** There is no wall between flavor and rules. A
description - a trait, a rumor, a secret, a name - is a *dormant mechanic* that becomes real
when the model, resolving an action, decides the situation squarely calls for it. The player
improvises in language; the model proposes meaning; the engine validates, prices, and applies
it as authoritative truth. Freedom on the surface, coherence underneath - **structured
freedom**.

**Richness comes from interaction, not from scripted content.** We build a small number of
general systems - wild magic, items and entities, traits, promises, deeds and reputation,
factions, relationships, regions - and let them collide. A spell witnessed by a villager
becomes a rumor becomes a recruit; an imbued gift becomes a bond becomes a confided secret
becomes a place the world then delivers. None of that is coded as a story; it falls out of
systems handing off to each other. So when you build a mechanic, ask whether it **creates more
combinations than it adds complexity**, and build the smallest *general* capability that
unlocks it - never a handler for one spell or one story.

## First-Class Requirements

- The authoritative engine lives in Godot/C#.
- The game must ship standalone without a Python runtime.
- GUI and CLI must drive the same backend.
- The CLI must be capable enough for AI agents to fully play and playtest the game.
- Renderer code must be replaceable. ASCII is the first renderer, not the architecture.
- Entity unification is central.
- Body swap is central.
- Props and items should be unified under the same entity/component model wherever
  practical.
- Provider code must support local models and API-key providers behind one interface.
- Mock providers exist for fast agent and regression playtests, not as the design target.
- Promises and prophecy are foundational.
- Wild magic quality is a first-order feature, not an implementation detail.

## Do Not Split The Game

Sorcerer has at least two interfaces:

- Godot GUI: the human-facing game.
- Headless CLI: the JSON-first agent-facing game.

They must stay exactly synced because they must call the same `GameSession` and engine
logic. A player-facing capability that exists only in the GUI or only in the CLI is a bug.

When adding a command, menu flow, spell mechanic, inventory feature, character flow,
dialogue action, or world-system readout, update both the GUI path and CLI path in the
same change.

Agents may receive perfect debug state in the CLI. The CLI does not need to imitate human
perception unless a test explicitly asks for a player-knowledge-only view.

## Design Principles

- Prefer general mechanics over prompt-specific behavior.
- Prefer schema repair and normalization over hard-coded spell fallbacks.
- Prefer data-driven operation metadata over scattered magic strings.
- Prefer actor-agnostic actions over player-only action paths.
- Prefer read-only state views over renderer access to rule internals.
- Prefer durable state lanes over free-text flags that become invisible rules.
- Prefer small composable effects: damage, status, terrain, movement, summoning,
  faction changes, tags, memory, traits, inventory, curses, promises, triggers, timers.
- Keep technical LLM failures from consuming turns.
- Keep intentional magical rejections turn-consuming.
- Keep background generation resource-aware and user-configurable.
- Keep costs normally post-cast.
- Do not build full engine spell-budget auto-pricing early.
- Do not add LLM calls for trade intent; use engine-side interaction rules and explicit
  commands instead.

## Engine Authority

LLM output is untrusted. It must go through:

1. parse
2. repair common shape mistakes
3. normalize aliases and references
4. validate operation types and fields
5. preflight costs and targets
6. apply transactionally
7. validate resulting state
8. produce action results and audit records

Do not let renderer code, prompt code, or provider code directly mutate world state.

## Entity Unification

Everything interactable should be represented as an entity with components:

- controlled bodies
- NPCs
- enemies
- items
- props
- books
- doors
- corpses
- husks
- summoned beings
- magical anchors
- promise sites

Avoid parallel systems where "item", "prop", "NPC", and "enemy" each reinvent identity,
position, rendering, targeting, persistence, and inspection.

Body swap should be a natural consequence of the model:

- control points to an entity id
- inventory stays with the body
- stats and appearance come from the body
- the vacated body becomes an entity with no agency
- the CLI, GUI, FOV, resolver context, and camera follow the controlled entity

## Renderer Boundary

Game rules must not depend on Godot rendering nodes.

Renderers consume read-only views such as:

- map tiles and glyphs
- visible entities
- player status
- inventory and equipment
- selected target
- messages
- journal and promises
- LLM/debug status

It should be possible to add a tile renderer later without changing combat, spell
application, inventory, AI, or world generation.

## CLI And Agent Playtesting

The CLI should expose structured observations and action results for agents. Human-readable
commands are fine, but agents should not need screen scraping.

Good agent-facing affordances:

- `--provider ollama` for ordinary playtesting and resolver quality evaluation
- `--provider mock` for quick deterministic troubleshooting, evals, and regression checks
- `--json` for machine-readable observations/results
- `--command`, `--script`, `--transcript`, and `--episode-log` for reproducible runs
- `inspect` or equivalent structured state view
- optional perfect debug state
- local coordinate map
- explicit commands for inventory, targeting, talking, reading, promises, and standing
- stable action-result fields: success, action, consumedTurn, technicalFailure,
  turnBefore, turnAfter, messages, resolution, errors

## Background Jobs

Background generation can exist, but it must not secretly starve the foreground game.

Design it with:

- max concurrent background jobs
- separate foreground/background provider settings
- easy disable flags
- visible developer queue
- audit logs
- clear replay/save behavior for generated outputs

## Documentation Requirements

Keep durable docs current when you add or change architecture:

- `README.md`
- `NORTHSTAR.md`
- `AGENTS.md`
- `docs/ARCHITECTURE.md`
- `docs/DOCUMENTATION_MAP.md`
- `docs/GRAND_VISION.md`
- `docs/CLI_AND_AGENT_PLAYTESTING.md`
- `docs/WILD_MAGIC_CONTRACT.md`
- `docs/MAGIC_RESOLVER_ARCHITECTURE.md`
- `docs/SEMANTIC_EFFECTS.md`
- relevant subsystem docs

Do not leave important behavior only in private notes, chat history, or comments inside
temporary code.

## Testing Expectations

For engine changes, add focused tests around:

- turn consumption
- state validation
- transaction rollback
- target binding
- action results
- CLI parity
- renderer independence
- LLM technical failure behavior
- intentional rejection behavior
- body swap and controlled-entity behavior

For Godot GUI changes, verify that the same behavior is reachable through the CLI.

## Development Style

- Read existing docs before editing.
- Keep changes scoped.
- Do not add one-off handlers for a single spell phrase.
- Do not build GUI-only mechanics.
- Do not let malformed LLM output partially mutate state.
- Do not hide a second spell engine inside fallback code.
- Do not make semantic flavor mechanically binding unless it crystallizes into an
  explicit engine operation. Entity traits are dormant mechanics the resolver may surface;
  see `docs/SEMANTIC_EFFECTS.md` (semantic by default, mechanical on demand, never on the
  critical path; traits are resolver-authored only).
- Do not let magic become merely valid but boring. When improving the resolver, measure
  specificity, consequence, and surprise as seriously as JSON correctness.
- Use clear, durable names based on behavior, not temporary planning labels.

When in doubt, build the smallest general system that unlocks ten weird spells.
