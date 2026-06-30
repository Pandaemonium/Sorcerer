# Build Plan (Handoff)

This is the executable, implementation-level plan for the next phase of Sorcerer. It is written
to be handed to another agent and followed end-to-end. It is the tactical companion to the
strategic [MATURITY_ROADMAP.md](MATURITY_ROADMAP.md); where they overlap, the roadmap gives the
"why" and this document gives the "how."

**Read before you start:** [GRAND_VISION.md](GRAND_VISION.md) (what the game is),
[../NORTHSTAR.md](../NORTHSTAR.md) and [../AGENTS.md](../AGENTS.md) (the non-negotiables),
[AESTHETICS_AND_TONE.md](AESTHETICS_AND_TONE.md) and [WORLDBUILDING.md](WORLDBUILDING.md) (the
voice and canon), [CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md) and
[MAGIC_RESOLVER_ARCHITECTURE.md](MAGIC_RESOLVER_ARCHITECTURE.md) (the cast pipeline).

## The Spirit (do not lose this while building plumbing)

> You can do anything, and it actually matters what you do.

Every system below exists to serve **authorship, wonder, and mischief**. We are not building a
spell list and a stat sim; we are building a world that **keeps answering**. Two disciplines make
that possible and must never be violated:

1. **The engine owns truth.** The model proposes meaning and paints reaction; it never mutates
   state. Untrusted output is normalized → validated → priced → applied transactionally → audited.
2. **The deterministic skeleton stands with zero model calls.** Ledgers hold truth, rules decide
   consequences, the model only reads ambiguous *meaning* and *narrates*. Everything must pass
   with `--provider mock`.

And the thesis that decides every design call:

> **A new system should create more combinations than it adds complexity.** Build the smallest
> *general* capability that unlocks ten weird situations. Never a handler for one spell or one
> story.

When you finish a phase, the game must be more playable by both a human (Godot) and an agent
(CLI), and the new capability must be reachable from **both** interfaces. A feature in only one is
a bug.

## Scope Of This Handoff

Detailed, file-level instructions are given for **all phases, Phase 0 through Phase 8.** Phases
0-4 (engine refactor + perception, character, deeds/legend/reputation, factions, social) are the
near-term sprint and are the most settled. Phases 5-8 (procedural world, magic/item depth,
narrator/lush content, persistence/win) are fully specified but carry a few open **product
decisions** flagged inline as `DECISION:` callouts; resolve those before starting the affected
phase. Each phase still ends in a playable slice for both human and agent.

Decisions baked into this plan (from the product owner):

- **Start at Phase 0.** The roadmap's Phase 1/2 sequencing resumes after the Phase 0 refactor and
  perception prerequisite is complete.
- **Refactor the engine first.** `GameEngine` is a 2,433-line class trending toward the
  predecessor's 5,519-line monolith. Phase 0 splits it into composed systems *before* new
  features land. The game is early enough that this may be a broad refactor, provided the public
  `GameSession`/`GameEngine` surface stays stable and behavior is characterized first.
- **Build real FOV now.** Witness-gated deeds (Phase 2) ride on a true line-of-sight perception
  model, shared by the renderer, resolver context, and the deed system.
- **Exploration memory follows the soul, not the body.** Body swap changes the eyes and location,
  but not what the player-soul remembers having explored.
- **The resolver may know more than the player.** Player renderers obey perception; resolver
  context may include hidden or non-player-visible facts when useful, as long as those facts are
  annotated by visibility and the engine still validates what can actually happen. Wild magic is
  not limited by player knowledge; do not reject a target solely because the player has not seen it.
- **Witness attribution is not omniscient.** An actor who sees an effect or target but not the
  caster becomes suspicious; if they get line of sight to the player within 20 turns, they may
  connect the player to the deed. If they never see the player, they do not magically know who
  caused it.
  The initial suspicion attribution window is **20 turns**.
- **Stats split across body and soul.** Vigor belongs to the body; Attunement and Composure belong
  to the soul. Mana belongs to the soul; HP belongs to the body. Origins are implemented in this
  sprint and must seed both sides cleanly.
- **Faction resource numbers are debug-only.** Player-facing standing uses pressure/mood language;
  exact finite-resource pools appear in debug observations and tooling.
- **Dialogue should feel organic.** Free-form player dialogue is parsed by a dialogue resolver into
  structured intent/proposed outcomes; the engine validates and applies mechanics. The model may
  interpret and voice, but it never directly mutates state.
- **The emperor should exist and be killable.** Complicated reachability, court, and final-encounter
  systems can be thin, but the game should become technically winnable once the emperor is present.
- **Target a small local model** (Ollama, qwen-class on consumer hardware) as the resolver bar.
  This means heavy parser repair and tight, well-guided operation cards are first-class work, not
  polish.

## Inherited Wisdom From Wild Magic (`C:\Games\WildMagic`)

The predecessor is ~48k lines across 74 modules. **Do not port by module.** Port by contract.

**Keep (the contract or the hard-won knowledge):**

- Shared session/action backend + `ActionResult` turn contract — already in Sorcerer.
- The cast pipeline discipline — already in Sorcerer, and its `OperationRegistry`/`IOperation` is
  cleaner than WildMagic's 2,700-line effect switch. Do not regress this.
- `resolution_parsing.py` (1,177 lines): **mine it for test fixtures.** It encodes how small local
  models actually malform JSON. Every distinct repair becomes a case in the parser test suite.
- The emergent spine *design*: deed → (deterministic rules; model only for ambiguous) → legend +
  multi-axis standing → simulator spends *finite* faction resources → narrator shows it.
- Promise ledger semantics (claimed-vs-bound space, buildable archetypes, false-binding lessons).
- Persistence + replay design — especially replay carrying materialized-content apply points so
  recorded runs reproduce without the model.
- The lush content *as data*: `content/lore/*.md` gated lore cards, ~130 props as spell anchors,
  the 107-spell intent-tagged eval corpus with scoring.

**Change (keep the idea, change the shape):**

