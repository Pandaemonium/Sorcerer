# Entity Model

Sorcerer should use one unified entity model for everything interactable.

This is not about adopting a heavy ECS framework. It is about avoiding separate, drifting
systems for enemies, NPCs, props, items, books, doors, corpses, and player bodies.

If magic can meaningfully target something, it should usually have entity identity. Bare
terrain can remain efficient grid data internally, but addressable wall sections, pools,
shrines, doors, fixtures, books, corpses, and similar things should be entities.

## Core Idea

An entity is a stable id plus a set of components.

Examples:

- player body: position, renderable, physical, actor, inventory, controller, profile
- goblin: position, renderable, physical, actor, AI, faction, profile
- brass moth familiar: position, renderable, physical, actor, AI, summoned, faction
- book: position, renderable, physical, item or prop, readable, inspectable
- shrine: position, renderable, physical, prop, interactable, magic anchor
- sword: item, equipment, renderable, maybe position when on floor
- door: position, renderable, physical, interactable, door
- husk: position, renderable, physical, actor, no controller, status unconscious

## Identity

Every entity should have:

- id
- display name
- tags
- optional description
- optional stable soul/person id for characters

The entity id is for mechanics. The display name is for players and prompts. The soul id
is for reputation, memories, and body swap consequences.

## Components

Suggested components:

### Position

- map id or zone id
- x/y tile
- occupancy layer

### Renderable

- glyph
- color or palette id
- render layer
- optional sprite/tile id for future renderers

### Physical

- blocks movement
- blocks sight
- material
- size
- durability or HP for destructible objects

### Actor

- HP/max HP
- mana/max mana
- attack/defense
- statuses
- faction
- AI profile

### Controller

- controlled by player
- controlled by AI
- inert
- remote/summoned/follower behavior

Only one entity is the current player-controlled body at a time, but the model should not
assume only one entity could ever be controllable.

### Inventory

- carried entity ids or stack records
- protected/reagent-safe markers
- equipment slots
- spell focus markers

Inventory belongs to the body that carries it.

### Item

- value
- material
- tags
- stack policy
- use profile
- equipment slot, if equippable
- reagent properties

### Prop

- fixture or scenery marker
- interactable flags
- can be transformed
- can be animated
- can be used as a spell anchor

A prop can also be an item if it can be picked up or carried.

### Readable

- title
- author
- text/canon id
- generation state
- lore topics

### Claim Source

For readable records, signs, fixtures, props, books, or other authored objects that should
surface future hooks:

- one or more claim seeds
- category, subject, salience, confidence, and tags
- whether the claim is visible to the player
- whether the claim should bind as a promise
- optional promise kind, realization kind, trigger hint, and claimed place

Reading or examining a claim source should not mutate ledgers directly. It should submit the
same `record_claim`, `record_rumor`, `create_promise`, and `update_claim` consequences used by
dialogue claim extraction. This lets documents and props participate in the rumor/promise
flywheel without becoming bespoke opening-script code.

### Profile

For actors and bodies:

- name others use
- appearance
- body-facing backstory or public history
- body stats such as Vigor
- origin

Soul-facing character state should be separate enough to travel with the soul:

- magical signature
- personal backstory
- soul stats such as Attunement and Composure
- current and max mana
- personal and exploration memory
- reputation, legend, and explored-memory ownership

On body swap, the controlled body supplies public name, appearance, inventory, equipment,
physical condition, HP, and Vigor. The player soul keeps Attunement, Composure, mana, magical
signature, reputation, legend, and exploration memory.

### Memory

For NPCs or sufficiently person-like entities:

- remembered claims
- source/provenance
- privacy/shareability
- salience

Memory edits should be bounded operations, not free-form direct mutation by the model.

### Want

For notable NPCs:

- one active desire
- salience
- stakes
- tags
- status

