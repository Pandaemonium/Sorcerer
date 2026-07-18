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

This repository now contains the first playable Godot/C# slice. The shared backend drives
both the JSON-first CLI and a Godot ASCII GUI with map interaction, spell entry, command
entry, provider/model controls, pending casts, inventory/status/promise panels, and an imperial
encounter scenario. Pending casts can be answered through a four-game casting repertoire; the
Bone-Song rhythm entry presents one carved drum with center, edge, and rim strike marks, distinct
swooping note approaches, and shockwave-versus-dud feedback. The current chamber also includes
shared-engine item verbs,
equipment/focus handling, readable and examinable fixtures, a locked cell, a prisoner
rescue consequence, origins and soul/body stats, deeds/legend/faction pressure, first-pass
social bonds, and a visible promise that can be realized in play. The first procedural-world
slice is also live: travel snapshots zones, lazily generates region-flavored places, exposes
an atlas/world card, feeds region affordances into the magic resolver, and can turn bound site
promises into generated places. Regions and traditions now load from `content/regions`: fourteen
canon-rooted regions cover every rolled realm, deterministic seed-jittered placement makes them
reachable on the zone grid, and crossing a realm border names both sides through the shared action
result. A minimal seeded world roll varies realm status, ruler, and effective imperial grip, and
keeps the canon rule that exactly one of the four old kingdoms is the free rival. Generated zones
now compose region-authored semantic props at uneven densities: ordinary scenery, readable hooks,
magic anchors, and coherent small ensembles all remain targetable spell material. Resolver and
dialogue context protect actors and hooks in a full-detail lane while carrying ordinary scenery in
a compact lane, so lushness cannot hide combatants from the model. Generated zones also include a
deterministic regional population field: named archetypes cluster around settlement centers, thin
out along their shoulders, and leave common empty wilderness stretches with rare outliers. Every
generated resident has a culturally specific want and dialogue knowledge; selected archetypes also
carry regional wares or services through the shared consequence grammar. A deterministic shared
place graph now turns every regional anchor into a named 3×3 settlement with distinct districts,
adds smaller hamlets and landmarks, and connects all settlements with physical road-zone paths.
District identity changes terrain, population, signature sites, travel prose, atlas/world output,
magic context, and dialogue context. Settlement lead residents also seed provider-free generated
journeys: talk to hear a concrete lead, receive a direct journal objective pointing at a real
landmark, and reach that exact place to realize named evidence and canon through the existing
promise system. The opening now uses the same general machinery for forward momentum: freeing any
qualifying captive generates a seed-varying spoken lead from real geography, materializes a named
regional contact at the promised settlement, and completes the objective only after the player
speaks with that contact. The contact can then produce a fetch, delivery, escort, threat, folk
service, rumor-verification, or social-leverage handoff. Completion reads ordinary world state,
returns to the giver for want/memory/standing payoff, and supports multiple threat/social outcomes.
GUI new runs now roll and display a fresh reproducible seed by default. That seed changes the
opening alarm, two inspectable confiscated irregularities with actionable hooks, the nearby refuge
chosen for the sweep warning, and the subsequent journey route; seed 7 remains pinned for focused
characterization tests.
The HUD, journal, inspect output, and CLI JSON share one prioritized next-step view. Rumors use
physical roads between regions, and selected motivated NPCs can spend bounded initiative
approaching for a visible reason. Lio is the first proving ground, not a scripted quest giver, and
the chain requires no provider call. Early
significant interiors are live as persistent linked zones: every regional center has one culturally
specific site, while the capital has separate palace and archive spaces. Shared `enter`/`leave`
commands carry followers, consume ordinary turns, expose the same context actions in Godot and the
CLI, and preserve interior people, fixtures, terrain, exploration, and magical changes across
return trips and save/load. Restricted thresholds accept ordinary keys plus permission or
open/forced state that validated dialogue and magic can create; they are never invisible plot
locks. See [docs/SIGNIFICANT_INTERIORS.md](docs/SIGNIFICANT_INTERIORS.md). The early status-trait
depth is live too: concealment affects hostile notice, and
burning, poisoning, and mending tick through the shared turn pump. Minimal trigger depth is live as
well: delayed effects and aura pulses are stored in a `TriggerLedger` and fire through the same
engine turn pump. Terrain has begun reacting too: water can extinguish burning into mist, fire can
ignite actors, and vines can snare bodies. Item depth has a first general slice: unprotected
reagents enter resolver context with spell-bias hints, and generated zones create deterministic
curios whose metadata survives pickup. Curses now have a minimal mechanical layer: close, far,
narrow, straight-path, and anchored templates can constrain later spell resolutions. Generated
residents can trade through explicit engine-side `wares`, `buy`, and `sell` commands, and NPCs can
offer revealed services through `services` and `request` without completing bargains inside
dialogue. The first
lush-content slice is live: Markdown lore cards load from `content/lore`, `LoreRouter` injects
relevant access-gated canon into magic context and atlas output, generated zone fixtures use
deterministic texture naming with durable subject tags, and background detail jobs write canon that
appears on later examine. Zone entry can now echo your current legend through deterministic rumor
lines, making reputation visible without adding hidden simulation. CLI provider creation now uses
purpose-based LLM settings for foreground wild magic/dialogue, with mock, Ollama, native Anthropic
Messages, native Gemini Interactions, and OpenAI-compatible adapters. Claude Sonnet 5 and Gemini
3.5 Flash support configurable reasoning effort plus per-call input/output/cache/thinking-token
and latency telemetry. Background jobs can be disabled or throttled for
resource-sensitive playtests. The long-run spine has its first durable slice:
schema-v1 save/load, transcript replay with materialized spell JSON, a reachable Vigovian Capital
with Emperor Odran as a normal killable actor, victory/defeat run completion, run-closing
chronicles, and inert cross-run memorial records. The CLI includes a spell eval harness, transcript
replay, and an unattended episode runner with JSONL logs plus invariant checks for agent
playtesting.

Wild-magic resolution now uses a latency-first routed request: obvious spells skip the semantic
router, selected cards expose only their operations and required state slices, and live providers
receive compact target/resource JSON rather than the rich engine inspection view. Engine
validation and transactional application still use the full authoritative state and registry.

Recommended first read:

- [NORTHSTAR.md](NORTHSTAR.md)
- [AGENTS.md](AGENTS.md)
- [docs/DOCUMENTATION_MAP.md](docs/DOCUMENTATION_MAP.md)
- [docs/GRAND_VISION.md](docs/GRAND_VISION.md)
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

The GUI and CLI both drive:

```csharp
ActionResult result = await session.ExecuteAsync(command);
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
- inventory, equipment, reagents, curses, promises, journal, rumors, standing, and messages
- current zone/region/place (including interiors), atlas/world affordances, travel, enter, and
  leave commands
- save/load, transcript replay, run status, and optional debug-perfect state
- mock LLM mode for fast deterministic CLI/agent testing
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
