# Content And Modding

Sorcerer should leave room for modding and content iteration.

Do not overbuild a mod loader before the game exists, but avoid choices that make future
content editing painful.

## Data Bias

Prefer data that the core can load without a renderer:

- JSON for structured content
- Markdown for prose/lore with metadata
- C# records for code-owned defaults and tests
- Godot resources for renderer assets and editor conveniences

Godot resources can become useful later, but authoritative mechanics should remain easy to
test from the core and CLI.

## Content Categories

Likely data sets:

- regions
- traditions
- tiles and terrain
- entities and templates
- item definitions
- prop/fixture definitions
- spells and charter-style reliable magic
- capability cards or prompt snippets
- factions
- origins
- NPC roles
- promise/site blueprints
- lore cards
- dialogue style guides

## Modding Goal

Future modding should allow:

- adding regions
- adding items and props
- adding enemy templates
- adding traditions
- adding lore cards
- adding charter-style spells
- adding prompt flavor packs
- adding scenarios/test chambers

Adding new engine operations is a code change. Mods can use existing operations more
freely than they can define new rule primitives.

## Schema Versions

Content files should eventually carry schema versions.

Early builds can keep this light, but avoid unversioned formats becoming permanent by
accident.

## Prompt Content

Prompt snippets are powerful and dangerous.

Any moddable prompt content should be treated as flavor and guidance. It should not bypass
engine validation, protected inventory rules, or transaction safety.

## Generated Content

Generated content that becomes durable should be stored as state with provenance:

- source provider
- prompt purpose
- generated text
- parsed structured fields, if any
- attached entity/place/promise id
- turn created

This keeps saves, audits, and future debugging possible.