Wants give dialogue and bond logic direction without becoming quests by themselves. Opening NPCs
can use authored wants; generated NPCs should receive deterministic wants when instantiated. Typed
`spawn_entity` supports explicit want fields and otherwise synthesizes a conservative default want
for notable NPCs: social tags/roles, talk/give/recruit verbs, promise hooks, or memory-bearing
spawns opt into that default. A non-summoned entity with no social signal should remain blank unless
content supplies an explicit want. The same spawn path also supports explicit entity ids and richer
profile metadata for rare canonical actors, so a named figure can stay addressable without leaving
the shared spawn lifecycle. Pass `autoWant: false` only when an otherwise notable actor
should intentionally remain blank. A want is not player-facing truth until an NPC says something
that becomes a claim, promise, memory, or other typed consequence. If a want changes after
instantiation, the change should apply through the shared `update_want` consequence so dialogue,
magic, services, and world-turns get the same validation and audit deltas. `update_want` can
optionally write a hidden `record_memory` child delta, letting dialogue, services, or rescue
actions preserve why motivation changed without exposing the want as journal truth; that parent
want update rolls back if the requested child memory rejects. Its deltas
include prior and new want fields so transcripts can explain the motivational change without
diffing entity state. Ordinary engine actions can also satisfy or redirect wants through that path:
freeing Lio, for example, marks his escape want satisfied while leaving later Hollowmere
trust, danger, or lead disclosure to dialogue, claims, bonds, and promises. Service offers may also
carry explicit completion metadata such as `wantStatusOnComplete` or completion tags; after a
successful `request`, those metadata fields submit `update_want` rather than mutating the provider
directly.

### Promise Anchor

For sites, objects, or characters bound to a promise:

- promise id
- role in promise
- realization state

## Body Swap

Body swap should follow directly from entity unification. It is architecturally central
but should be rare and severe in play.

Rules:

- `GameState.ControlledEntityId` points at the body the player currently controls.
- The controlled body supplies HP, Vigor, public profile, inventory, equipment, and focus.
- The occupying soul supplies Attunement, Composure, mana, magical signature, personal memory,
  reputation, legend, and explored map memory.
- Inventory stays with the body.
- The player soul id remains stable for deeds, promises, reputation, and consequences.
- The vacated body becomes an entity with no controller.
- The renderer, camera, current FOV, resolver context, and CLI all follow
  `ControlledEntityId`; explored map memory follows the soul.
- Some spells may be soul swaps rather than possession, meaning another soul can occupy
  the player's former body.

Body swap should not copy all fields from one player object to another. It should move the
control pointer and adjust components.

Current implementation:

- `possess [target]` exists as a shared `GameSession` command, and `possess` also exists
  as a wild-magic operation.
- The target must be a nearby living actor.
- Alert hostile bodies resist possession, consume the turn, and do not move the control
  pointer.
- Incapacitated bodies can be possessed.
- On success, the player soul moves into the new body and the displaced soul moves into
  the old body through `swap_souls`; `ControlledEntityId` follows the new body through
  `set_controlled_entity`; factions/controllers are swapped; and inventory remains on
  whichever body carried it.
- The successful possession packet is transactional: soul swap, controllers, factions,
  statuses, controlled entity, target clear, deed, and narration commit together or roll
  back with a hidden `possessionSkipped` audit.
- `BodyStatsComponent` stays with the body, while `SoulLedger` records stay keyed to the
  moving soul. `ActorComponent` is synced from both: HP/attack/defense from body Vigor,
  mana from soul Attunement/current mana.
- The old body receives a short `disoriented` status so it does not immediately act during
  the same turn.

This is a narrow engine seam, not the final body-swap design. It proves the actor-agnostic
control path and gives later magic operations a concrete behavior to call.

## Items And Props

Items and props differ by components, not by separate identity systems.

Examples:

- A loose pearl is an entity with `Item` and maybe `Position`.
- A statue is an entity with `Prop`, `Physical`, and `Position`.
- A small idol can have both `Prop` and `Item`; it is scenery until picked up.
- A book can be `Readable`, `Item`, and `Prop` depending on whether it is fixed or loose.
- A corpse can be `Physical`, `Item` for lootable remains, and `ActorRemains`.

This unification is important for wild magic. The resolver should be able to target "the
mirror", "the sword", "the book", "the corpse", and "the nearest enemy" through the same
reference system.

## Stacking Policy

Do not make all inventory stack by name.

Recommended policy:

- commodities stack: gold, salt, chalk, sand, arrows
- unique or modified entities do not stack
- generated curios do not stack unless explicitly commodity-like
- books do not stack
- equipped or focused items do not stack
- items with memories, curses, promises, charges, or generated abilities do not stack

Stack records can still be entities or inventory entries, but the policy must be explicit.

## State Views

Renderer and LLM context should not inspect raw components freely. Build read-only cards:

- `EntityCard`
- `ItemCard`
- `PropCard`
- `ActorCard`
- `InventoryCard`
- `SpellAnchorCard`

These cards decide what is visible and relevant.
