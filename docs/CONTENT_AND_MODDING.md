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

Current live content:

- `content/lore/*.md` contains Markdown lore cards with a fenced `lore` metadata block.
  The core loader searches upward from the current/app base directory, then falls back to
  built-in seed cards if files are absent.
- Lore cards use `subjects` and `triggers` as router keys. Sections are headed
  `## Level N`; only sections at or below the current access level enter routed context.
  Cards or sections marked draft are excluded from the live catalog.
- Generated zone fixtures use deterministic texture grammar in code for now. Their names,
  descriptions, and subject tags are engine-visible so later lore routing and background
  detail can reuse them.
- Cross-run memorial records are JSONL run chronicles, currently under `runs/memorials.jsonl`.
  They are commemorative content only: a later run may surface one as an inert readable memorial
  prop, but it does not grant inherited power, inventory, stats, or faction standing.

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
