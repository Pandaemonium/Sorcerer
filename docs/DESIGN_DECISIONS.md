# Design Decisions

This document records durable decisions for Sorcerer so implementation work does not
have to recover them from chat history.

## Settled Decisions

- Sorcerer is a clean-slate spiritual successor to Wild Magic.
- The project name is Sorcerer.
- The implementation target is Godot 4 with C#.
- The game should ship standalone without a Python runtime.
- The authoritative engine lives in C# inside the Godot project, not in an external
  Python backend.
- The GUI and CLI must use the same `GameSession` backend.
- The CLI is first-class, JSON-first, and must be strong enough for AI agents to fully
  playtest.
- The CLI should be a separate .NET console executable referencing the core.
- The core should avoid Godot-specific types where practical.
- Agents may access perfect debug state.
- ASCII is the first renderer.
- The renderer must remain separate from game state and rules.
- Entity unification remains central.
- Everything meaningful that magic can target should be an entity, including props,
  items, doors, books, corpses, fixtures, and eventually addressable wall or terrain
  features.
- Body swap remains architecturally central but should be rare in play.
- Personhood follows the soul; inventory follows the body.
- Props and items should be unified wherever practical through components.
- Provider support should include local providers and API-key providers behind one
  interface. Ollama can be supported, but should not be the only real path.
- Mock providers exist for fast testing and agent playthroughs.
- Background generation remains part of the vision, should default low, and must be
  configurable and resource-aware.
- Deterministic replay is desirable if it stays cheap. It is not a hard requirement
  if it imposes large architectural cost.
- Debug and audit tools should exist early as developer affordances, even if they later
  become hidden screens or separate tools.
- Runs should be long.
- The eventual win condition is killing the emperor.
- There is no meta-progression; grave markers or chronicles are acceptable memorials.
- The Empire is not reformable in the main arc. It can be destroyed, evaded, or hidden
  from.
- Promises and prophecy are foundational.
- Costs are normally revealed after casting.
- Full engine-side spell-budget auto-pricing is deferred until much later.
- Protected/treasured inventory is a core mechanic.
- Combat should be meaningful but not constant.
- Wild magic should be the most powerful magic and should remain central.
- Reliable charter-style spells can exist, but should be less powerful and less open than
  wild magic.
- LLM trade-intent calls should be cut; trade should be driven by explicit interaction and
  engine-side rules.
- The first playable should prioritize combat with an excellent live wild-magic resolver.
- Content should bias data-first for future modding.
- Renderer animation should prefer engine `StateDelta` records.

## Architectural Biases

Sorcerer should optimize for:

- cleaner architecture over compatibility
- general mechanics over prompt-specific handlers
- visible contracts over hidden global behavior
- narrow provider interfaces over model-specific coupling
- explicit state views over renderer access to internals
- actor-agnostic actions over player-only paths
- lightweight replay artifacts over heavy rewind machinery
- JSON-first agent surfaces over terminal prettiness
- audits and evals over guessing at resolver quality
- player-reactive emergence over detailed life simulation

## Compatibility Position

Sorcerer does not need to reproduce Wild Magic behavior exactly.

It should preserve the best ideas:

- player-authored wild magic
- engine-authoritative validation
- CLI and GUI parity
- agent playability
- reusable effect operations
- entity unification
- body swap
- promises and prophecies
- local model support
- background generation

It should freely redesign implementation details that became tangled, premature, or
hard to explain.