- The God-class engine → composed systems (Phase 0).
- Generation that mixes deterministic carving with LLM flavor → split structure-roll from naming.
- Eight near-identical provider stacks → one structured-call harness with purpose labels.
- Item model accreted across ~7 modules with legacy mirrors → keep Sorcerer's unified component
  model; defer identification/palettes until actually needed.

**Drop (do not carry over):**

- The daily-clock world tick (05:00 heartbeat) → pump points instead.
- `fallbacks.py`, the regex spell parser → it is the forbidden "hidden second spell engine." The
  mock provider covers offline; keep only parser-level repair.
- Pygame/SDL2 rendering, scenes, portraits/SDXL, `.env` write-back, the LLM trade provider, and
  all legacy compatibility lanes.

---

# Phase 0 - Engine Refactor + Perception (Prerequisite)

**Goal:** pay down the God-class before it rots, and stand up the perception model the spine
needs. Behavior-preserving. No new player-facing features in 0.0/0.1.

### 0.0 - Lay down characterization tests FIRST

The test project currently has **one file** (`tests/Sorcerer.Tests/GameSessionTests.cs`). You
cannot safely refactor a 2,433-line class behind one test. Before touching the engine:

- Add **golden/characterization tests** that drive `GameSession` through scripted command
  sequences against `TestScenarios.ImperialEncounter()` with `--provider mock`, asserting on
  `ActionResult` fields (`success`, `consumedTurn`, `turnBefore/After`, `messages`) and a
  serialized `GameView`/`AgentObservation` snapshot. Use judgment about snapshot granularity:
  checked-in golden JSON is fine where the view is stable, while targeted assertions are better
  where full snapshots would become churn.
- Cover, at minimum: movement/collision, bump-combat to death, pickup/drop/use/equip/focus,
  read/examine/open, talk, possess, each operation family via the mock resolver, status/terrain
  expiry, scheduled-event due, background-job pump, the prisoner-rescue consequence, and the
  technical-failure vs intentional-rejection turn contracts.
- Reuse the existing `EpisodeRunner` invariant checks as a long-run smoke test.

Acceptance: `dotnet test` is green and meaningfully exercises the engine surface. These tests are
the safety net for everything after.

### 0.1 - Extract systems (behavior-preserving)

Split `src/Sorcerer.Core/Engine/GameEngine.cs` into focused systems under
`src/Sorcerer.Core/Engine/Systems/`. `GameEngine` becomes a thin coordinator that owns `GameState`
and the systems and **keeps its current public method signatures** (so `GameSession` and
`WildMagicController` are untouched) — each public method delegates to a system.

Proposed seams (each operates on `GameState`, returns `StateDelta`s where it mutates):

| System | Absorbs from today's GameEngine |
|---|---|
| `MovementSystem` | `MoveControlled`, `MoveEntity`, `CanEnter`, collision, doors, teleport/push/pull mechanics |
| `CombatSystem` | `AttackEntity`, `DamageEntity`, `MarkDefeated`, death/corpse, loot |
| `AiSystem` | `RunActorTurns`, target selection, `IsHostile`, `IsUnableToAct` |
| `PerceptionSystem` | FOV/visibility/explored/witnesses (new — see 0.2) |
| `ItemSystem` | `Pickup`/`DropItem`/`UseItem`/`Equip`/`Unequip`/`Focus`/`Unfocus`, inventory keys |
| `InteractionSystem` | `Read`/`Examine`/`Open`/`Talk`, `RealizePromisesForEntity` |
| `TurnSystem` | `AdvanceTurn` orchestration: status/terrain expiry, scheduled events, background pump, **world-reaction pump (Phase 2+)** |
| `ViewBuilder` | `View`/`Observation`/`MagicContext`/card builders |
| `SpawnService` | `spawn_actor`/entity-id/serial, `BuildItemEntity` |

Rules: pure mechanical reads/writes only; **no system imports Godot or a provider**; systems may
hold a reference to `GameEngine`/`GameState` but not to each other's internals beyond declared
methods. Move method-by-method, running the 0.0 tests after each extraction. Do not change
behavior; if you find a bug, write a failing test, then fix it in a separate commit.

Acceptance: `GameEngine.cs` is a coordinator (target < ~400 lines as a smell threshold, not a hard
requirement); each system is independently readable; all 0.0 tests still green; no public API
change observed by `GameSession`; after the refactor, run at least one CLI playtest script by hand
with the mock provider and inspect the resulting observations for obvious drift.

### 0.2 - Perception / FOV subsystem

Today everything is perfect-information: `MagicContext` and `View` enumerate **all** entities, and
`MapTileCard.Visible/Explored` default to `true`. Build a real model in `PerceptionSystem`.

- **State:** add to `GameState` a soul-keyed explored-memory lane, e.g.
  `Dictionary<string, HashSet<GridPoint>> ExploredBySoulId`. Current visibility should be
  recomputed from the controlled body by `PerceptionSystem` / `ViewBuilder`; if cached, treat
  `HashSet<GridPoint> Visible` and `HashSet<EntityId> VisibleEntityIds` as transient/non-durable
  state that can be rebuilt after load.
- **Algorithm:** symmetric shadowcasting (or Bresenham LOS to start) using
  `PhysicalComponent.BlocksSight`. Add a `sightRadius` (default from a constant; later modulated by
  statuses such as a future `sight_shrouded`). `MapTileCard` already carries `BlocksSight`,
  `Visible`, `Explored` — populate them for real.
- **Renderer (Godot `Main.cs`) and CLI map:** render visible tiles bright, explored-but-not-visible
  dim, unknown hidden. Entities show only when in `VisibleEntityIds`.
- **Resolver context (`MagicContext`):** do not make player perception the resolver's hard
  knowledge boundary. It may include hidden or debug-only state when useful for coherent magic, but
  every entity/tile should carry a visibility relation such as `visible`, `explored`,
  `hidden_from_player`, or `debug_only`. Renderer views still hide hidden facts. The engine still
  validates references, operation legality, range/cost/curse constraints, and transactional state,
  but a valid target is not illegal merely because it is hidden from the player.
