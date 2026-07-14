# Design Decisions

This document records durable decisions for Sorcerer so implementation work does not
have to recover them from chat history.

## Settled Decisions

- Sorcerer is a clean-slate spiritual successor to Wild Magic.
- The project name is Sorcerer.
- The implementation target is Godot 4 with C#.
- The game should ship standalone without a Python runtime.
- The authoritative engine lives in C# inside the Godot project, not in an external
  Python backend.
- The GUI and CLI must use the same `GameSession` backend.
- The CLI is first-class, JSON-first, and must be strong enough for AI agents to fully
  playtest.
- The CLI should be a separate .NET console executable referencing the core.
- The core should avoid Godot-specific types where practical.
- Agents may access perfect debug state.
- ASCII is the first renderer.
- The renderer must remain separate from game state and rules.
- Entity unification remains central.
- Everything meaningful that magic can target should be an entity, including props,
  items, doors, books, corpses, fixtures, and eventually addressable wall or terrain
  features.
- Body swap remains architecturally central but should be rare in play.
- Personhood follows the soul; inventory follows the body.
- Props and items should be unified wherever practical through components.
- Provider support should include local providers and API-key providers behind one
  interface. Ollama can be supported, but should not be the only real path.
- Mock providers exist for fast testing and agent playthroughs.
- Background generation remains part of the vision, should default low, and must be
  configurable and resource-aware.
- Deterministic replay is desirable if it stays cheap. It is not a hard requirement
  if it imposes large architectural cost.
- Debug and audit tools should exist early as developer affordances, even if they later
  become hidden screens or separate tools.
- Runs should be long.
- The eventual win condition is killing the emperor; the emperor should exist as a killable
  character so the game can be technically won before the final encounter systems are elaborate.
- There is no meta-progression; grave markers or chronicles are acceptable memorials.
- The Empire is not reformable in the main arc. It can be destroyed, evaded, or hidden
  from.
- Promises and prophecy are foundational.
- Costs are normally revealed after casting.
- Full engine-side spell-budget auto-pricing is deferred until much later.
- Protected/treasured inventory is a core mechanic.
- Combat should be meaningful but not constant.
- Wild magic should be the most powerful magic and should remain central.
- Reliable charter-style spells can exist, but should be less powerful and less open than
  wild magic.
- LLM trade-intent calls should be cut; trade should be driven by explicit interaction and
  engine-side rules.
- The first playable should prioritize combat with an excellent live wild-magic resolver.
- Content should bias data-first for future modding.
- Renderer animation should prefer engine `StateDelta` records.

## Settled Decisions (Player Feel and Product Modes)

Locked by the owner calibration on 2026-07-13. The detailed execution contract lives in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

- Ordinary model-free play should foreground curiosity: travel, exploration, culturally specific
  inspectable scenery, useful props, relationships, and reliable charter magic.
- Combat is medium-infrequent and only medium-lethal before the capital. It rewards deterministic
  mastery through generous intent telegraphs, learnable attack patterns, ranges, defenses,
  weaknesses, and terrain counters. Generic rat/wolf filler encounters are out of scope.
- Retreat, surrender, bribery, disguise, theft, avoidance, social leverage, transformation, and
  force should all be strongly viable.
- Inventory capacity is extremely permissive. Scarcity comes from choosing which desirable spell
  components and gear to spend, not encumbrance, durability, or backpack maintenance.
- A worthwhile fresh wild-cast opportunity should appear at least every three minutes in ordinary
  play. Wild magic should resolve very strongly and may solve a major problem at a proportionate,
  legible cost; routine casts should not all be crippling. Thirty seconds is the experiential
  ceiling for the supported local floor model.
- Within-run stat growth is a recurring progression lane. Rare items used as fitting spell
  components may participate in validated permanent stat gains without item-specific recipes.
- Body swap should be highly transformative when it occurs. Roughly ten followers should remain
  manageable through autonomy and group orders rather than party micromanagement.
- False identities should be relatively easy for competent players to establish and keep apart.
  Soul recognition by a deep friend may require evidence such as a shared memory. Witness and
  identity attribution must be explicit in the player log.
- NPC initiative occurs reasonably often; follower decisions are reasonably predictable from
  known state; conversation and relationships are a major portion of play. Journals provide useful
  tracking and may be explicit about known wants and plausible satisfaction.
- Classic mode is permadeath with save-and-exit suspension only; death deletes the save. Roleplay
  mode uses the identical simulation and difficulty with free save/load. It replaces checkpoint
  restoration. Neither mode has meta-progression, and a new world is one input away after death.
- The full-density slice is Marble Containment Yard → Hollowmere Margin → Brall → Vigovian
  Capital. Brall is remembered for collaborative boasting about other people's deeds through the
  shared witness/rumor/relationship chain.
- Before public Early Access, there is no compatibility obligation. Delete obsolete code and
  break/regenerate unreleased saves, transcripts, fixtures, and schemas rather than adding legacy
  fallbacks. Public save migrations begin with the first EA compatibility promise.

## Settled Decisions (Resolver, Magic, and Systems)

Locked while specifying the core execution model and adapting the inherited design docs. See
[CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md), [SEMANTIC_EFFECTS.md](SEMANTIC_EFFECTS.md),
[GRAND_VISION.md](GRAND_VISION.md), [EMERGENT_WORLD.md](EMERGENT_WORLD.md),
[CHARACTER_AND_STATS.md](CHARACTER_AND_STATS.md), and
[PROMISES_AND_PROPHECY.md](PROMISES_AND_PROPHECY.md).

