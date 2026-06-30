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
- On success, the player soul moves into the new body, the displaced soul moves into the
  old body, `ControlledEntityId` follows the new body, factions/controllers are swapped,
  and inventory remains on whichever body carried it.
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