- **Witness hook (consumed by Phase 2):** expose
  `PerceptionSystem.WitnessesOf(GridPoint point)` → the set of *other* actors with LOS to a point.
  Deed capture uses it for both the caster/origin and the affected target/effect. Actors who see
  only the effect create an unattributed suspicion record; if they get LOS to the player within
  **20 turns**, the deed may become attributed to the player-soul. If they never see the player in
  that window, it remains suspicious but unattributed.

Acceptance: a wall hides what's behind it in both renderers; explored memory persists; an agent
can still request perfect debug state; resolver context can include hidden facts marked as hidden;
`WitnessesOf` returns the right actors across a wall test; suspicious-but-unattributed witnessing is
covered by a focused test.

---

# Phase 1 - Character & the Resolver Lens

**Spirit:** you are an unusually strong sorcerer, not a chosen one. Three stats shape your body,
your soul's potency, and how hard wild magic bites back. The most important job of stats is not
combat math — it is **shifting the anchors the resolver sends the model.** (See
[CHARACTER_AND_STATS.md](CHARACTER_AND_STATS.md).)

**Good news:** `ProfileComponent(PublicName, Appearance, Origin, MagicalSignature, Backstory)`
already exists on entities. This phase makes it real.

### Types & state

- Extend the character model with an explicit body/soul split. Prefer separate components so
  body-swap is natural:
  - `BodyStatsComponent(int Vigor)` lives on the body and drives HP, physical resilience, melee
    pressure, and carry capacity.
  - Add a soul/person state lane, e.g. `SoulLedger` + `SoulRecord`, keyed by `SoulComponent.SoulId`.
    It owns `SoulStatsComponent(int Attunement, int Composure)`, current/max mana, personal memory,
    exploration memory, reputation/legend keys, magical signature, and soul-facing backstory.
  - Body-facing profile fields such as public name and appearance follow the body; soul-facing
    fields such as magical signature and personal backstory follow the soul unless a later origin
    explicitly says otherwise.
- Add an `Origin` data type loaded from `content/origins/*.json`: split stat baselines, starting
  items, tradition tie, and **faction first-reactions** (seeds Phase 3). Implement a small initial
  canon roster in this sprint, using examples such as a bone-singer's apprentice, a deserter
  charter mage, a desert Parn, and a merfolk exile as guidance rather than a fixed required list.

### Engine / systems

- `ActorComponent` max HP and physical combat derive from body Vigor. Mana (current and max) and
  spell potency derive from the soul's Attunement and move with the soul during possession.
- **Resolver lens (the core mechanism)** — extend `GameEngine.MagicContext` /
  `MagicContextView` to carry an anchor block derived from the profile:
  - **Attunement → magnitude:** push suggested effect-magnitude bands up/down.
  - **Composure → volatility:** low = "wild magic answers more chaotically; backfires and costs
    more frequent and gorgeous"; high = "answers cleanly."
  - **Vigor → cost framing:** high lets the model lean on health/physical costs; low steers away.
  - **Signature → flavor lens:** a persistent idiom injected sparingly so it tints, not dominates.
  - These render into the prompt addendum the provider assembles (see
    `OllamaSpellProvider`/operation cards). A **deterministic fallback** (and the mock provider)
    must read the same profile so stats still bite offline.
- **Name-is-external rule:** the message log stays second person ("You"); `PublicName` surfaces
  only where *others* refer to the player (NPC dialogue in Phase 4, imperial warrants in Phase 3).
  After a body swap, others use the inhabited body's public name, while resolver potency and
  volatility follow the player-soul's Attunement and Composure.

### Commands & UX (CLI + GUI parity)

- CLI: a `character`/`sheet` command printing stats + identity; `--quickstart` default profile so
  agents never block; optional `--origin <id>` / flags to set fields. Add to `TextCommandParser`,
  route in `GameSession`, render in `Program.cs`.
- GUI: a character-creation entry (quick-start on Enter; customize to pick/roll origin, spend a
  small point pool, fill free-form fields) and a character panel in `Main.cs`.

### Tests

- Profile → `MagicContextView` anchor block (magnitude/volatility/cost/flavor) for low/mid/high
  stats; **mid changes nothing** (anchors unchanged).
- Offline/mock fallback honors stats deterministically.
- Body-swap carries the inhabited body's Vigor and external name, while preserving the
  player-soul's Attunement, Composure, reputation, and exploration memory.
- HP derives from body Vigor; mana derives from soul Attunement at spawn and after possession.

**Acceptance & payoff:** the same spell text resolves differently for a reckless low-Composure
caster vs. a controlled one; origin seeds how the world will first react (feeds Phase 3). CLI never
stalls on creation.

---

# Phase 2 - Deeds -> Legend -> Reputation

**Spirit:** what you do is recorded, gated by **who saw it**, and distilled into a legend the whole
world reads. Reputation is never one score. This is the deterministic backbone everything social
reads — it must work with **zero model calls.** (See [EMERGENT_WORLD.md](EMERGENT_WORLD.md),
[WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md).)

The lanes already exist in `GameState`: `DeedLedger` (`DeedRecord` has `ActorSoulId`, `Kind`,
`Magnitude`, `PlaceKey`, `Visibility`, `Witnesses`, `Tags`), `LegendLedger` (`LegendTag`),
`MemoryLedger`. Today they are written ad-hoc (e.g. prisoner rescue at `GameEngine.cs:1513`). This
phase replaces ad-hoc writes with a **general pipeline driven at pump points.**

### The pipeline (new `WorldReactionSystem`, run from `TurnSystem`)

1. **Deed capture.** Engine events emit pending deeds: kills/damage (`CombatSystem`), accepted
   casts (`WildMagicController` → pass effect families/severity through to a deed), theft,
   rescues, oaths/promises, property/terrain damage. A deed records the actor's **soul id**
   (`SoulComponent`, falling back to entity id), kind, magnitude, `PlaceKey` (region+local), and
   witness evidence from `PerceptionSystem.WitnessesOf(origin)` and
   `PerceptionSystem.WitnessesOf(target/effect)` at the moment it happens. For wild magic, record
   both the player's spell text / accepted resolution tags / effect families and the concrete
   validated `StateDelta`s. Legend rules use concrete deltas for mechanics and spell intent for
   flavor and ambiguity.
