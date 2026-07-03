# AGENTS.md

This project is Sorcerer, a standalone Godot 4 C# roguelike about wild, player-authored
magic. It is a clean-slate spiritual successor to Wild Magic, not a compatibility port.

The core rule:

> The model may interpret magic. The engine decides what is real.

Your job as an agent is to help build a clean, renderer-agnostic, agent-playable
simulation where strange spell prompts become reusable mechanics.

## What We Are Building (read `docs/GRAND_VISION.md`)

The pitch is one line: **you can do anything, and it actually matters what you do.** Sorcerer
is a long roguelike where the player improvises wild magic in a living world that keeps
answering. The feeling, in priority order, is **authorship, wonder, and mischief**, then
tactical cleverness - danger is real, but this is not a punishing survival game. The arc is
long: grow in power, weaken the Empire, and eventually kill the emperor. No meta-progression;
each death rolls a new world.

The setting is **color under marble**: a lush, peculiar, culturally-specific world of folk
traditions, policed by the cold, reasonable, genuinely-not-evil Empire of Vigovia. You are the
pest. The tone is **wonder with teeth** - ecstatic, feral magic with strange, gorgeous costs,
never generic grimdark. See `docs/WORLDBUILDING.md` (canon) and `docs/AESTHETICS_AND_TONE.md`
(voice).

**The one engine idea: prose becomes mechanics.** There is no wall between flavor and rules. A
description - a trait, a rumor, a secret, a name - is a *dormant mechanic* that becomes real
when the model, resolving an action, decides the situation squarely calls for it. The player
improvises in language; the model proposes meaning; the engine validates, prices, and applies
it as authoritative truth. Freedom on the surface, coherence underneath - **structured
freedom**.

**Richness comes from interaction, not from scripted content.** We build a small number of
general systems - wild magic, items and entities, traits, promises, deeds and reputation,
factions, relationships, regions - and let them collide. A spell witnessed by a villager
becomes a rumor becomes a recruit; an imbued gift becomes a remembered gesture becomes a bond becomes a confided secret
becomes a place the world then delivers. None of that is coded as a story; it falls out of
systems handing off to each other. So when you build a mechanic, ask whether it **creates more
combinations than it adds complexity**, and build the smallest *general* capability that
unlocks it - never a handler for one spell or one story.

## First-Class Requirements

- The authoritative engine lives in Godot/C#.
- The game must ship standalone without a Python runtime.
- GUI and CLI must drive the same backend.
- The CLI must be capable enough for AI agents to fully play and playtest the game.
- Renderer code must be replaceable. ASCII is the first renderer, not the architecture.
- Entity unification is central.
- Body swap is central.
- Props and items should be unified under the same entity/component model wherever
  practical.
- Provider code must support local models and API-key providers behind one interface.
- Mock providers exist for fast agent and regression playtests, not as the design target.
- Promises and prophecy are foundational.
- Wild magic quality is a first-order feature, not an implementation detail.

## Do Not Split The Game

Sorcerer has at least two interfaces:

- Godot GUI: the human-facing game.
- Headless CLI: the JSON-first agent-facing game.

They must stay exactly synced because they must call the same `GameSession` and engine
logic. A player-facing capability that exists only in the GUI or only in the CLI is a bug.

When adding a command, menu flow, spell mechanic, inventory feature, character flow,
dialogue action, or world-system readout, update both the GUI path and CLI path in the
same change.

Agents may receive perfect debug state in the CLI. The CLI does not need to imitate human
perception unless a test explicitly asks for a player-knowledge-only view.

## Design Principles

- Prefer general mechanics over prompt-specific behavior.
- Prefer schema repair and normalization over hard-coded spell fallbacks.
- Prefer data-driven operation metadata over scattered magic strings.
- Prefer actor-agnostic actions over player-only action paths.
- Prefer read-only state views over renderer access to rule internals.
- Prefer durable state lanes over free-text flags that become invisible rules.
- Prefer the shared typed consequence grammar for model- or content-authored side effects.
  Dialogue, books, promises, services, AI plans, factions, and magic should submit validated
  consequences instead of mutating systems through bespoke helper code. Consequence families
  include immediate tactical effects such as damage, actor resources, status, terrain creation/update, movement, summoning,
  transformations, resistance/weakness, delayed damage creation/release, status acceleration, behavior tag creation/update,
  persistent effect creation/update, and tile flow creation/update, plus durable social/world effects such as memory, bond,
  claim, want, stock, trade, service offer/request, route, fixture/place spawn, item transfer, equipment, faction, control policy, controlled-entity pointer, run status, faction standing/resources, legend, canon, world-turn audit, world flag, scheduled event creation/update, trigger creation/update,
  suspicion/deed recording and updates, rumor creation/update, or promise changes.
- Prefer the shared apply point as well as the shared schema. Engine-owned flows should apply
  consequences through `GameEngine.ApplyConsequence` or an injected consequence sink; construct a
  local `WorldConsequenceApplier` only behind `WorldConsequenceGuard` for standalone helpers,
  tests, or deliberately detached generated-state sandboxes.
