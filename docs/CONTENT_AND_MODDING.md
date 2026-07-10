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

- `content/regions/*.json` contains tradition and region definitions. The core loader searches
  upward from the current/app base directory, then uses the same corpus embedded in
  `Sorcerer.Core` so standalone exports retain it, and finally falls back to the compile-safe
  minimal registry. Loose files are therefore the editable/moddable override; the embedded copy is
  the shipped baseline.
  Regions carry terrain/voice tags, voice summary, ambient lines, affordance cards, imperial
  presence, floor terrain, a deterministic map anchor with optional seed-derived jitter, and a
  `props` grammar. Prop grammars define density variance, weighted bases/materials/conditions,
  readable or anchor hooks, and relative-offset ensembles; the engine derives each zone's result
  from seed and content without a model call.
  Top-level `populations` rows attach to regions by `regionId`, regardless of file load order.
  They define center/near/wild density, settlement centers, name-part pools, and weighted resident
  archetypes. Archetypes may carry habitat weights, stat bands, role/tone tags, description and
  want templates, knowledge tiers, wares, and services. The resulting people are deterministic
  engine entities; population content never calls a model or mutates components outside the shared
  spawn/trade/service consequences.
  Top-level `settlements` rows attach to the same regions. They supply settlement and hamlet name
  pools, footprint size, road identity, at least three district profiles, and landmark tables.
  District rows own their summary, terrain, population multiplier, tags, and one signature-site
  fixture. `WorldPlaceGraph` combines this content with the seed; no coordinates or renderer nodes
  are hard-coded into the content.
  Adding a region changes shared engine generation, atlas/resolver context, dialogue scene voice,
  and both renderers without adding renderer code.
- `content/quests/*.json` contains provider-free journey templates. Patterns may use giver, role,
  settlement, district, landmark, exact destination-zone, direction, and evidence-token fields.
  Templates propose claim/promise metadata only; the engine binds them to real `WorldPlaceGraph`
  landmarks and applies them through ordinary claim, promise, spawn-item, and canon consequences.
- `content/lore/*.md` contains Markdown lore cards with a fenced `lore` metadata block.
  The core loader searches upward from the current/app base directory, then falls back to
  built-in seed cards if files are absent.
- Lore cards use `subjects` and `triggers` as router keys. Sections are headed
  `## Level N`; only sections at or below the current access level enter routed context.
  Cards or sections marked draft are excluded from the live catalog.
- Generated zone fixtures use deterministic texture grammar in code for now, drawing their
  subjects and non-imperial fallback texture from loaded region data. Their names, descriptions,
  and subject tags are engine-visible so later lore routing and background detail can reuse them.
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