2. **Visibility and attribution classification.** secret (no witnesses) / suspicious
   (witnesses saw the effect or target but not the caster) / witnessed (some saw the actor) /
   public (many or a faction official saw) / mythic (reserved for legend-worthy magnitude). Store
   both what was seen and who the deed is currently attributed to. Suspicious deeds may become
   attributed if the witness gets LOS to the player within **20 turns**; they remain unattributed if
   that connection never happens.
3. **Pump application.** In `AdvanceTurn`/`TurnSystem`, at significant-deed / rest / zone-transition
   boundaries, apply unapplied deeds: a **declarative deed-rules table** (keyed by faction *role*,
   not literal id — see Phase 3) maps `(kind, tags, visibility, magnitude)` → multi-axis standing
   deltas + legend tags. Deterministic; the model is consulted **only** for ambiguous outcomes and
   always has a fallback (defer the LLM deed-interpreter until Phase 3+ — start fully deterministic).
4. **Legend distillation.** Accumulate weighted `LegendTag`s on the soul (e.g. `defiant`,
   `butcher`, `merciful`, `uncanny`). Keep two forms: bounded tags systems reason over, and a prose
   mirror written to `MemoryLedger`/canon for prompts (the engine never reads prose for outcomes).
5. **Reputation axes.** Derive multidimensional reputation — notoriety, fear, gratitude,
   legitimacy, uncanniness, imperial-threat — from deeds + legend. One deed lands on several axes.

**Soul-bound, not body-bound:** all of the above key on soul id, so body swap does not launder
reputation.

### Views & legibility (CLI + GUI)

- Extend `Standing()` and the journal to show legend tags and reputation axes (not raw deed rows).
- Emit a message when reputation visibly shifts ("Word of the marble-melting will travel.").
- Add legend/reputation to `LedgerSummary`/`DebugStateView` for agents.

### Tests

- Witness gating: a kill behind a wall is `secret` (no LOS witnesses) and shifts only those who
  know; an explosion seen without the caster is `suspicious` and unattributed; the same kill in the
  open is `public`.
- Determinism: identical seed+script → identical legend/reputation with `mock`.
- Soul-bound: possess another body, commit a deed, confirm legend attaches to the soul.
- Pipeline replaces the prisoner-rescue ad-hoc write (now flows through the rules table).

**Acceptance & payoff:** acting on the world produces a legible, witnessed reputation with no model
in the loop. Combat now carries social weight — this is the substrate Phase 3 spends and Phase 4
reads.

---

# Phase 3 - Factions & the Empire

**Spirit:** powers hold multidimensional standing and **finite resources**, so their reactions are
*expenditures*, not meters crossing a line. A crackdown is the Empire spending a patrol, not
"fear > 70." Finite resources are what keep escalation bounded and the game winnable. The Empire is
**genuinely not evil** — its menace is that it is reasonable.

`FactionLedger`/`FactionRecord` already carry `Standing` and `Resources` dicts and `AdjustStanding`.

### Engine / systems (extend `WorldReactionSystem`)

- **Faction definitions** loaded from `content/factions/*.json`: the Empire of Vigovia plus
  regional / rebel / tradition factions, each with role, multidimensional standing, and a finite
  resource pool. Keep the deed-rules table keyed by **role** (`empire_bloc`, `resistance`,
  `tradition`, `player_org`) so it generalizes across the per-run roster (Phase 5).
- **Reputation → standing** at pump points (reuse the Phase 2 deed application).
- **Reaction as expenditure:** a backlash step spends resources to *act* — patrols, informants,
  warrants, bounties — realized as `ScheduledEventLedger` entries + spawns. An overspent faction
  goes quiet; pressure **ebbs and flows** as resources regenerate slowly. Start with the minimal
  regeneration rule that proves the seam (for example, a small deterministic trickle at reaction
  pump points); defer a fuller resource-economy decision until later playtesting.
- **Empire heat & the long arc:** imperial-threat reputation raises heat; imperial defenses are a
  spendable pool that can shape whether the emperor is reachable. Wire the *seam* now (a heat value
  + a defenses pool + a reachability check), but keep the eventual access rules thin until Phase 8.
- **Hostility from factions:** replace `AiSystem.IsHostile`'s hard-coded check with a faction
  hostility lookup (a `FACTION_HOSTILITIES`-style table by role), so charmed/allied conversions and
  faction standing actually change who attacks whom. Personal bonds can override faction hostility
  once Phase 4 exists, so an enemy who personally trusts or loves the player can refuse to attack,
  and an allied faction member who personally betrays the player can become hostile.

### Legibility (narrator hook, still deterministic-safe)

- The **Censorate clerk's escalating memos** and a **wanted poster bearing your legend** — emit as
  messages/canon at pump points. The deterministic version writes a templated line; the model (when
  present) may voice it. Never required for the skeleton.

### Views (CLI + GUI)

- `Standing()` shows role, standing axes, pressure/mood, and player rank per faction. Exact
  resource numbers stay out of player-facing output.
- Add bounty/warrant state to the journal and raw faction resources to debug views.

### Tests

- Expenditure: a crackdown depletes the resource pool; a depleted faction cannot act until it
  minimally regenerates at the chosen pump point; heat escalates then ebbs.
- Role-keyed rules generalize (a deed against any `empire_bloc` faction behaves consistently).
- Charm/standing flips `IsHostile` so AI retargets.
- Soul-bound heat survives body swap.

**Acceptance & payoff:** the world now *pushes back* with bounded, legible pressure, and the
long-arc gate exists. legend → standing → recruitment/hostility/heat is closed.

---

# Phase 4 - Dialogue, Bonds, Followers

**Spirit:** this is where emergent *stories* first appear — the gift that becomes a secret that
becomes a place; the mercy that makes you a monster. Build general primitives, not a party system.
The math stays invisible: it must read as relationships, not stat bars.

`BondLedger`/`BondRecord` (`Loyalty`, `Fear`, `Admiration`, `Resentment`, `Posture`) and
`MemoryComponent` already exist. `Talk` is currently a canned line (`GameEngine.cs:457`).

