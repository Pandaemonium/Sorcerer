# Sorcerer

Sorcerer is a standalone Godot 4 C# roguelike about wild, player-authored magic.

The player can type almost any spell idea. A local LLM proposes a structured magical
resolution, then the engine validates and applies it. The engine remains authoritative:
the model interprets intent, but never directly mutates game state.

Sorcerer is a clean-slate spiritual successor to Wild Magic. It is not a compatibility
port. The goal is cleaner architecture, eventual distribution, and a simulation that is
equally playable through the Godot GUI and a headless CLI for AI agents.

The short pitch:

> A game where you can do anything, but it actually matters what you do.

## Design Rule

> The model may interpret magic. The engine decides what is real.

## Goals

- Build the authoritative engine in Godot 4 C#.
- Ship as a standalone Godot game with no Python runtime.
- Keep the renderer fully separate from game state and rules.
- Start with an ASCII renderer, while preserving the ability to add tile or other
  renderers later.
- Keep a first-class headless CLI so AI agents can playtest the full game.
- Use one session/action backend for GUI, CLI, tests, and automated play.
- Preserve entity unification and body swap as core mechanics.
- Unify props, items, scenery, books, doors, bodies, and carried objects under one
  entity/component model.
- Support local and API-key LLM providers behind one provider interface.
- Keep background generation, but make it configurable and resource-aware.
- Build toward long runs where the eventual win condition is killing the emperor.
- Keep promises and prophecy foundational.

## Current Status

This repository now contains the first architecture stub for the Godot/C# project. The
stub defines the solution layout, shared core/session contracts, JSON-first CLI shell,
minimal Godot shell, mock/Ollama provider seams, initial operation registry, and an
imperial encounter scenario.

Recommended first read:

- [NORTHSTAR.md](NORTHSTAR.md)
- [AGENTS.md](AGENTS.md)
- [docs/DOCUMENTATION_MAP.md](docs/DOCUMENTATION_MAP.md)
- [docs/GAME_VISION.md](docs/GAME_VISION.md)
- [docs/DEVELOPMENT_SETUP.md](docs/DEVELOPMENT_SETUP.md)
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md)
- [docs/CLI_AND_AGENT_PLAYTESTING.md](docs/CLI_AND_AGENT_PLAYTESTING.md)
- [docs/FIRST_PLAYABLE.md](docs/FIRST_PLAYABLE.md)
- [docs/DESIGN_DECISIONS.md](docs/DESIGN_DECISIONS.md)
- [docs/MIGRATION_NOTES.md](docs/MIGRATION_NOTES.md)
- [docs/OPEN_QUESTIONS.md](docs/OPEN_QUESTIONS.md)

## Core Architecture

Sorcerer should be built around plain C# engine classes rather than putting game rules
inside Godot scene nodes.

Expected high-level shape:

```text
Godot scene tree
  input, window lifecycle, renderer nodes, menus, developer tools

Sorcerer.Core
  deterministic simulation, entities, actions, turns, combat, items, props, magic

Sorcerer.Llm
  live/mock providers, prompt assembly, JSON parsing, audits, background jobs

Sorcerer.Cli
  JSON-first headless agent-playable interface over the same GameSession backend

Sorcerer.Tests
  unit and integration tests for contracts, rules, CLI, and resolver behavior
```

The GUI and CLI must both drive:

```csharp
ActionResult result = session.Execute(command);
GameView view = session.View();
```

Renderer code consumes `GameView`. It does not own rules.

## Agent Playability

The CLI is not a debug afterthought. It is how AI agents playtest the real game.
It can optimize for machine-readable JSON rather than human-readable terminal play.

The CLI should support:

- scripted commands
- structured JSON observations
- structured JSON action results after every command
- compact local map output
- perfect debug state when requested
- visible enemies, NPCs, floor items, props, and terrain
- inventory, equipment, reagents, curses, promises, journal, standing, and messages
- mock LLM mode for fast deterministic agent testing
- live provider mode for resolver evaluation

For details, see [docs/CLI_AND_AGENT_PLAYTESTING.md](docs/CLI_AND_AGENT_PLAYTESTING.md).

## Wild Magic

Wild magic is resolved as structured JSON. The engine validates the result before any
state mutation.

Technical failures, such as invalid JSON, unsupported operations, impossible references,
or provider timeouts, do not consume a turn. Intentional magical rejections do consume a
turn.

For the initial contract, see [docs/WILD_MAGIC_CONTRACT.md](docs/WILD_MAGIC_CONTRACT.md).
For the resolver architecture, see
[docs/MAGIC_RESOLVER_ARCHITECTURE.md](docs/MAGIC_RESOLVER_ARCHITECTURE.md).

## Entity Model

Everything interactable should be an entity with components:

- player-controlled bodies
- NPCs and enemies
- summoned creatures
- props and fixtures
- doors, shrines, books, signs, altars
- floor items and carried items
- corpses and husks
- persistent magical anchors

For the proposed model, see [docs/ENTITY_MODEL.md](docs/ENTITY_MODEL.md).

## LLM And Background Jobs

The architecture should isolate provider configuration so users can swap local models or
API-key providers without touching engine rules.

Foreground LLM calls block the player. Background jobs enrich the world but must be
throttled, cancellable where practical, and visible in developer tools.

For details, see [docs/LLM_AND_BACKGROUND_JOBS.md](docs/LLM_AND_BACKGROUND_JOBS.md).
For provider shape, see [docs/PROVIDERS_AND_CONFIGURATION.md](docs/PROVIDERS_AND_CONFIGURATION.md).

## Development Principle

When adding a feature, ask:

> Does this add a reusable mechanic that can support many strange prompts?

Avoid one-off spell handlers, renderer-only behavior, hidden fallback engines, and
state mutation outside the authoritative session/action path.
