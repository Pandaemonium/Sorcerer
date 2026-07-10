# Documentation Map

This repo is intentionally documentation-heavy before implementation begins. Sorcerer is a
clean-slate project, and the documents here are meant to keep architecture, design, and
agent work aligned once code starts moving.

## Start Here

- [GRAND_VISION.md](GRAND_VISION.md): the central guiding vision - what the game plays like,
  the systems, and how they interact.
- [../NORTHSTAR.md](../NORTHSTAR.md): the core vision and non-negotiable contracts.
- [../README.md](../README.md): project overview and current status.
- [../AGENTS.md](../AGENTS.md): instructions for coding agents working in this repo.
- [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md): durable decisions already made.
- [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md): questions still worth answering later.

## Tone & Canon (inherited from Wild Magic)

The soul of the game, carried over verbatim from the prototype. Engine-agnostic; ignore any
references inside them to Python modules or the old name.

- [INHERITED_FROM_WILD_MAGIC.md](INHERITED_FROM_WILD_MAGIC.md): what these are and how to
  read them.
- [AESTHETICS_AND_TONE.md](AESTHETICS_AND_TONE.md): the voice/look/feel bible (COLOR vs
  MARBLE).
- [WORLDBUILDING.md](WORLDBUILDING.md): the canon - Vigovia, the realms, the traditions.
- [SPELL_COMPENDIUM.md](SPELL_COMPENDIUM.md): example spells (tone bar + eval corpus) and the
  taxonomy of harder spells.

## Game Design

- [GRAND_VISION.md](GRAND_VISION.md): the central vision (supersedes GAME_VISION) - play, the
  systems, and how they interact.
- [HOLISTIC_DEVELOPMENT_PLAN.md](HOLISTIC_DEVELOPMENT_PLAN.md): current product-level review and
  prioritized plan for turning the existing engine lanes into freedom, expression, and a visibly
  responsive world.
- [SYSTEMS_VISION.md](SYSTEMS_VISION.md): the systems-level unification - the
  act/witness/record/circulate/respond/deliver loop, the one-consequence-grammar discipline, and
  the four convergence moves (consequence grammar, rumor transport, wants, world-turn).
- [WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md): the Empire, factions,
  consequences, and player-driven emergence.
- [EMERGENT_WORLD.md](EMERGENT_WORLD.md): how the world responds to the player - deeds,
  legend, multidimensional reputation, the interpret/narrate (never simulate) model, and
  event-driven world reaction.
- [IDENTITY_AND_ATTRIBUTION.md](IDENTITY_AND_ATTRIBUTION.md): soul-bound legend vs
  identity-bound warrants - disguise, body swap, forked identities, and evidence-driven merges.
- [PROMISES_AND_PROPHECY.md](PROMISES_AND_PROPHECY.md): the promise ledger, always-honor, and
  prophecy as foundational systems.
- [PROMISE_PAYOFF_PLAN.md](PROMISE_PAYOFF_PLAN.md): how bound promises become concrete sites,
  people, items, stock, services, threats, routes, memories, and other world affordances.
- [DIALOGUE_SYSTEM.md](DIALOGUE_SYSTEM.md): LLM-generated dialogue, claim/promise extraction,
  NPC memories, bond changes, and engine validation.
- [DIALOGUE_CONTEXT_ROUTING.md](DIALOGUE_CONTEXT_ROUTING.md): active dialogue context routing
  architecture: NPC-knowledge-gated cards, the short context-router call, parser-router
  capability cards, route metrics, and replay.
- [OPENING_SEQUENCE.md](OPENING_SEQUENCE.md): the first 20 minutes as a promise-rich,
  system-driven vertical slice rather than a scripted tutorial.
- [SEMANTIC_EFFECTS.md](SEMANTIC_EFFECTS.md): entity descriptions/traits as dormant mechanics
  the resolver surfaces (semantic by default, mechanical on demand).