### The three orthogonal layers (never conflate)

1. **Combat allegiance** (`ActorComponent.Faction`) — who fights whom.
2. **Organization membership** (`FactionComponent.Roles` / a new org id) — who belongs to what.
3. **Personal bond to the player** (`BondLedger`, keyed by soul) — how an individual feels. This
   layer can override faction hostility in AI targeting and recruitment decisions.

Followers today are "same-faction actors" (`Followers()` at `:751`) — replace with bond-driven
membership.

### Dialogue (organic text, engine-owned mechanics)

- Free-form dialogue through `GameSession`: the player can type organic speech or intent, and a
  dialogue resolver (purpose-labeled on the shared provider harness) parses it into structured
  dialogue intent and proposed outcomes. The model may interpret meaning and voice the NPC, but the
  engine validates the proposal against allowed mechanics, relationship state, inventory,
  visibility, faction context, and turn-cost rules before applying anything. Malformed dialogue
  resolver output is a technical failure and must not mutate state.
- The deterministic skeleton still stands: mock dialogue resolves common intents such as greet,
  ask, gift, recruit, threaten, promise, and disclose with simple rule-driven fallbacks. Do not
  build a menu-only conversation system as the design target; menus can expose shortcuts, but the
  main feel should be organic, responsive, and free.
- Dialogue context includes the NPC's `MemoryComponent`, the player's legend (Phase 2), and the
  current faction standing (Phase 3) — so NPCs greet you by reputation.

### Bonds, gifts, followers (deterministic core)

- **Gifts/leverage:** giving items (especially imbued/trait-bearing ones) shifts bonds; deeds and
  legend color every bond check (`drift_bond` = legend × traits × memory, deterministic).
- **Followers:** recruit at bond thresholds; a follower acts through the **existing actor-agnostic
  turn seam** (`AiSystem` + `ControllerKind.Ai` + allied faction). No player-only path.
- **Devotion / drift / departure / betrayal:** emerge from thresholds + durable deed marks written
  to the NPC's memory; the model voices the moment, consequences are written back as
  traits/notes that color future behavior. Keep thresholds and math out of the UI.
- **Secrets & commitments:** a confided secret in dialogue binds as a **promise** (reuse the
  promise ledger + binding), so "her brother is held at a checkpoint two valleys east" becomes a
  realized place when Phase 5 generates it.

### Commands & UX (CLI + GUI parity)

- CLI: extend `talk`; add `give <item> to <target>` and `recruit`/bond inspection. Defer
  `found <org>` until bonds/followers are stable.
- GUI: a talk mode and a bond/followers panel in `Main.cs`.

### Tests

- The three layers move independently (an ally can resent you; an enemy can admire you).
- Gift and deed effects on bond values are deterministic.
- Threshold crossing recruits / triggers a departure; a betrayal writes a durable mark.
- A confided secret binds as a promise visible to the engine, mysterious in the journal.
- Malformed dialogue resolver output causes no mutation; accepted dialogue proposals apply only
  through validated engine operations.

**Acceptance & payoff:** items/traits → gifts → bonds → confided secrets → bound promises →
delivered futures. The recruit-a-stranger and earned-betrayal vignettes are now possible from
general systems with no script.

---

# Cross-Cutting: Resolver Hardening For Small Local Models

Run this thread **alongside** every phase (the bar is a modest Ollama model):

- **Mine `WildMagic/wildmagic/resolution_parsing.py`** for every distinct malformation it repairs
  (singular vs plural effects/costs, nested `outcome`/`details`, element/flavor-status aliases,
  point/tile shorthand, array-valued traits, string/numeric costs, stray commentary after JSON) and
  add each as a case in a `SpellResolutionJson` test suite. The Sorcerer `HANDOFF.md` "Spell Notes"
  list is the seed.
- **Port the 107-spell intent-tagged corpus** (common/creative/exploit) into `SpellEvalHarness`,
  and score not just JSON-validity but **specificity, consequence, and surprise**, plus hallucinated
  targets, exploit leakage, and latency. Magic must be memorable more often than merely valid.
- **Tighten operation cards** in `content/operations/` with fields, guidance, and few-shot examples
  aimed at a small model. Every new operation ships with a card and corpus prompts.
- Keep one **structured-call harness** (purpose-labeled: wild, dialogue, canon, deed) rather than
  cloning provider stacks. Do **not** add a regex fallback engine.

---

# Phase 5 - Procedural World: Regions, Traditions, A Fresh World Each Run

**Spirit:** each death rolls a wholly new geopolitics — a fresh map to read and master. The
**procedural roll fixes the structure deterministically; the model names and flavors it.** Every
rolled feature must imply at least one **tactical affordance** the player can act on, never just
lore. Weirdness scales with wild-magic saturation: imperial provinces feel surveyed, deep wild
places go dreamlike. (See [EMERGENT_WORLD.md](EMERGENT_WORLD.md),
[WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md), [WORLDBUILDING.md](WORLDBUILDING.md).)

This is the largest phase. Build it in the sub-steps below; **5.1 (a minimal multi-zone slice) is
shippable on its own** before the full geopolitical roll.

