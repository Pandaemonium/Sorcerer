# Documentation Map

This repo is intentionally documentation-heavy before implementation begins. Sorcerer is a
clean-slate project, and the documents here are meant to keep architecture, design, and
agent work aligned once code starts moving.

## Start Here

- [../NORTHSTAR.md](../NORTHSTAR.md): the core vision and non-negotiable contracts.
- [../README.md](../README.md): project overview and current status.
- [../AGENTS.md](../AGENTS.md): instructions for coding agents working in this repo.
- [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md): durable decisions already made.
- [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md): questions still worth answering later.

## Game Design

- [GAME_VISION.md](GAME_VISION.md): the player promise, feel, run structure, and win arc.
- [WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md): the Empire, factions,
  consequences, and player-driven emergence.
- [PROMISES_AND_PROPHECY.md](PROMISES_AND_PROPHECY.md): the promise ledger and prophecy
  as foundational systems.
- [COSTS_REAGENTS_AND_TREASURED_ITEMS.md](COSTS_REAGENTS_AND_TREASURED_ITEMS.md): post-cast
  costs, protected inventory, and item fuel.
- [CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md): the future casting-performance
  minigame contract.

## Architecture

- [ARCHITECTURE.md](ARCHITECTURE.md): the overall C#/Godot architecture.
- [ARCHITECTURE_STUB_MAP.md](ARCHITECTURE_STUB_MAP.md): the compile-safe system lanes
  created before deep implementation begins.
- [ENTITY_MODEL.md](ENTITY_MODEL.md): unified entities, components, body swap, items, and
  props.
- [WILD_MAGIC_CONTRACT.md](WILD_MAGIC_CONTRACT.md): the initial wild-magic JSON contract.
- [MAGIC_RESOLVER_ARCHITECTURE.md](MAGIC_RESOLVER_ARCHITECTURE.md): capability cards,
  operation registry, context routing, and audits.
- [RENDERER_BOUNDARY.md](RENDERER_BOUNDARY.md): how renderers consume state without owning
  rules.
- [LLM_AND_BACKGROUND_JOBS.md](LLM_AND_BACKGROUND_JOBS.md): foreground/background model
  calls and queues.
- [PROVIDERS_AND_CONFIGURATION.md](PROVIDERS_AND_CONFIGURATION.md): provider abstraction,
  local models, and API-key providers.
- [CONTENT_AND_MODDING.md](CONTENT_AND_MODDING.md): content data and future modding shape.

## Development

- [DEVELOPMENT_SETUP.md](DEVELOPMENT_SETUP.md): tool requirements and first build commands.
- [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md): staged build plan.
- [FIRST_PLAYABLE.md](FIRST_PLAYABLE.md): the first vertical slice worth building.
- [CLI_AND_AGENT_PLAYTESTING.md](CLI_AND_AGENT_PLAYTESTING.md): JSON-first CLI and agent
  play.
- [TESTING_AND_EVALS.md](TESTING_AND_EVALS.md): tests, spell evals, audits, and unattended
  playtesting.
- [MIGRATION_NOTES.md](MIGRATION_NOTES.md): lessons to preserve or leave behind from Wild
  Magic.

## Documentation Rule

When code changes a durable architectural contract, update the relevant document in the
same change. Do not leave core design decisions only in chat history or temporary notes.