- Wild-magic operations are code-owned verbs - each owns its own validate and apply - enriched
  by data-driven cards (prompt guidance, examples, aliases). Code is the one source of truth
  for the operation set and behavior; data only enriches.
- Wild magic resolves validate-all-then-apply-all inside a transaction. Malformed or rejected
  magic never partially mutates state.
- Turn-cost contract: technical failures (provider down, unparseable JSON, an operation that
  threw) consume no turn; rejections (model refusal, or engine validation such as an
  unsupported operation, impossible target, or illegal/overpowered spell) consume a turn.
- Long casts use a pending-cast async model: the session does not block the turn loop, the turn
  is consumed only on resolve, and the CLI awaits and returns one final envelope. This is also
  the seam for the future casting minigame and agent-skip.
- A seeded engine RNG is threaded from the start; replay records resolved model JSON so live
  magic stays reproducible. This is the cheap determinism the earlier replay decision wanted.
- The default agent observation is player-equivalent perception; perfect debug state is opt-in.
- Resolver context is allowed to know more than the player when useful for coherent magic, but
  hidden facts must be annotated as hidden/debug-only and must not leak into player renderers.
  Targets resolve by id, selector, or fuzzy name to a set, so single and group targeting are one
  mechanism; the engine remains authoritative about whether a target can actually be affected. A
  target is not illegal merely because it is hidden from the player.
- Entity descriptions and traits are dormant mechanics the resolver surfaces and may cash into
  effects: semantic by default, mechanical on demand, never on the critical path.
- Traits are resolver-authored only. The player never directly authors a trait; they may only
  request through spell text. There is no player-facing trait-authoring command, now or later.
- Trait crystallization (promotion to a standing mechanic) is the documented target but is
  deferred until the status/tag system exists.
- World reaction is event/pause-driven, applied at explicit engine pump points, not a periodic
  real-time or daily tick.
- The model interprets meaning and narrates reaction; it never simulates. The deterministic
  skeleton must stand with zero model calls.
- The character stat model is the Vigor / Attunement / Composure triad; Vigor follows the body,
  while Attunement and Composure follow the soul. HP follows the body; mana follows the soul.
  Composure is the wildness dial tied to casting performance.
- Exploration memory follows the soul, while current FOV follows the controlled body.
- Witnessed deeds distinguish suspicion from attribution: seeing an effect without seeing the
  caster creates suspicion, and only line of sight to the player within 20 turns can connect the
  deed to the player.
- Faction finite-resource pools are debug-visible only; player-facing standing uses pressure,
  mood, warrants, and visible consequences rather than raw resource numbers.
- Dialogue uses the same engine-authoritative pattern as wild magic: a model may parse organic
  player speech into structured intent and voice the NPC, but engine rules validate and apply any
  mechanical outcome.
- The promise economy is always-honor (yes-and): not everything said binds, but every bound
  promise the world keeps.
- GRAND_VISION.md is the central guiding vision and supersedes GAME_VISION.

## Settled Decisions (Casting Minigames)

Locked while designing the casting-minigame system. See
[CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md).

- Casting performance is applied by the engine after the provider returns; the resolver
  never sees it. The minigame plays during the provider call, so the score cannot enter
  the prompt.
- The engine combines model-reported severity with the final performance: a powerful spell
  cast on a poor performance can be converted into a mishap drawn from engine-side
  complication tables. The narrator may flavor a mishap afterward; narration never blocks
  the apply.
- Skipping is the neutral baseline and playing is EV-neutral: on average, playing and
  skipping produce the same power level. Minigames are a latency mask first, entertainment
  second; skipping is never punished.
- Scoring is rate-based (points per second) so unknown provider latency stays fair. If the
  provider returns before a minimum scoring window, the attempt is discarded as neutral.
  Games should support indefinite runtime and may run one or two seconds past the
  provider's return.
- Games may report up to two axes (e.g., speed -> power, accuracy -> control) when
  legible; many report a single Wild-to-Strong axis.
- The spell does not shape the minigame; the GUI pulls a random game from a repertoire
  whose parameters are computed locally with no model call.
- Minigames assume mouse input for now. The CLI defaults to neutral and may inject a fixed
  `CastPerformance` for debug and agent testing.

## Architectural Biases

Sorcerer should optimize for:

- cleaner architecture over compatibility
- general mechanics over prompt-specific handlers
- visible contracts over hidden global behavior
- narrow provider interfaces over model-specific coupling
- explicit state views over renderer access to internals
- actor-agnostic actions over player-only paths
- lightweight replay artifacts over heavy rewind machinery
- JSON-first agent surfaces over terminal prettiness
- audits and evals over guessing at resolver quality
- player-reactive emergence over detailed life simulation

## Compatibility Position

Sorcerer does not need to reproduce Wild Magic behavior exactly.

It should preserve the best ideas:

- player-authored wild magic
- engine-authoritative validation
- CLI and GUI parity
- agent playability
- reusable effect operations
- entity unification
- body swap
- promises and prophecies
- local model support
- background generation

It should freely redesign implementation details that became tangled, premature, or
hard to explain.