- Detached generated-state sandboxes are still apply-point packets: start them from the real
  global ledgers, stage zone-local entities separately when needed, and commit ledger changes
  such as canon, memories, rumors, scheduled events, world flags, RNG, and serial state only after
  all child consequences succeed.
- Generated zone terrain texture should also go through typed consequences, usually hidden
  `set_terrain` deltas in the detached generation sandbox. Do not add terrain flavor by writing
  directly to the active map unless you are loading an already-authoritative zone snapshot.
- Do not reintroduce public engine convenience helpers that hide ordinary world mutations behind
  old-style methods like "apply status", "add promise", or "record deed". If a helper is needed,
  keep the helper local to tests or a narrow system boundary and have it submit a typed
  `WorldConsequence`.
- When a model or content source produces both a claim and an immediate action for the same
  local affordance, attach the ledger record to the already-applied consequence instead of
  applying duplicate state changes. Example: a service claim plus `reveal_service` should produce
  one `offer_service` mutation and one claim update.
- For authored documents, signs, fixtures, books, and props that should seed future hooks, prefer
  `ClaimSourceComponent` plus `read`/`examine` consequences over bespoke interaction code. The
  object supplies claim seeds; the engine still records claims and binds promises through
  `record_claim`, `record_rumor`, `create_promise`, and `update_claim`.
- Prefer small composable consequences: damage, status, terrain, movement, summoning,
  faction changes, tags, memory, traits, inventory, curses, promises, triggers, timers,
  and bundled social outcomes such as `free_captive` when one player-visible event must
  transactionally update faction/control/bond/want/deed/message state.
- Prefer `WantComponent` for notable NPC motivation. Opening NPCs can have authored wants;
  generated NPCs should receive deterministic wants at instantiation. Do not create bespoke quest
  flags when an NPC want plus claims, promises, and typed consequences can express the same
  pressure. When a want changes because of dialogue, services, world-turns, or magic, route the
  change through `update_want` rather than setting the component directly. Use the consequence's
  optional hidden memory child when the NPC should remember why the want changed.
- When a service should satisfy or redirect its provider's want, put completion metadata on the
  `ServiceOffer`/`offer_service` consequence and let `request_service` submit `update_want`
  after success; do not create service-specific quest flags. Explicit `request` commands should
  call the shared `request_service` consequence and own only command resolution and turn
  consumption.
- Treat memory creation as `record_memory` even when the surface verb is `edit_memory`; memory-edit
  side effects such as calming hostility should emit child consequences rather than writing bonds or
  ledgers inline. Witnessed deeds should leave NPC witness memories through `record_memory` with
  deed provenance, not through a bespoke witness-knowledge store.
- Treat `BondLedger` rows as crystallized relationship facts. Inspections, dialogue context, and
  eligibility checks should use `TryGet` plus a neutral in-memory fallback; only `update_bond`
  should create or mutate bond records.
- For provider-backed wild magic and future slow model-backed actions, keep resolution and
  application separate when latency or replay matters. A materialized result is evidence, not
  authority: the apply point must re-parse, normalize, validate against current state, and mutate
  through engine operations/consequences.
- Prefer the bounded `WorldTurnSystem` for world initiative at pump points. Rumor spread, promise
  stirring, private high-salience want stirring, faction expenditures, memos, and future autonomous moves should be budgeted and audited
  there through `record_world_turn` instead of becoming unbounded hidden turn side effects. If a
  world-turn move has child effects, such as rumor spread writing heard-rumor memories or want
  stirring writing private NPC memory, keep them
  as typed child consequences under the same budgeted move. Rejected child packets should roll back
  the whole move and leave only rejection diagnostics plus a hidden `worldTurnSkipped` audit.
- When replaying generated, consequence, or world-turn deltas into action-result messages or durable
  message logs, use `StateDelta.IsPlayerVisible()` / `PlayerMessages()` so audit-only or
  player-invisible records stay available to debug state and transcripts without duplicating or
  leaking into player-facing logs.
- Keep technical LLM failures from consuming turns.
- Keep intentional magical rejections turn-consuming.
- Keep background generation resource-aware and user-configurable.
- Keep costs normally post-cast.
- Do not build full engine spell-budget auto-pricing early.
- Do not add LLM calls for trade intent; use engine-side interaction rules and explicit
  commands instead.

## Engine Authority

LLM output is untrusted. It must go through:

1. parse
2. repair common shape mistakes
3. normalize aliases and references
4. validate operation types and fields
5. preflight costs and targets
6. apply transactionally
7. validate resulting state
8. produce action results and audit records

