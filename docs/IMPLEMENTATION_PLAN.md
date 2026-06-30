# Implementation Plan

This plan is intentionally clean-slate. Sorcerer should not mechanically port Wild Magic.
It should rebuild the strongest ideas with a cleaner Godot/C# architecture.

Each milestone should leave the game more playable by humans and agents.

For the first vertical slice, see [FIRST_PLAYABLE.md](FIRST_PLAYABLE.md).

## Milestone 0: Project Skeleton And Contracts

Goal: create the Godot project and core C# architecture before gameplay grows.

Work:

- create Godot 4 C# project
- establish namespaces and folders
- create plain C# core assembly structure
- add `GameSession`, `GameEngine`, `GameState`, `GameCommand`, `ActionResult`
- add test project
- add separate JSON-first CLI executable referencing the core
- add docs links in README

Acceptance:

- project opens in Godot
- tests run
- CLI can start and print an empty/new game view
- GUI and CLI both construct `GameSession`

## Milestone 1: Minimal Playable Core

Goal: play a tiny deterministic roguelike without LLMs.

Work:

- grid map
- unified entities
- controlled entity id
- movement
- collision
- doors
- wait
- basic combat
- HP and mana
- message log
- state validation
- seeded test chamber
- read-only `GameView`
- CLI commands: inspect, map, move, wait, attack by bump

Acceptance:

- an AI agent can play a short scripted run through CLI
- GUI ASCII renderer shows the same state
- turn consumption is test-covered
- blocking entities cannot overlap

## Milestone 2: Renderer Boundary

Goal: make ASCII rendering real without coupling it to rules.

Work:

- Godot ASCII map renderer
- HUD panel using `GameView`
- input maps to `GameCommand`
- target/selection display
- message log display
- developer key to dump current view

Acceptance:

- renderer imports no rule mutation APIs
- game remains fully playable through CLI
- adding a second renderer would not require engine changes

## Milestone 3: Entity And Interactable Unification

Goal: represent bodies, props, and items through one entity model.

Work:

- component model
- item/carryable component
- prop/fixture component
- readable component
- interactable component
- pickup/drop
- inspect/examine entity
- floor item and prop cards
- inventory view

Acceptance:

- a book, a shrine, a dropped item, and an enemy are all targetable entities
- carried items preserve identity or stack by explicit policy
- CLI and GUI show the same inventory/interactable facts

## Milestone 4: Wild Magic MVP

Goal: type a spell, get structured JSON from a live provider, validate it, and apply it.

Work:

- live provider interface
- at least one working live provider
- mock provider
- capability-card resolver architecture
- operation registry
- spell context view
- initial JSON contract
- parser and normalizer
- validation
- transactional effect application
- audit log
- minimal promise ledger
- create-promise effect
- effects: damage, heal, status, terrain, push/pull, summon, message, mana/health costs
- technical failure turn contract
- intentional rejection turn contract

Acceptance:

- mock spells can be tested quickly
- live provider spells write audit records
- malformed JSON does not consume a turn
- rejected overpowered spell consumes a turn
- failed application rolls back partial state
- a spell can create a simple promise record

## Milestone 5: Items, Props, Reagents, And Anchors

Goal: make the world materially available to spells.

Work:

- one item/prop value model
- reagent cards
- protected/treasured inventory
- item costs
- spell anchors from visible props/items
- transform item/prop operation
- animate prop operation
- generated simple curios

Acceptance:

- spells can target scenery through general mechanics
- item costs cannot spend unavailable or protected items
- expensive items are visible as meaningful spell fuel
- protected items cannot be silently consumed
- props do not require special spell systems

## Milestone 6: Promise Realization And Prophecy

Goal: make promises matter beyond existing as records.

Work:

- promise types: rumor, prophecy, quest, threat, debt, omen
- spatial hints
- binding/reservation rules
- simple realization during generation
- mysterious player journal view
- structured agent/debug promise view
- CLI/GUI journal access

Acceptance:

- a spell can create a future commitment
- the engine decides whether and where it binds
- future generation can realize at least one promise type
- promises are visible to agents and debuggable

## Milestone 7: Body Swap And Actor-Agnostic Actions

Goal: make possession and entity control elegant.

Work:

- actor-agnostic action methods
- controlled entity id in state
- per-body inventory and stats
- body profile
- possess command/effect
- husk/vacated-body behavior
- camera/FOV follow controlled body
- magic context follows controlled body

Acceptance:

- body swap works through CLI and GUI
- inventory stays with body
- controlled body can move, cast, use items, and inspect
- no player-only duplicate path is introduced

## Milestone 8: Background Generation

Goal: enrich the world without stealing the machine.

Work:

- background job queue
- foreground/background provider config
- max concurrent jobs
- disable toggles
- audit records
- developer queue view
- background book/room/prop detail MVP

Acceptance:

- foreground spells remain responsive within configured limits
- background jobs can be disabled
- queue state is visible in developer tools and CLI
- durable output enters state at explicit apply points

## Milestone 9: Broader Simulation

Goal: add world reaction systems only after the core play loop is stable.

Candidates:

- factions and standing
- NPC memory
- bonds/followers
- world deeds
- explicit trade and barter
- richer quests
- region voice and generation

Rule:

Add these as clean lanes that serve the main loop. Do not recreate tangled experimental
systems before the foundation is fun and inspectable.

## Always-On Acceptance Tests

Every milestone should preserve:

- CLI can start a run
- GUI and CLI use the same `GameSession`
- `inspect` or JSON view exposes enough state for agents
- technical LLM failures do not consume turns
- intentional rejections do consume turns
- renderer code does not mutate rules
- state validation catches invalid positions and overlaps
- new player-facing features have CLI access