> **RESOLVED: a bounded region grid with lazy zone interiors.** Places live on a small overworld
> grid of regions; each region holds zones generated on first visit. Directional promises ("a blade
> waits north") and the wild-magic saturation gradient map onto grid coordinates directly, and this
> is the WildMagic-proven shape. No specific grid size is fixed yet; pick the smallest useful size
> when implementing the first multi-zone slice. (Considered and rejected for now: an abstract
> place-graph — cleaner but loses cheap directionality; a hybrid compass layout.)

### 5.0 - Split generation into a system (prerequisite)

Today the only map is the hand-built `TestScenarios.ImperialEncounter()`; `GameState` carries a
single live map and a `RegionId` string. Create `GenerationSystem` (Phase 0 already isolates
systems) with four deterministic sub-services, **structure roll strictly separated from model
flavor**: `WorldRoll` (geopolitics), `ZoneGen` (carve + populate one zone), `TownGen` (settlement
layout), `PromiseRealization` (sites). Use `DeterministicRng` and a `StableSeed(parts...)` helper
so zones derive process-stable seeds (port WildMagic's `determinism.py` lesson: never seed from a
process-randomized hash). The model is reachable only through the structured-call harness with a
deterministic fallback name always present.

### 5.1 - Zone graph, travel, and generation (the shippable slice)

- **State:** add a `ZoneSnapshot` record (tiles, blocking/terrain maps, entities, `RegionId`,
  generated flag, promise hooks, room profiles) and a `Dictionary<string, ZoneSnapshot> Zones` plus
  a `CurrentZoneId` on `GameState`. The live `GameState` map is the active zone; non-active zones
  live as snapshots. `ControlledEntity`, soul-bound ledgers, and exploration memory persist across
  transitions; per-zone visibility recomputes on arrival.
- **Travel:** a `travel`/`go <direction|region>` command. On transition: snapshot the current zone,
  load-or-generate the target, clear `SelectedTarget` (coordinates are now meaningless), place the
  player at the matching edge, and **fire the world-reaction + promise-realization pump** (zone
  transition is already a designated pump point).
- **ZoneGen:** deterministic room/corridor carving biased by a `RoomProfile` (type, era, condition,
  topics, tags, promise hooks, secret slots) that biases prop/enemy selection **without consuming
  gameplay RNG**. Region identity drives floor theme, enemy pool, and ambient tables.
- **RegionProfile** (extend the existing `Regions.cs`, ~57 lines today): voice + example outcome
  lines, enemy template pool, imperial presence, floor theme weights, ambient/wonder tables, and
  `WildnessBase` (effective wildness = base + depth). Consumed by generation, ambience, and the
  resolver lens (region voice spliced into the cast prompt addendum).

### 5.2 - World roll + towns + populations

- **WorldRoll:** a fixed core realm roster (Vigovia + the four old kingdoms Stalnaz/Brall/Ryolan/Vint
  + client/small realms per WORLDBUILDING) with a seeded rotation/reflection of a fixed political
  relationship graph, so each run differs but stays coherent (no impossible ownership). Fix realm
  ownership (conquered / defiant / client), ruler, dominant tradition, and imperial grip **up
  front**; leave zone interiors lazy. Produce a `RealmCard` per region used by views, generation,
  dialogue, and the resolver.
- **TownGen / populations:** settlements with NPCs, present factions (Phase 3 roster), props, and
  items seeded by region + tradition. One structured-call may produce a settlement spec
  (`town`-purpose), with a deterministic fallback layout. NPCs spawn with `FactionComponent`,
  `MemoryComponent`, and bonds initialized from origin first-reactions + legend.
- **Tradition as emphasis, not new magic (critical):** a region "leans" into a tradition by
  emphasizing **existing** engine systems — water terrain, sound-confusion statuses, name curses,
  bone summons, crystal divination — never a bespoke per-tradition handler. This is the phase's
  "more combinations than complexity" test.
- **Affordances:** every rolled feature exposes a `RegionAffordanceCard` (a safer region, a
  recruitable tradition, an exploitable conflict, terrain that suits a tradition's magic) surfaced
  in the atlas/inspection and the resolver context.

### 5.3 - Promise realization during generation

Close the **promises → worldgen → delivered futures** hand-off (the gift→secret→place vignette).

- Port WildMagic's site archetypes as data-driven `PromiseSiteArchetype` cards under
  `content/sites/`: `sacred_site`, `inhabited_site`, `hostile_site`, `memorial_site`, `hidden_site`,
  `creature_site`, `authority_site`, each defining structure style, footprint, props, optional NPC
  role, optional hostile count.
- When a zone generates and a bound/reserved promise targets it (`PromiseLedger` already stores
  claimed-vs-bound place + binding), realize the matching site, write `CanonRecord`s for site /
  keeper / fleshed detail, and mark the promise realized. Directional/terrain/wildcard hints reserve
  future zones with a default capacity (~2 realizations per zone; directional overflow spills
  outward).

### Views (CLI + GUI)

- `atlas`/`world` command + a `WorldView`/`AtlasCard`: realms, ownership, rulers, traditions,
  imperial grip, and where you are. GUI gets an atlas panel; `travel` is a button/keys.
- `RegionCard` + affordances in inspection and `MagicContext`.

### Tests

- World-roll determinism: same seed → identical geopolitics; different seed → coherent variety (no
  impossible ownership graph).
- Zone snapshot round-trips: leave and return; entities/terrain/explored preserved; soul ledgers
  intact across transitions.
- Every region exposes at least one affordance card.
- A bound promise realizes as a site when its reserved zone generates; canon written; journal
  updates; a directional promise reserves a zone in the right direction.
- Tradition emphasis is implemented entirely via existing operations (assert no new per-tradition
  effect type was added).

**Acceptance & payoff:** a fresh, coherent world each run; promises become real places; regions
read and play differently. This unlocks Phases 6-8 having somewhere to happen.

---

# Phase 6 - Magic & Item Depth

**Spirit:** "every object is potential grammar," and a spell's consequences should be able to
**chain** — a status that spreads, a ward that waits, a curse that costs you later. Depth comes from
**general primitives that compose**, not new bespoke handlers. (See
[SEMANTIC_EFFECTS.md](SEMANTIC_EFFECTS.md), [COSTS_REAGENTS_AND_TREASURED_ITEMS.md](COSTS_REAGENTS_AND_TREASURED_ITEMS.md).)

### 6.1 - Conditions, triggers, auras (the big general unlock)

- **Status traits beyond block/restrain** (extend `StatusRegistry`): damage-over-time, regeneration,
  `sight_shrouded` (reduces FOV radius via `PerceptionSystem`), fear (AI flees), charm (AI
  allegiance flip), vulnerability/resistance. Traits are data on the status definition, so flavor
  aliases (e.g. "crystallized", "time-locked") inherit a shared mechanic.
- **A general trigger/predicate system:** add a `(when, then)` model — a `TriggerComponent` or
  `TriggerLedger` evaluated by the appropriate deterministic cadence. Distinguish **turn-tick
  systems** (statuses, auras, delayed effects, terrain reactions that need tactical immediacy) from
  **world-reaction pump systems** (deeds, factions, promises, narration). Port WildMagic's
  `conditions.py` predicate vocabulary as a pure evaluator (`hp_below`, `on_terrain`,
  `count_visible`, `step_multiple`, `same_spell_streak`, ...). **Auras** are triggers that apply a
  status to entities in radius each tick; **delayed effects** are triggers on a turn count;
  **wards** are triggers on entry/death events. One mechanism, many spells.
- **New operation surface** (each ships with an operation card + corpus prompts): prefer the most
  general reusable operation shape, likely a canonical `createTrigger` with aliases/templates such
  as `ward`, `aura`, `delayIncoming`, and `createPersistentEffect` where they are just specialized
  trigger forms. Add separate canonical operations only when validation/application genuinely
  differs.
- **Terrain × status reactions** (deterministic rules in a `TerrainReactionSystem` on the movement /
  turn pump): fire + water → mist, water extinguishes burning, vines snare entrants, slick ice
  slides movement, frost freezes water-soaked entities, fire cauterizes bleeding. These make the
  battlefield itself reactive without new spell handlers.

### 6.2 - Curses as durable, loaded conditions

- Deepen `addCurse`: known **mechanical** curse templates (Close / Far / Narrow / Straight-Path /
  Anchored) that enforce limits on accepted resolutions (range / area radius / line of sight /
  forbidden effect family) plus **semantic** curses that are prompt context only. Validate accepted
  resolutions against active mechanical curse limits inside the cast pipeline **before** apply
  (a new pre-apply check in `WildMagicController` / a curse service).
- Curse clearing tied to a deed or objective (e.g. a number of qualifying kills, or a promise),
  not a free dismiss.

### 6.3 - Item depth (defer identification)

- **Item catalog** (`ItemCatalog`, ~40 lines today → expand): authoritative `ItemDefinition`
  metadata unifying market value and reagent value, tags, material, and a spell-bias hint.
- **Curio generator:** a small compositional `GeneratedCurio` (name / value / material / tags /
  description) for loot, rewards, and trader wares — model-free, cheap lushness. Port the idea from
  WildMagic's `item_generation.py`.
- **Reagents/fuel:** surface unprotected inventory + value/material/tags as spell fuel in the
  resolver context (treasured items already protected by cost validation).
- `DECISION: item identification.` The roadmap defers identification, palettes, and ability-cards
  until a design need appears. This plan **defers them** — build catalog + curios only. Revisit if
  "unidentified loot" becomes a desired play loop.

### 6.4 - Trade (engine-side, no LLM)

- Per AGENTS.md, **no model in trade intent.** Explicit commands + engine rules: `wares`/`browse`,
  `buy`/`sell` against a merchant entity, value from the catalog, gold as a currency item. CLI + GUI
  parity.

### Tests

- Status traits: DoT ticks and expires; fear makes AI flee; charm flips allegiance; `sight_shrouded`
  shrinks FOV.
- Trigger fires on its predicate; aura applies in radius each tick; ward fires on entry; delayed
  effect fires on schedule.
- Terrain reactions (water extinguishes fire; ice slides; vines snare) are deterministic.
- A mechanical curse rejects an over-range / forbidden-family cast in the pipeline; semantic curse
  only colors context.
- Curio generation and trade values are deterministic; treasured items are never auto-consumed.

**Acceptance & payoff:** spells gain chainable, surprising consequences; the environment and the
player's objects become usable grammar; curses are interesting costs rather than flavor text.

---

# Phase 7 - Narrator & Lush Content

**Spirit:** this is where "shockingly lush" is earned and where emergence becomes **perceptible**.
The model interprets and narrates; it never simulates. Every narrated thing has a deterministic
template fallback so the skeleton stands with the provider cold. (See
[LLM_AND_BACKGROUND_JOBS.md](LLM_AND_BACKGROUND_JOBS.md), [AESTHETICS_AND_TONE.md](AESTHETICS_AND_TONE.md).)

### 7.1 - Lore as data (canon the model can reach)

- Port `content/lore/*.md` from WildMagic: gated lore cards with `## Level N` sections, access
  tags/thresholds, router triggers, and injected bodies. Build a `LoreCardLoader` (parse Markdown +
  fenced metadata) and a pure `LoreRouter` (relevance selection by triggers/subjects/access; no
  provider call). Inject relevant, **access-gated** lore into resolver, dialogue, and canon context.
  Draft sections never enter the live load path.
- This plus WORLDBUILDING is the canon spine the model's voice draws on.

### 7.2 - Texture & background enrichment (via the existing job lane)

- **Model-free texture grammars** (port the idea from `texture.py`): instant, deterministic naming
  for bulk content — placed books/props get concrete names, hidden shelf cards, and durable
  `subjects` (lore-router keys), about half pulling authored canon and half staying local/odd.
- **Provider-backed background generation:** replace the current deterministic placeholder jobs
  (`canon_detail`, `entity_detail` already queue/pump/apply) with real generation **behind the same
  queue/apply boundary** — resource-aware, cancellable, inspectable. Durable output enters state
  only at the apply pump point.

### 7.3 - The narrator voice (legibility budget)

- **Rumors that greet you by reputation** on zone entry (reads legend + standing from Phases 2-3);
  the **Censorate clerk's escalating memos** (mature the Phase 3 hook); situation reports; the
  run-closing **chronicle** (consumed in Phase 8). Consequence-bearing detail: a wanted poster, a
  shrine raised to what you did — each writes a `CanonRecord` and may spawn a prop entity.
- All narration is read-only on mechanics; the deterministic template is the fallback.

### 7.4 - Provider harness maturity

- Give the single structured-call harness **purpose routing** (urgent/foreground vs background) with
  separate model/host settings in `LlmConfiguration` (port WildMagic's purpose-routing idea, not its
  `.env` mechanism), `MaxConcurrentBackgroundJobs`, disable flags, and per-purpose audit logs.

### Tests

- Lore router selects the right cards by trigger/subject; gated sections never leak above the
  knower's access; draft sections excluded.
- Narration disabled (or provider cold) → skeleton intact, templated fallbacks used.
- Background generation respects concurrency + disable flags and applies only at pump points.
- A zone-entry rumor reflects current legend deterministically (templated form under mock).

**Acceptance & payoff:** the world reads as lush and reactive; reputation is mirrored back to the
player; richness arrives without stealing the turn loop.

---

# Phase 8 - Persistence, Replay, Chronicle, Win Condition

**Spirit:** runs are long, and each death is a real ending whose reward is a wholly new world. No
meta-progression; cross-run traces commemorate, never empower. (See
[PROMISES_AND_PROPHECY.md](PROMISES_AND_PROPHECY.md), [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md).)

### 8.1 - Persistence (save/load)

- **RESOLVED: full mid-run quicksave/resume**, **System.Text.Json with a versioned envelope**
  (`{ schemaVersion, savedAt, state }`) and a migration hook from v1, kept in a dedicated
  persistence layer **separate from the lossy view layer** (WildMagic kept `persistence.py` distinct
  from `state_view.py` — same discipline). A run must serialize/restore at any point. Serialize all
  ledgers, zone snapshots, RNG cursor, entity serial, controlled-entity/soul, and drained background
  results. Replays live under `runs/<id>/`.
- **Implication for earlier phases:** because resume must work mid-run, every lane added in Phases
  1-7 must stay plain and round-trippable as it lands (see the "serialization rot" risk). Don't
  introduce non-serializable state (open handles, delegates, live tasks) into `GameState`.
- CLI: `save <path>` / `load <path>`. GUI: save/load menu entries.

### 8.2 - Replay (the regression backbone for model-touching systems)

- Re-feed recorded commands through a fresh `GameSession`; carry **materialized-content apply
  points** (canon, generated zone specs, deed-interpreter verdicts) on the record so replay
  reproduces **without calling the model.** Sorcerer already has `ReplayRecords` stubs + transcript
  JSONL — build the re-feed runner and an optional final-state assertion.
- This is how the world-reaction, worldgen, and narrator systems stay regression-safe despite
  involving the model.

### 8.3 - Emperor and win condition

- **RESOLVED: add the emperor now as a killable character so the game is technically winnable.**
  The elaborate systems for reaching him — court infiltration, layered defenses, late-game
  encounter texture, and complicated capital access — can stay thin or deferred, but the emperor
  must exist as an entity, can be killed through ordinary engine rules, and killing him ends the run
  in victory.
- Keep the Phase 3 gate seam useful but modest: track imperial defenses / capital reachability and
  surface locked/reachable state in standing/atlas/debug views. For this build, the seam may simply
  decide whether the emperor's zone/encounter is reachable; do not build a large bespoke finale.
- **Player death rolls a fresh world** (Phase 5) and carries no power forward.

### 8.4 - Chronicle + cross-run traces (commemorate, not empower)

- A run-closing **chronicle** (narrator, Phase 7) distilling legend + key deeds. **Grave markers /
  memorial records** persisted across runs as inert content the next world may surface (a grave you
  can find), never as inherited power.

### 8.5 - Long-run validation

- Extend `EpisodeRunner`: long deterministic episodes with invariant checks across the new lanes —
  reputation stays bounded, faction pressure ebbs and flows, promises realize or expire, no orphaned
  zones, and save → load → save round-trips to byte-identical state.

### Tests

- Round-trip save/load equality (serialize → deserialize → serialize is identical).
- Replay determinism for runs that touched the model (apply points reproduce content offline).
- The emperor exists as a normal entity in a reachable capital/emperor zone; killing him through
  ordinary engine rules triggers victory. Player death produces a new seed/world.
- A grave marker persists across runs and can appear in a later world.
- Long-episode invariants hold over a many-hundred-turn run.

**Acceptance & payoff:** the full long arc is playable end to end in a minimal form; runs can be
won, lost, and renewed; the world remembers past runs without empowering them.

---

# Definition Of Done (every phase)

- [ ] New capability reachable from **both** CLI and Godot GUI, added in the same change.
- [ ] Deterministic skeleton intact: full behavior with `--provider mock`, no model required.
- [ ] The model never mutates state outside the validated, transactional, audited path.
- [ ] World-reaction/social consequence runs at a **pump point**, never from a background mutator;
      tactical status/terrain/trigger ticks run only through explicit engine turn-tick systems.
- [ ] New player-facing state exposed through a read-only view (`GameView`/`AgentObservation`).
- [ ] Tests added for the new contract (turn consumption, determinism, soul-binding where relevant,
      rollback, CLI parity); `dotnet test` green.
- [ ] Touched the predictable set: operation/system metadata, validator, applier, resolver card,
      view, CLI+GUI, tests, agent playtest script.
- [ ] Durable docs updated in the same change (this plan, the roadmap, and the relevant subsystem
      doc).

# Risk Register

- **Re-monolithing.** New phases will be tempted to dump logic back into `GameEngine`. Keep it a
  coordinator; new logic goes in systems.
- **Legibility debt.** Emergence the player can't perceive reads as randomness. Budget UI/message
  work in every phase, not at the end.
- **Determinism leaks.** Any model call that affects mechanics breaks replay. The model interprets
  and narrates; rules decide. Audit every new provider call against this.
- **GUI/CLI drift.** The fastest way to violate the prime contract. Add both paths together.
- **Boring magic.** The corpus + scoring is the guardrail; do not let the operation set grow
  without growing the evals.
- **Generation tangle (Phase 5).** Keep the deterministic structure roll strictly separate from
  model naming/flavor; WildMagic's 3,815-line `generation.py` that fused them is the warning. Every
  rolled feature must expose a tactical affordance, not just lore.
- **Serialization rot (toward Phase 8).** Each new lane should stay serialization-friendly as it
  lands. Deferring *all* persistence thought to Phase 8 risks a painful retrofit across many lanes;
  keep records plain and round-trippable even before save/load exists.
- **Tradition special-casing (Phase 5).** A region's tradition must lean on existing operations
  (water terrain, name curses, bone summons), never a per-tradition handler — or the world grows
  by content instead of by combination.