Do not let renderer code, prompt code, or provider code directly mutate world state.
When an LLM, dialogue provider, authored text, prophecy, service, magic resolver, faction, or
background job proposes state change, normalize it into the narrowest existing typed consequence
before applying it. Consequences are fast by default; deferred or world-pump timing must be an
explicit field, not an implied side effect of which subsystem produced the change. Non-immediate
timing is delivered by the shared scheduled-event pump for one delayed consequence; use explicit
scheduled-event or trigger consequences only for broader events, repeating triggers, auras, wards,
or hooks. Add new
consequence types only when they unlock broad reuse across systems.

## Entity Unification

Everything interactable should be represented as an entity with components:

- controlled bodies
- NPCs
- enemies
- items
- props
- books
- doors
- corpses
- husks
- summoned beings
- magical anchors
- promise sites

Avoid parallel systems where "item", "prop", "NPC", and "enemy" each reinvent identity,
position, rendering, targeting, persistence, and inspection.

Body swap should be a natural consequence of the model:

- control points to an entity id
- soul exchanges should use `swap_souls`, not direct `SoulComponent`, soul-ledger, or
  actor-stat mutation
- inventory stays with the body
- stats and appearance come from the body
- the vacated body becomes an entity with no agency
- the CLI, GUI, FOV, resolver context, and camera follow the controlled entity
- changes to the controlled entity pointer should use `set_controlled_entity`, not direct
  `ControlledEntityId` assignment, except during scenario setup or save/load restoration

## Renderer Boundary

Game rules must not depend on Godot rendering nodes.

Renderers consume read-only views such as:

- map tiles and glyphs
- visible entities
- player status
- inventory and equipment
- selected target
- messages
- journal and promises
- LLM/debug status

It should be possible to add a tile renderer later without changing combat, spell
application, inventory, AI, or world generation.

## CLI And Agent Playtesting

The CLI should expose structured observations and action results for agents. Human-readable
commands are fine, but agents should not need screen scraping.

Good agent-facing affordances:

- `--provider ollama` for ordinary playtesting and resolver quality evaluation
- `--provider mock` for quick deterministic troubleshooting, evals, and regression checks
- `--json` for machine-readable observations/results
- `--command`, `--script`, `--transcript`, and `--episode-log` for reproducible runs
- transcript replay should materialize live spell JSON, generated dialogue responses, dialogue
  claim-extraction results, background text results, and quickstart setup instead of calling live
  providers
- `inspect` or equivalent structured state view
- optional perfect debug state
- local coordinate map
- explicit commands for inventory, targeting, talking, reading, promises, and standing
- stable action-result fields: success, action, consumedTurn, technicalFailure,
  turnBefore, turnAfter, messages, resolution, errors

## Background Jobs

Background generation can exist, but it must not secretly starve the foreground game.
Provider-backed background text is candidate prose only; it must still become durable through the
background job queue and the narrow typed consequence for that job purpose (`add_canon`,
`update_rumor`, etc.). Keep deterministic fallback available when the provider is disabled or fails.

Design it with:

- max concurrent background jobs
- separate foreground/background provider settings
- easy disable flags
- visible developer queue
- audit logs
- clear replay/save behavior for generated outputs

## Documentation Requirements

Keep durable docs current when you add or change architecture:

- `README.md`
- `NORTHSTAR.md`
- `AGENTS.md`
- `docs/ARCHITECTURE.md`
- `docs/DOCUMENTATION_MAP.md`
- `docs/GRAND_VISION.md`
- `docs/CLI_AND_AGENT_PLAYTESTING.md`
- `docs/WILD_MAGIC_CONTRACT.md`
- `docs/MAGIC_RESOLVER_ARCHITECTURE.md`
- `docs/SEMANTIC_EFFECTS.md`
- relevant subsystem docs

Do not leave important behavior only in private notes, chat history, or comments inside
temporary code.

## Testing Expectations

For engine changes, add focused tests around:

- turn consumption
- state validation
- transaction rollback
- target binding
- action results
- CLI parity
- renderer independence
- LLM technical failure behavior
- intentional rejection behavior
- body swap and controlled-entity behavior

For Godot GUI changes, verify that the same behavior is reachable through the CLI.

## Development Style

- Read existing docs before editing.
- Keep changes scoped.
- Use the C# hygiene path: `.editorconfig`, `dotnet format`, and pre-commit hooks. Ruff is
  for the old Python repo and should not be added here unless Sorcerer grows Python tooling.
- Do not add one-off handlers for a single spell phrase.
- Do not build GUI-only mechanics.
- Do not let malformed LLM output partially mutate state.
- Do not hide a second spell engine inside fallback code.
- Do not make semantic flavor mechanically binding unless it crystallizes into an
  explicit engine operation. Entity traits are dormant mechanics the resolver may surface;
  see `docs/SEMANTIC_EFFECTS.md` (semantic by default, mechanical on demand, never on the
  critical path; traits are resolver-authored only).
- Do not let magic become merely valid but boring. When improving the resolver, measure
  specificity, consequence, and surprise as seriously as JSON correctness.
- Use clear, durable names based on behavior, not temporary planning labels.

When in doubt, build the smallest general system that unlocks ten weird spells.
