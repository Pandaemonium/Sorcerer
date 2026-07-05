# WildMagic Import Plan

Status: planning document. This is the detailed follow-up to
[MIGRATION_NOTES.md](MIGRATION_NOTES.md), the "Inherited Wisdom From Wild Magic" section in
[BUILD_PLAN.md](BUILD_PLAN.md), and the Spark archaeology pass over `C:\Games\WildMagic`.

Sorcerer is a clean-slate successor, not a compatibility port. The goal is to import the
valuable contracts, content, tests, and hard-won failure knowledge from WildMagic while
preserving Sorcerer's C# engine, entity model, consequence grammar, renderer boundary, and
agent-playable CLI.

## Reading This Plan

Path convention: in every phase block the **Sources** list is WildMagic, with paths relative to
`C:\Games\WildMagic\`; the **Sorcerer targets** list is this repository, with paths relative to
the repo root. Both repos ship identically named docs (`WORLDBUILDING.md`,
`AESTHETICS_AND_TONE.md`, `CAPABILITY_ROUTING.md`, `SEMANTIC_EFFECTS.md`), so a bare `docs/NAME.md`
means the WildMagic copy when it appears under Sources and the Sorcerer copy when it appears under
Sorcerer targets.

Execution order: the numbered phases are thematic groupings, not a schedule. The authoritative
order of work is the Prioritized Backlog (P0 -> P1 -> P2 -> deferred) near the end of this
document; it deliberately interleaves early tasks from several phases. Read a phase for the
contract, tests, and do-not-port rules of one system; read the backlog for what to build next.

## Import Rule

Port by contract, not by module.

For every candidate, ask:

- What player capability or world behavior does this unlock?
- Which Sorcerer primitive owns it: entity/component, typed consequence, ledger, router card,
  provider purpose, view, or eval?
- What state persists, and how is it saved/replayed?
- What can the GUI, CLI, agents, and prompts inspect?
- What tests prove turn semantics, rollback, target binding, replay, and provider-failure
  behavior?

If a WildMagic system cannot be expressed through those seams, it is not ready to port.

## Source Map

High-value WildMagic sources (paths relative to `C:\Games\WildMagic\`):

- Magic contract and parser repair: `wildmagic/wild_magic.py`,
  `wildmagic/resolution_parsing.py`, `wildmagic/spell_contract.py`,
  `wildmagic/effect_registry.py`, `wildmagic/capabilities.py`.
- Semantic traits: `wildmagic/semantics.py`, `docs/SEMANTIC_EFFECTS.md`.
- Lore and knowledge tiers: `wildmagic/lore_cards.py`, `wildmagic/lore_router.py`,
  `wildmagic/file_lore_cards.py`, `content/lore/*.md`, `docs/LORE_CARDS.md`.
- Dialogue, memory, claims, services: `wildmagic/dialogue.py`, `wildmagic/prompts.py`,
  `wildmagic/lore.py`, `wildmagic/trade.py`, `wildmagic/bonds.py`.
- Promises, quests, deeds, factions: `wildmagic/promises.py`, `wildmagic/quests.py`,
  `wildmagic/deeds.py`, `wildmagic/factions.py`, `docs/WORLD_PROMISES.md`,
  `docs/EMERGENT_QUESTS.md`, `docs/FACTION_KILL_REPUTATION.md`.
- Regions and content: `wildmagic/regions.py`, `wildmagic/worldgen.py`,
  `wildmagic/populations.py`, `wildmagic/item_catalog.py`, `wildmagic/item_generation.py`,
  `wildmagic/props.py`, `docs/WORLDBUILDING.md`, `docs/AESTHETICS_AND_TONE.md`.
- Tooling and evals: `wildmagic/cli.py`, `wildmagic/replay.py`, `wildmagic/autoplay.py`,
  `wildmagic/speleval.py`, `wildmagic/dialogue_eval.py`, `wildmagic/lore_eval.py`,
  `docs/AGENT_PLAYTESTING.md`, `docs/MODEL_CONFIG.md`.
- Region and worldgen data not yet mapped above but relevant to Phases 4 and 6:
  `wildmagic/game_data.py` (region/place records such as Hollowmere, Glasswild, Saltmarket),
  `wildmagic/town_gen.py`, and `wildmagic/npc_quests.py`. Cite or explicitly exclude each of
  these before the matching phase begins.

Sorcerer targets:

- `src/Sorcerer.Core`: entities, components, engine systems, ledgers, consequence applier,
  persistence, action results, dialogue context assembly.
- `src/Sorcerer.Magic`: operation registry, operation cards, provider result parsing,
  validation, cost application, transactions.
- `src/Sorcerer.Llm`: providers, structured calls, routers, audit sinks, purpose settings.
- `src/Sorcerer.Cli`: JSON CLI, transcripts, replay, episode runner, eval harness.
- `content/lore`, future `content/operations`, future eval corpora and texture content.
- `tests/Sorcerer.Tests`: characterization, integration, parser-repair, replay, eval, and
  invariant tests.

## Current Sorcerer Baseline

Already present or started:

- Shared `GameSession`/`GameEngine` backend for GUI, CLI, tests, and agents.
- Typed `ActionResult` and structured state deltas.
- Wild-magic operation registry and transactional apply path.
- Shared `WorldConsequence` grammar and broad `WorldConsequenceApplier`.
- Claims, rumors, promises, memories, bonds, wants, services, faction pressure, world-turn
  moves, scheduled events, triggers, persistent effects, tile flows, body possession, and
  generated-zone payoffs already route through typed consequences in current slices.
- File-backed `LoreCatalog`/`LoreRouter` and dialogue knowledge tiers, including tier 0 and
  subject-specific access.
- Four-step generated dialogue model: context-router, generator, parser-router, parser.
- CLI transcript/episode/replay foundations.

This means most imports should be "enrich and harden existing lanes" rather than "add new
parallel systems."

## Phase 0 - Anti-Port Inventory And Gates

Goal: make every future import auditable before code changes.

Tasks:

- Create a small source index from `C:\Games\WildMagic` with candidate systems, source paths,
  Sorcerer target modules, and status.
- Reconcile the content import list against the actual WildMagic file inventory before importing:
  diff the Phase 10 lists against `C:\Games\WildMagic\content\lore\*.md`, `regions.py`,
  `game_data.py`, and `town_gen.py` so no card, region, or population is dropped by omission or
  miscategorized (a lore-card culture treated as a code-defined region, or vice versa).
- Add a checklist to PR descriptions or implementation notes:
  - no Python runtime dependency
  - no renderer-owned rules
  - no direct ledger mutation outside typed consequences
  - no fallback spell engine
  - no GUI-only or CLI-only feature
  - no unmaterialized live-provider output needed for replay
- For each import, decide whether it is:
  - contract/test import
  - content/data import
  - mechanic import
  - provider/tooling import
  - rejected/deferred

Acceptance:

- Every imported slice names its WildMagic source, Sorcerer owner, tests, and deletion/avoidance
  notes.
- `MIGRATION_NOTES.md` remains short; this file owns the detailed backlog.

## Phase 1 - Parser Repair And Magic Contract Hardening

WildMagic value: local models produce predictable malformed JSON and effect-shape mistakes.
That knowledge should become tests and normalizers, not prompt-specific game logic.

Sources:

- `wildmagic/resolution_parsing.py`
- `wildmagic/spell_contract.py`
- `wildmagic/effect_registry.py`
- `wildmagic/speleval.py`
- `wildmagic/speleval_corpus.py`

Sorcerer targets:

- `src/Sorcerer.Magic/WildMagicController.cs`
- `src/Sorcerer.Magic/Operations`
- `src/Sorcerer.Magic/OperationRegistry.cs`
- `docs/WILD_MAGIC_CONTRACT.md`
- `tests/Sorcerer.Tests` parser/normalization/eval tests

Tasks:

1. Mine `resolution_parsing.py` for distinct malformation classes:
   - bare effect object instead of envelope
   - `effect_type` vs `type`
   - single object vs array
   - status names emitted as effect types
   - natural-language trigger actions
   - aliases for terrain/status/cost/target fields
   - malformed nested trigger/event payloads
   - target strings that need engine binding, not trust
2. Convert each class into a Sorcerer test fixture with:
   - raw model payload
   - normalized shape
   - expected acceptance or rejection
   - turn-consumption contract
3. Keep repairs syntactic and schema-level. Do not infer a whole spell from prose when the
   provider failed to produce a usable operation.
4. Expand `OperationRegistry` metadata only when the operation is already implemented or ready to
   be implemented as a general primitive.
5. Add from-audit replay parsing to eval tooling so old live responses can be rechecked after
   parser changes.

Acceptance:

- New parser repair tests are green without expanding gameplay fallbacks.
- Unsupported but parseable operations reject as intentional magical failures and consume a turn.
- Provider/JSON technical failures still do not consume a turn.
- No repair path directly mutates state.

Do not port:

- WildMagic's regex fallback spell parser.
- Legacy compatibility aliases that make invalid target references silently meaningful.
- Full `SUPPORTED_EFFECTS` breadth before Sorcerer has the primitives.

## Phase 2 - Capability And Context Routing For Magic

WildMagic value: capability cards controlled prompt size and effect attention. Sorcerer already
has the architectural shape; this phase finishes the high-value parts.

Sources:

- `wildmagic/capabilities.py`
- `wildmagic/effect_registry.py`
- `wildmagic/state_view.py`
- `docs/CAPABILITY_ROUTING.md` in both repos

Sorcerer targets:

- `src/Sorcerer.Magic`
- `docs/CAPABILITY_ROUTING.md`
- `docs/MAGIC_RESOLVER_ARCHITECTURE.md`
- future `content/operations/*.md`

Tasks:

1. Split operation metadata into:
   - always-available core operations
   - routed operation cards
   - required state/context slices
   - aliases and schema fields
   - risk/cost hints
2. Add deterministic selection first:
   - spell text tokens
   - selected target type
   - visible local affordances
   - region affordance cards
   - recent failure/unsupported patterns
3. Add optional live router only after deterministic routing is measurable.
4. Record selected operation cards and payload byte counts in magic audit records.
5. Add table tests: spell text plus scene -> expected card families.

Acceptance:

- Resolver prompts are smaller for ordinary casts without losing core operations.
- Unknown/unrouted operation names still validate against the registry at apply time.
- Router failure falls back to deterministic cards and does not fail the cast by itself.

Do not port:

- WildMagic card wording verbatim.
- Card-owned behavior hidden outside `OperationRegistry` and typed consequences.

## Phase 3 - Lore Cards, Knowledge Tiers, And Canon Import

WildMagic value: tiered lore cards make canon available to the model at the right depth and
speaker. Sorcerer has the first implementation; this phase fills out the data and tightens
the rules.

Sources:

- `content/lore/*.md`
- `wildmagic/file_lore_cards.py`
- `wildmagic/lore_cards.py`
- `docs/LORE_CARDS.md`
- `docs/WORLDBUILDING.md`
- `docs/AESTHETICS_AND_TONE.md`

Sorcerer targets:

- `content/lore`
- `src/Sorcerer.Core/Lore`
- `src/Sorcerer.Core/Dialogue/DialogueKnowledgeProfile.cs`
- `docs/DIALOGUE_CONTEXT_ROUTING.md`
- `docs/WORLDBUILDING.md`

Tasks:

1. Convert WildMagic `content/lore/*.md` into Sorcerer's Markdown card format:
   - preserve subjects/tags
   - preserve `## Level N` sections
   - preserve triggers
   - drop old implementation metadata that only served Python
2. Normalize tier semantics:
   - 0 common knowledge
   - 1 basic familiarity
   - 2 deep familiarity
   - 3 specialist
   - 4 secret
3. Add subject aliases for cultures, regions, traditions, factions, services, and legal topics.
4. Seed generated NPC knowledge profiles from:
   - origin/region
   - faction/role
   - profession/service
   - tags
   - authored overrides
5. Keep static `world_knowledge` separate from dynamic claims, rumors, and promises.
6. Teach the dialogue parser to avoid extracting claims that merely restate supplied static lore.
7. Add tests for:
   - tier 0 common access
   - tier 4 secret gating
   - unrelated secret non-leakage
   - NPC origin/role profile seeding
   - replay preserving selected lore cards

Acceptance:

- A local NPC can answer culturally specific questions with tier-appropriate detail.
- An outsider sees common or no lore, not secret lore descriptions.
- Lore-fed generated speech does not pollute the claim/promise ledger with restated canon.

Do not port:

- False-common-lore correction mechanics until the belief/reliability layer exists.
- Any lore that conflicts with Sorcerer's canonical docs without an explicit canon decision.

## Phase 4 - Region Voice, Traditions, And Content Palettes

WildMagic value: region identity carried voice, ambient texture, enemy sets, props, and magic
idioms. Sorcerer should import this as data-driven texture and prompt guidance, not as hard-coded
generation.

Sources:

- `wildmagic/regions.py`
- `wildmagic/worldgen.py`
- `wildmagic/populations.py`
- `wildmagic/prompts.py`
- `docs/AESTHETICS_AND_TONE.md`
- `docs/WORLDBUILDING.md`

Sorcerer targets:

- `RegionDefinition`
- `RegionAffordanceCard`
- `TextureGrammar`
- magic context/resolver lens
- dialogue context cards
- `content/lore` and future content data

Tasks:

1. Define per-region data records:
   - voice summary
   - tradition idiom palette
   - imperial presence
   - common subjects/tags
   - ambient lines by context
   - fixture/curio/material palettes
   - likely NPC roles and concerns
2. Import/adapt priority regions:
   - Hollowmere/frontier
   - Vigovia/imperial heartland
   - Glasswild/deep wild
   - Saltmarket
   - Stalnaz
   - Vint
3. Feed region voice into:
   - magic resolver prompt
   - generated dialogue base context
   - background detail jobs
   - deterministic fallback texture
4. Keep weirdness causal:
   - use region identity, old strata, promises, factions, and deeds
   - avoid a hidden "weirdness meter" deciding truth
5. Add tests/evals for tone:
   - region-specific resolver context includes voice/tradition notes
   - generic grimdark phrases are absent from imported content
   - generated fallback names use region subjects

Acceptance:

- The same operation can read differently in Vigovia, Hollowmere, and the Glasswild.
- Region voice appears in model context but does not itself create mechanics.
- Deterministic fallback still produces specific place texture without a provider.

Do not port:

- `region_for_zone` from WildMagic as canonical geography; it was explicitly crude.
- Generic dungeon ambience, torture props, skull-decoration texture, or dread-only prose.

## Phase 5 - Semantic Traits, Items, Props, And Services

WildMagic value: ordinary objects and traits became semantic affordances for later model
interpretation. Sorcerer should deepen this through unified entities and typed consequences.

Sources:

- `wildmagic/semantics.py`
- `wildmagic/items.py`
- `wildmagic/item_catalog.py`
- `wildmagic/item_generation.py`
- `wildmagic/props.py`
- `wildmagic/trade.py`
- `docs/ITEMS_AND_REAGENTS.md`
- `docs/SEMANTIC_EFFECTS.md`

Sorcerer targets:

- `Entity` + components
- `TraitComponent` or existing semantic component lanes
- `ClaimSourceComponent`
- `ItemComponent`, `FixtureComponent`, `ServiceComponent`, `MerchantComponent`
- `WorldConsequence` operations:
  - `add_tags`
  - `transform_entity`
  - `spawn_item`
  - `offer_trade`
  - `offer_service`
  - `request_service`

Tasks:

1. Import item/prop categories as data:
   - materials
   - semantic tags
   - region palettes
   - use profiles
   - spell-anchor subjects
2. Keep all objects unified as entities where practical:
   - props and items share position, identity, tags, inspection, targeting, and persistence
   - inventory may still use stacks, but the state boundary should be explicit
3. Expand semantic trait writing:
   - magic can add dormant traits
   - examine/read can reveal claim seeds
   - dialogue can reference traits through routed context
4. Build service protocol:
   - `offer_service`: NPC or generated provider exposes a service
   - `request_service`: player explicitly asks for a known service
   - `service_completed`: optional want/memory/standing follow-up through consequences
5. Add tests:
   - trait survives save/load and enters resolver context
   - trait has no automatic mechanic until invoked
   - service offer cannot perform without explicit request
   - service payment/effect/want update commit or roll back together

Acceptance:

- A weird object can be inspected, targeted by magic, gifted, used as a cost/focus, or become a
  promise anchor through general systems.
- Services are explicit engine interactions, not LLM trade intent.

Do not port:

- WildMagic's direct trade-settlement path.
- Separate prop/item identity systems.
- Item identification complexity before core item use is fun.

## Phase 6 - Promises, Quests, And Generated Payoffs

WildMagic value: promise binding was the strongest "words become future world" mechanism.
Sorcerer already has promise and payoff lanes; this phase makes them broad, reliable, and
content-rich.

Sources:

- `wildmagic/promises.py`
- `wildmagic/quests.py`
- `wildmagic/generation.py`
- `wildmagic/flesh.py`
- `docs/WORLD_PROMISES.md`
- `docs/EMERGENT_QUESTS.md`

Sorcerer targets:

- `PromiseLedger`
- `ClaimLedger`
- `GenerationSystem`
- `WorldConsequenceApplier`
- `PROMISE_PAYOFF_PLAN.md`
- `PROMISES_AND_PROPHECY.md`

Tasks:

1. Keep the distinction:
   - reported claim can be wrong
   - bound promise must be honored
   - realized promise writes receipts/canon
2. Expand buildable payoff families:
   - site
   - landmark
   - route
   - person
   - item
   - merchant stock
   - service provider
   - threat
   - door rule
   - prophecy/debt/timer
3. Add promise reservations against:
   - zone/direction
   - region subject
   - entity anchor
   - future scheduled event
4. Add quest as a view over promises, not a parallel quest engine:
   - typed objective specs
   - reward specs
   - objective progress from deeds, inventory, travel, talk, or service completion
5. Make optional flavor/flesh candidate-only:
   - model may name/decorate
   - deterministic skeleton stands
   - replay records materialized output
6. Add tests:
   - dialogue claim binds to route/site/person/item payoffs
   - concrete false/low-salience claims stay unbound
   - destination-zone payoff rollback leaves promise bound
   - save/replay preserves promise apply points and realized receipts

Acceptance:

- A player can hear or create a concrete future hook, travel, and find the world has honored it.
- Failed payoff application never leaves orphan entities, spent promises, or partial ledger state.

Do not port:

- Old independent quest storage.
- Promise realization side effects outside typed consequences.
- Flesh/prose as a source of authority.

## Phase 7 - Deeds, Factions, Gossip, And World-Turn Pressure

WildMagic value: deeds were the atom of emergent world response, and factions reacted through
multi-axis standing and finite resources. Sorcerer has the first lanes; this phase makes the
world socially legible.

Sources:

- `wildmagic/deeds.py`
- `wildmagic/deed_interpreter.py`
- `wildmagic/factions.py`
- `docs/FACTION_KILL_REPUTATION.md`
- `docs/GOSSIP_GRAPH_STRATEGY.md`

Sorcerer targets:

- `DeedLedger`
- `FactionLedger`
- `LegendLedger`
- `RumorLedger`
- `WorldTurnSystem`
- `NarrationSystem`
- `WORLD_REACTION_AND_EMPIRE.md`
- `IDENTITY_AND_ATTRIBUTION.md`

Tasks:

1. Expand deed recording:
   - magic witnessed
   - killing faction members
   - freeing captives
   - theft
   - service/reward completion
   - public mercy or cruelty
   - body-swap evidence
2. Record visibility and attribution:
   - who witnessed
   - soul vs current identity
   - faction victim/beneficiary
   - place and region
3. Add deterministic deed rules first:
   - standing deltas by faction roles
   - legend tag updates
   - suspicion/warrant updates
   - rumor seeds
4. Add LLM deed interpretation only for ambiguous high-salience deeds, as candidate output.
5. Use world-turn budget for:
   - rumor spread
   - faction pressure
   - resource recovery
   - promise stir
   - private want stir
   - clerk memos and notices
6. Add player-facing legibility:
   - journal summaries
   - rumor lines
   - standing mood/pressure
   - debug exact numbers for agents
7. Add tests:
   - one deed affects multiple factions differently
   - witnessless deed does not become public rumor
   - faction pressure spends resources and can recover
   - rumor transport records provenance
   - world-turn compound move rolls back on child rejection

Acceptance:

- Player actions create social consequences that are visible, inspectable, and replayable.
- Faction reactions are expenditures, not hidden threshold scripts.

Do not port:

- WildMagic daily-clock heartbeat. Sorcerer should use bounded pump points.
- Any direct faction mutation outside `update_faction_*` consequences.

## Phase 8 - Body/Soul And Identity Deepening

WildMagic value: control followed a soul/body split, and body swap was a central mechanical
possibility. Sorcerer has a first possession seam; this phase hardens identity.

Sources:

- `wildmagic/engine.py` body-control/swap code
- `wildmagic/models.py`
- `docs/IDENTITY_AND_ATTRIBUTION.md`

Sorcerer targets:

- `SoulComponent`
- `ControlledEntityId`
- `BodyStatsComponent`
- `ActorComponent`
- `set_controlled_entity`
- `swap_souls`
- `update_control`
- `change_faction`
- `PossessCommand`

Tasks:

1. Preserve rules:
   - soul carries memory, mana, Attunement, Composure, legend
   - body carries Vigor, wounds, inventory, appearance
   - controlled pointer follows the inhabited body
   - vacated body remains a real entity
2. Expand identity attribution:
   - warrants attach to observed body/identity
   - legend attaches to soul
   - disguise and body swap can split or merge evidence
3. Add inspection/debug views:
   - current body
   - soul id
   - known identities
   - evidence links
4. Add tests:
   - control pointer follows swap through GUI/CLI views
   - inventory stays with body
   - soul stats stay with soul
   - deed attribution follows soul unless evidence says otherwise
   - save/load preserves swapped state

Acceptance:

- Body swap changes play affordances without forking the player model or renderer code.

Do not port:

- Any hard-coded "player entity" assumptions from old systems.

## Phase 9 - Agent Tooling, Replay, And Evals

WildMagic value: the game became debuggable because runs produced artifacts: action records,
audits, replays, eval reports, and autoplay findings.

Sources:

- `wildmagic/cli.py`
- `wildmagic/replay.py`
- `wildmagic/autoplay.py`
- `wildmagic/speleval.py`
- `wildmagic/dialogue_eval.py`
- `wildmagic/lore_eval.py`
- `docs/AGENT_PLAYTESTING.md`
- `docs/MODEL_CONFIG.md`

Sorcerer targets:

- `src/Sorcerer.Cli`
- `EpisodeRunner`
- transcript/replay providers
- `docs/CLI_AND_AGENT_PLAYTESTING.md`
- `docs/TESTING_AND_EVALS.md`

Tasks:

1. Stabilize action-result materialization:
   - command text
   - turn before/after
   - consumed turn
   - technical failure
   - magic/dialogue/parser/background records
   - deltas
   - debug observation
2. Version transcript and replay formats.
3. Record apply-point materialized outputs:
   - spell resolution
   - dialogue response
   - dialogue parser result
   - promise/flesh/canon/background text
4. Add from-audit eval modes:
   - reparse spell output
   - rescore dialogue directness/voice/claim quality
   - rescore magic specificity/consequence/surprise/tone
5. Expand unattended episodes:
   - scripted smoke
   - deterministic mock agent
   - future live agent command chooser
   - invariant checks after each step
   - findings report
6. Add tests:
   - transcript replay avoids live provider calls
   - old schema rejection/upgrade behavior is explicit
   - episode invariants catch invalid state
   - eval reports include latency and selected cards

Acceptance:

- A failing live-model run can be investigated and substantially replayed without calling the
  model again.
- Agents can run many episodes and report reproducible findings.

Do not port:

- Python `.env` write-back.
- Pygame debug panels as gameplay APIs.
- Subprocess-heavy provider autostart as a core dependency.

## Phase 10 - Content Import Order

Import content only when the target system can use it structurally.

Order:

1. Lore cards (from `C:\Games\WildMagic\content\lore\*.md`, 20 cards total):
   - Vigovia/Empire/Censorate (`vigovia.md`, `empire.md`)
   - traditions: crystal, bone, blood, woven, charter, shadow purge
   - cultures/peoples: Stalnaz, Vint, Brall, Ryolan, Threen, Parn, merfolk, birdfolk,
     Gontark (goatfolk curses), Monteary (horse-realm), Ontria (yoghurt tribes),
     Rentacosta (free city of sailors)
   - Note: place identities such as Hollowmere/frontier, Glasswild, and Saltmarket are not
     lore-card files; they live in `regions.py`/`game_data.py` and import as region voice cards
     in Phase 4, not here.
2. Region voice cards:
   - Hollowmere
   - Vigovia
   - Glasswild
   - Saltmarket
   - Stalnaz/Vint/Brall/Ryolan variants
3. Item and prop palettes:
   - imperial documents and tools
   - tradition objects
   - curios and reagents
   - readable/claim-seeding props
4. Population templates:
   - residents
   - merchants
   - guides
   - functionaries
   - rebels/separatists
   - folk practitioners
5. Promise payoff archetypes:
   - hidden road
   - checkpoint
   - shrine
   - merchant stock
   - folk-magic service
   - warrant/threat
6. Eval corpus:
   - spell prompts from `SPELL_COMPENDIUM.md`
   - unsupported-spell list as future capability backlog
   - dialogue eval situations

Acceptance:

- Imported content has subject tags and at least one consumer: resolver context, dialogue
  context, generation, background detail, promise payoff, or eval.
- No imported content is only decorative unless it is explicitly part of a tone/eval corpus.

## Cross-Cutting Tests

Every import phase should add or extend tests in these categories:

- turn consumption
- state validation
- transaction rollback
- target binding
- provider technical failure
- intentional rejection
- save/load round trip
- transcript/replay materialization
- CLI/Godot parity where player-facing
- prompt/router context budget where LLM-facing
- audit record contains source and normalized output

Wide persistence fixture rule:

- When a new durable lane is added, extend the broad save/load/save fixture rather than relying
  only on incidental scenario tests.

Eval rule:

- If the import affects LLM behavior, add an eval or audit-review path before relying on it in
  play.

## Prioritized Backlog

This backlog is the authoritative execution order (see "Reading This Plan"). It interleaves tasks
across phases: finish P0 before P1, and P1 before P2, pulling each task's contract, tests, and
do-not-port rules from its parent phase.

P0 - harden foundations:

- Parser repair tests mined from `resolution_parsing.py`.
- Magic capability routing metrics and deterministic selection tests.
- Lore-card conversion for core canon.
- Dialogue parser prompt rule: static lore is not claim fodder.
- Replay materialization for dialogue router/parser and background text.

P1 - deepen the flywheel:

- Region voice/tradition palette in magic resolver context.
- Semantic item/prop palettes as entity content.
- Service protocol hardening.
- More promise payoff archetypes.
- Deed rule expansion and faction pressure tests.
- Gossip graph privacy/provenance.

P2 - richer world:

- Full generated-site archetype catalog.
- Population concerns/dispositions.
- Quest-as-promise objective matcher.
- LLM-assisted deed interpretation for ambiguous deeds.
- Offline feel judges for magic and dialogue.
- Wider autoplay campaign reports.

Deferred:

- Contradictory lore tiers and belief variants.
- Deep item identification/palette economy.
- Full geopolitical victory path beyond initial emperor kill route.
- Unsupported-spell families that require entirely new spatial/rendering primitives.

## Import Review Template

Use this template for each concrete import PR or slice:

```text
WildMagic source:
Sorcerer owner:
Import type: contract | content | mechanic | tooling | eval
Player-facing capability:
Engine primitive:
Typed consequences used:
Persistent state:
Views/CLI/debug exposure:
Provider/audit/replay behavior:
Tests:
Do-not-port notes:
Acceptance result:
```

## One-Line Rule

WildMagic is the field notebook. Sorcerer is the instrument panel. Bring over the readings,
the proven contracts, and the best specimens, but rebuild every control through Sorcerer's
typed engine.