- [CHARTER_MAGIC.md](CHARTER_MAGIC.md): the deterministic licensed counterpoint - instant
  authored spell bundles, charter infrastructure as entities, and witness-classification splits.
- [SPELL_ECHOES.md](SPELL_ECHOES.md): proposed experiment - re-casting recorded resolutions as a
  within-run repertoire, with drift and fatigue guardrails.
- [COSTS_REAGENTS_AND_TREASURED_ITEMS.md](COSTS_REAGENTS_AND_TREASURED_ITEMS.md): post-cast
  costs, the cost palette and overreach ladder, protected inventory, and item fuel.
- [CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md): the future casting-performance
  minigame contract.
- [CHARACTER_AND_STATS.md](CHARACTER_AND_STATS.md): the Vigor/Attunement/Composure triad,
  origins, and how stats modulate the resolver.
- [DEATH_AND_CHRONICLE.md](DEATH_AND_CHRONICLE.md): killer-split death screens, the run-closing
  chronicle assembled from ledgers, and cross-run traces that commemorate without empowering.

## Architecture

- [ARCHITECTURE.md](ARCHITECTURE.md): the overall C#/Godot architecture.
- [ARCHITECTURE_STUB_MAP.md](ARCHITECTURE_STUB_MAP.md): the compile-safe system lanes
  created before deep implementation begins.
- [CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md): the buildable RFC for the cast pipeline,
  operations, targeting, transactions, RNG, and the pending-cast model.
- [ENTITY_MODEL.md](ENTITY_MODEL.md): unified entities, components, body swap, items, and
  props.
- [WILD_MAGIC_CONTRACT.md](WILD_MAGIC_CONTRACT.md): the initial wild-magic JSON contract.
- [MAGIC_RESOLVER_ARCHITECTURE.md](MAGIC_RESOLVER_ARCHITECTURE.md): capability cards,
  operation registry, context routing, and audits.
- [CAPABILITY_ROUTING.md](CAPABILITY_ROUTING.md): plan for routing the operation cards and
  game state (and an opt-in generative router call) to cut resolver context/latency.
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
- [MATURITY_ROADMAP.md](MATURITY_ROADMAP.md): the phased path from the first-playable slice to
  full Wild Magic-level maturity, building general systems over the reserved state lanes.
- [PROCGEN_RICHNESS_PLAN.md](PROCGEN_RICHNESS_PLAN.md): tactical props/people/places/quests plan
  for the large, content-driven procedural world.
- [SIGNIFICANT_INTERIORS.md](SIGNIFICANT_INTERIORS.md): data-authored one-zone interiors,
  threshold access, follower transitions, persistence, and GUI/CLI parity.
- [BUILD_PLAN.md](BUILD_PLAN.md): the executable, file-level handoff plan - engine refactor,
  perception/FOV, Phases 1-8, including character/body-soul state, deeds/factions/social/worldgen,
  magic/item depth, lore/narrator/provider harness work, and the current persistence/replay/
  chronicle/win-condition slice, with Wild Magic keep/change/drop guidance.
- [FIRST_PLAYABLE.md](FIRST_PLAYABLE.md): the first vertical slice worth building.
- [CLI_AND_AGENT_PLAYTESTING.md](CLI_AND_AGENT_PLAYTESTING.md): JSON-first CLI and agent
  play.
- [TESTING_AND_EVALS.md](TESTING_AND_EVALS.md): tests, spell evals, audits, and unattended
  playtesting.
- [MIGRATION_NOTES.md](MIGRATION_NOTES.md): lessons to preserve or leave behind from Wild
  Magic.
- [WILDMAGIC_IMPORT_PLAN.md](WILDMAGIC_IMPORT_PLAN.md): detailed contract-by-contract plan for
  importing WildMagic mechanics, content, tests, and tooling into Sorcerer's C# architecture.

## Documentation Rule

When code changes a durable architectural contract, update the relevant document in the
same change. Do not leave core design decisions only in chat history or temporary notes.
