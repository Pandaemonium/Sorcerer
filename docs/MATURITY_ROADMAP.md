# Maturity Roadmap

This roadmap charts the path from the current first-playable slice to the breadth and depth of
the parent prototype, **Wild Magic** (`C:\Games\WildMagic`, ~48k lines across 74 modules), while
keeping Sorcerer's cleaner architecture. It is the detailed expansion of Milestones 5-9 in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) and the build sequence for the systems described
in [GRAND_VISION.md](GRAND_VISION.md). When this roadmap and that milestone list disagree on
sequencing, this roadmap wins; the vision in GRAND_VISION always wins on intent.

For the executable, file-level handoff plan (Phases 0-8 in implementation detail, including the
engine refactor, perception, world reaction, procedural world, and persistence/win work), see
[BUILD_PLAN.md](BUILD_PLAN.md). This roadmap is the strategic "why"; BUILD_PLAN is the tactical
"how."

## The Target, And The Anti-Port Principle

Wild Magic is mature: procedural worldgen, regions and towns, factions with finite-resource
pressure, deeds and legend, bonds and followers, talk-to-anyone dialogue, trade, quests, curses
and auras, item identification and ability cards, lore cards, a narrator, persistence, and a deep
autoplay harness. That is the maturity bar.

**We do not port it.** Porting reproduces 48k lines of tangle. Instead we hit the same richness
with far less code by building **the smallest general system that unlocks ten weird situations**,
and letting systems hand off to each other (see the interaction map in GRAND_VISION). The measure
of any phase below is not "does it match a Wild Magic module" but **"does it create more
combinations than it adds complexity."**

The architecture already gives us a head start the prototype never had:

> Every world-reaction state lane is already reserved in `GameState`: `PromiseLedger`,
> `DeedLedger`, `FactionLedger`, `LegendLedger`, `MemoryLedger`, `CanonLedger`, `BondLedger`,
> `ScheduledEventLedger`, `TriggerLedger`, `BackgroundJobs`.

Many began as records-only lanes; the sprint is progressively giving them deterministic readers and
writers at pump points. Most of this roadmap is **filling in rule-systems over an existing spine**,
not inventing new state shapes. That is why it can stay elegant.

## Where We Are

Solid and shipping:

- One `GameSession` backend behind both the Godot ASCII GUI and the JSON-first CLI.
- Wild magic through `OperationRegistry` / `IOperation`: parse -> repair -> normalize -> validate
  -> preflight -> transactional apply -> re-validate -> audit. Technical failures do not consume a
  turn; intentional rejections do.
- Costs (mana/health/item), protected inventory, post-cast cost framing.
- Engine actions: movement, collision, doors, bump-combat, items/equip/focus, read/examine,
  statuses and terrain with expiry, possession/body-swap seam, hostile actor turns.
- Promises with target/trigger binding and read/open/talk realization.
- Background-job lane (provider-backed candidate text with deterministic fallback), CLI eval (26/26 mock), episode
  runner, transcript logs, transcript replay, mock + Ollama providers.
- Save/load with a schema-v1 persistence envelope, materialized spell replay, a reachable
  killable emperor, run victory/defeat transitions, chronicles, and inert cross-run memorials.

Still thin or future work: live dialogue polish/evals, full geopolitics and towns, deeper quests,
richer background generation, item identification, late-game capital access, court infiltration,
and death-to-new-run UX polish.

## Build Discipline (applies to every phase)

- **Deterministic skeleton stands with zero model calls.** Ledgers hold truth; rules decide
  consequences; the model only reads ambiguous *meaning* and *narrates* reaction. Replay and tests
  must pass with the mock provider.
- **Engine owns truth.** Model output stays an untrusted proposal until normalized, validated,
  priced, applied transactionally, and audited.
- **World reaction runs at pump points,** not per turn and never from a background worker mutating
  state: after a significant deed, on rest, on zone transition. Tactical systems such as statuses,
  auras, terrain reactions, and delayed effects may tick through explicit engine turn systems. Same
  apply-boundary discipline as background jobs and pending casts.
- **GUI/CLI parity in the same change.** A capability in only one interface is a bug.
- **Each new mechanic touches the predictable set:** operation/system metadata, validator, applier,
  resolver card guidance, a read-only view field, CLI+GUI presentation, tests, and an agent
  playtest script.
- **Legibility is a feature.** Budget as much on *showing* a reaction as on computing it; emergence
  the player cannot perceive is indistinguishable from randomness.

## The Phases

Ordered by dependency, not by size. Each phase ends with a playable, more-emergent slice. The
"Unlocks" line names the GRAND_VISION interaction-map hand-off the phase lights up.

### Phase 0 - Engine Refactor And Perception Prerequisite

Status: first implementation complete. Tactical detail lives in BUILD_PLAN.md.

- Refactor the current large `GameEngine` into composed systems before adding more world-reaction
  features. The project is still early enough for a broad behavior-preserving extraction, but the
  public `GameSession` surface must stay stable.
- Add characterization tests first, then do a real CLI mock-provider playtest after the refactor in
  addition to `dotnet test`.
- Build real FOV/perception for renderers and deed witnessing. Explored map memory is **soul-bound**,
  while current visibility is computed from the controlled body.
- The resolver may receive broader-than-player knowledge when useful, but hidden facts must be
  annotated by visibility and must not leak into player renderers unless the engine makes them real.
  Wild magic is not limited by player knowledge; hidden targets are not invalid merely because they
  are hidden.
- Witnessing distinguishes attribution from suspicion: an NPC who sees the effect but not the caster
  becomes suspicious; they only attribute it to the player if they see the player within 20 turns.

**Unlocks:** real renderer perception, witness-gated deeds, and a smaller engine ready for the
world-reaction spine. **Tests:** characterization coverage, wall/FOV tests, soul-bound exploration
memory, suspicious-but-unattributed witnessing, CLI playtest transcript after extraction.

### Phase 1 - Character & the Resolver Lens

Status: first implementation complete. Small, high-leverage, deterministic, and feeds faction
first-reactions.

- Character state splits cleanly across body and soul:
  - **Vigor** belongs to the body and drives HP, physical resilience, melee pressure, and carry
    capacity.
  - **Attunement** and **Composure** belong to the soul and drive mana/potency plus wild-magic
    volatility and cost/backfire framing.
  - Mana belongs to the soul; HP belongs to the body.
  - Public name and appearance are body-facing; magical signature and personal backstory are
    soul-facing unless a later origin explicitly says otherwise.
- Route the profile into resolver context as **anchor shaping**: Attunement -> magnitude band,
  Composure -> volatility/backfire framing, Vigor -> cost framing, signature -> flavor lens. A
  deterministic fallback reads the same profile so stats bite offline.
- **Origins** as data packages (stat baseline, starting items, tradition tie, faction
  first-reactions) keyed to WORLDBUILDING traditions. Implement a small real origin roster in this
  sprint rather than leaving origins as placeholders.
- Creation UX: CLI quick-start default that never blocks agents; GUI/CLI customize flow.
- Name-is-external rule: message log stays second person; the name surfaces only where others refer
  to the player.

**Unlocks:** stats shape every cast; origin seeds how the world first reacts (feeds Phase 3).
**Tests:** profile -> resolver anchors; offline fallback honors stats; body swap carries body Vigor,
HP, and external name while preserving soul mana, Attunement/Composure; name rendering rule.

### Phase 2 - Deeds -> Legend -> Reputation (the spine of world reaction)

*The deterministic backbone everything social reads. No model required.*

Status: first implementation complete. `WorldReactionSystem` captures accepted wild magic,
player attacks/kills, prisoner rescue, and body swap; classifies visibility through the shared
perception model; applies pending deeds at the turn pump; and writes soul-bound legend tags plus
multidimensional faction standing. Broader deed families, richer rumor display, and model-assisted
ambiguity remain future layers.

- **Witness model:** at action time, compute who-saw-it from FOV/proximity; classify visibility as
  secret / suspicious / witnessed / public / mythic. (`DeedRecord` already carries `Witnesses` and
  `Visibility`.) Suspicious means an NPC saw the effect or target but not the caster; it becomes
  attributed only if they get LOS to the player within 20 turns.
- **Deed interpreter:** map engine events (kills, casts, thefts, rescues, oaths, property damage)
  into `DeedRecord`s with tags + magnitude. Magic deeds should retain both player spell text /
  accepted resolution tags and concrete validated deltas. Deterministic rules first; the model only
  reads ambiguous intent near a threshold.
- **Pump points:** apply pending deeds on the next significant-deed / rest / zone-transition pump
  (reuse `ScheduledEventLedger` + turn advance).
- **Legend distillation:** deeds -> a few weighted `LegendTag`s on the **soul** (defiant, butcher,
  merciful, uncanny). Body swap does not launder reputation.
- **Multidimensional reputation** derived from deeds + legend: notoriety, fear, gratitude,
  legitimacy, uncanniness, imperial threat. One deed lands on several axes at once.
- **Legibility:** `standing` and `journal` expose legend/reputation; debug views summarize ledger
  counts; reputation-shift messages enter the shared action result stream.

**Unlocks:** act -> witnessed deed -> rumor seed -> legend -> reputation. Combat gains social
weight (who saw it, which faction was harmed, and whether the actor was identified). **Tests:**
witness gating; secret vs. suspicious vs. public divergence; soul-bound legend across body swap;
deterministic axis math.

### Phase 3 - Factions & the Empire (finite-resource pressure)

Status: first implementation complete. Faction definitions load from `content/factions`, empire-bloc
rules are role-keyed, witnessed deeds raise heat, heat spends finite resources into scheduled
patrol/warrant pressure, quiet pumps regenerate resources and cool heat, AI hostility reads the
faction ledger, and the first emperor-reachability seam reads the imperial defenses pool.

- Faction definitions (Empire of Vigovia + regional / rebel / tradition factions) holding
  multidimensional standing and **finite resources** (`FactionRecord` already has `Standing` and
  `Resources`).
- **Reaction as expenditure, not threshold:** a crackdown spends a patrol and an informant; an
  overspent faction goes quiet; pressure ebbs and flows. This is what keeps escalation bounded and
  the game winnable. Exact resource numbers are debug-visible only; player-facing views show
  pressure, mood, warrants, and consequences. Start with minimal pump-point regeneration and defer a
  fuller resource economy until playtesting demands it.
- Reputation -> faction standing deltas at pump points.
- **Empire heat:** rises with imperial-threat legend; spends responses (patrols, warrants,
  bounties) via scheduled events + spawns. Imperial defenses are a spendable pool that must be drawn
  down or bypassed before the emperor is easily reachable - the long-arc seam. The first winnable
  implementation can keep access thin as long as the emperor exists and can be killed.
- **Legibility (narrator hook):** the Censorate clerk's escalating memos; a wanted poster bearing
  your legend.

**Unlocks:** legend -> faction standing -> recruitment, hostility, and the Empire's rising heat.
**Tests:** expenditure depletes and recovers in debug state; heat escalates then ebbs; soul-bound
heat; bounty spawn determinism.

### Phase 4 - NPC Interiority: Dialogue, Bonds, Followers (the social game)

Status: first deterministic slice complete. The engine now supports organic-ish `talk` intent
parsing, `give`, `recruit`, qualitative `bonds`, bond-driven followers, personal-bond hostility
overrides, gift memories, and post-dialogue claim extraction that can bind promises. Deeper
model-voiced dialogue remains later; the current layer is the deterministic skeleton it will have
to validate through.

- **Talk-to-anyone:** organic free-form dialogue through the same session. A dialogue resolver parses
  player speech into structured intent/proposed outcomes; the engine validates relationship,
  inventory, visibility, faction context, turn cost, and allowed mechanics before applying anything.
  The model can interpret and voice; it never directly mutates state.
- **Personal bond per NPC** (`BondRecord` exists: loyalty / fear / admiration / resentment /
  posture), kept as **three orthogonal layers never conflated:** combat allegiance, organization
  membership, personal bond to the player.
- **Gifts and leverage:** giving items (especially imbued ones) writes durable memory; later
  dialogue, deeds, and legend can move bonds through validated consequences.
- **Followers** recruit at bond thresholds and act through the existing actor-agnostic turn seam.
- Personal bond can override faction hostility in AI targeting and recruitment decisions.
- **Devotion / drift / departure / betrayal** emerge from thresholds + durable deed marks; the model
  voices the moment; consequences are written back as traits/notes that color all future behavior.
  The math stays invisible - it must read as relationships, not stat bars.
- **Secrets and commitments** confided in dialogue bind as promises.

**Unlocks:** items/traits -> gift memories -> dialogue/deeds -> bonds -> confided secrets -> bound promises -> delivered
futures. Makes the "gift -> secret -> place" and "mercy -> monster" vignettes possible. **Tests:**
bond layers independent; gift memories influencing later bond proposals; deed effects on bonds; threshold-driven recruit/betray; secret ->
promise binding.

### Phase 5 - Procedural World: Regions, Traditions, A Fresh World Each Run

*The big one; split a minimal multi-zone version first if needed.*

Current status: the minimal multi-zone slice exists. The engine can snapshot zones, travel between
coordinate zones, lazily generate region-flavored interiors, carry recruited followers, expose an
atlas/world card, feed region affordances into the magic resolver, and realize a bound travel/site
promise as a generated place. A minimal seeded `WorldRoll` also varies realm status, ruler, and
effective imperial grip by run seed, and generated zones seed one resident NPC from region/realm
profile. Full geopolitical ownership graphs, town layouts, population rosters, and rich
promise-site archetypes are still future Phase 5 work.

- **Zone graph** beyond the single chamber: regions with identity, imperial grip, tradition lean,
  and discoverable sources of strangeness. Zone transition is a reaction pump point.
  Start with the smallest useful bounded grid; no exact size is fixed by the roadmap.
- **Worldgen rolls geopolitics from the seed** (which realms are conquered/defiant, who rules, where
  traditions survive, imperial grip per province). Procedural rolls the structure deterministically;
  the model names and flavors it.
- **Every rolled feature implies a tactical affordance** - a safer region, a recruitable tradition,
  an exploitable conflict - not just lore.
- **Town / population generation:** NPCs, present factions, props, and items seeded by region +
  tradition.
- **Tradition as emphasis, not new magic:** each region leans on existing engine systems (water
  terrain, sound confusion, name curses, bone summons, crystal divination) rather than bespoke
  handlers.
- **Promise realization during generation:** promises bind to a region and come true when it is
  generated (the "checkpoint two valleys east" vignette).

**Unlocks:** region roll -> tactical affordances -> a different game to read each run; promises ->
worldgen -> futures the world actually delivers. **Tests:** seed determinism / replay safety; every
feature exposes an affordance; promise -> realized place; tradition emphasis uses only existing ops.

### Phase 6 - Magic & Item Depth (auras, conditions, curses, reagents, identification)

Current status: the first status-trait mechanics exist. Concealment aliases such as
`river_concealed` canonicalize to a `concealed` status, use registry default duration when omitted,
and suppress hostile AI notice beyond close range. Registry-driven turn ticks now make `burning` and
`poisoned` deal ongoing harm and `regenerating` / `mending` restore HP through the shared turn pump.
The minimal `createTrigger` slice is also live: delayed effects and radius/filter auras store
records in `TriggerLedger` and fire `addStatus`, `damage`, `heal`, or `message` effects through
`TriggerSystem` on the turn pump. The first terrain reaction slice is live too: water extinguishes
burning into temporary mist, fire ignites actors, and vines apply rooted status through the same
status registry. The minimal item-depth slice is live as well: item definitions carry spell-bias
hints, unprotected reagents enter resolver context, and generated zones create deterministic curios
whose metadata survives pickup through the shared item catalog. Mechanical curse templates now
reject violating accepted resolutions before mutation. Richer predicates, entry/death wards,
curse-clearing deeds, slick-ice movement, frost reactions, identification, and trade remain future
depth remain future work. Minimal explicit trade is live through `wares`, `buy`, and `sell` against
generated resident merchants.

- **Condition/effect depth on the existing registries:** persistent effects, auras, delayed effects,
  condition triggers, and status traits beyond block/restrain (visibility, fear, charm,
  vulnerability/resistance). Extends `StatusRegistry`. Distinguish tactical turn-tick systems from
  world-reaction pump systems so aura pulses, delayed effects, and terrain reactions can be
  immediate without becoming background world simulation.
- **Curses** as durable, narratively loaded conditions: deepen clearing, escalation, and more
  expressive template coverage.
- **Item depth:** deepen palettes, **identification** (mysterious until learned), ability cards,
  equipment depth, and broader generated loot/merchant inventories.
- **Trade / barter** through engine rules + explicit commands - **no LLM for trade intent**. The
  first merchant/wares/buy/sell slice exists; deepen inventory, pricing, and merchant variety later.
- **Semantic effects matured:** traits as dormant mechanics the resolver surfaces on demand, never
  on the critical path.

**Unlocks:** richer consequences and "every object is potential grammar." **Tests:** condition
lifecycle/expiry; curse persistence; identification states; trade values deterministic; trait
promotion is resolver-authored only.

### Phase 7 - The Narrator & the Lush Content Layer

*This is where "shockingly lush" is earned.*

- **Lore as data:** port Wild Magic's `content/lore/*.md` canon as content cards; add a lore router
  so the right canon reaches the resolver and narrator. Texture/flesh systems enrich room, book, and
  prop description through the **existing background-job lane**. First slice is live: Markdown lore
  cards, pure routing, resolver context injection, atlas lore, deterministic fixture texture, and
  routed-lore background detail fallbacks.
- **Narrator voice:** rumors that greet you by reputation, clerk memos, situation reports, the
  run-closing chronicle, consequence-bearing detail (a shrine raised to what you did). First slice:
  deterministic zone-entry rumors reflect current legend and standing after travel without changing
  mechanics.
- **Provider-backed background generation:** candidate prose now comes from the optional background
  provider or deterministic fallback behind the same queue/apply boundary. Continued maturity work:
  richer prompts, audits, cancellation, and developer queue inspection.
- **Legibility layer:** rumor lines on zone entry, NPCs greeting by legend, named voices, standing
  readouts.

**Unlocks:** the world reads as lush and reactive; emergence becomes perceptible. **Tests:**
narration never mutates state; background generation stays within resource limits and applies only
at pump points; skeleton intact with generation disabled.

### Phase 8 - Persistence, Replay, Chronicle, and the Long Run

Status: first implementation complete. `GameSaveService` serializes authoritative state through a
schema-v1 envelope, CLI/Godot share `save`/`load`, transcripts carry materialized spell JSON, and
`--replay` can re-feed recorded commands through a fresh session without model calls. The thin
world graph now reaches the Vigovian Capital, Emperor Odran is a normal killable actor, victory and
controlled-body defeat both close the run, completed runs write chronicles, and later runs can
surface those records as inert memorial props. Episode runs now check save/load byte stability and
cross-lane invariants.

- **Save/load:** deepen migration/version tooling, compress/organize run folders, and keep every new
  state lane serializable.
- **Replay:** keep expanding materialized apply points as new provider-backed systems land; spell
  JSON, generated dialogue, claim extraction, and background text are already replay-fed.
- **Cross-run traces** that commemorate, not empower: grave markers, chronicles, memorial records,
  and other inert echoes.
- **Win condition wired:** the emperor exists as a killable character; deepen the late-game access
  systems later through imperial defenses, court infiltration, and capital texture rather than a
  bespoke finale.
- **Long-run validation:** keep growing deterministic, inspectable autoplay episodes with invariant
  checks across new lanes.

**Unlocks:** the full long arc and many-runs loop. **Tests:** round-trip save/load equality;
replay determinism; win/death transitions; long-episode invariants.

## Cross-Cutting Threads (not phases, always running)

- **Resolver quality** stays the first-order feature: as the operation set grows, keep evals
  measuring specificity, consequence, and surprise - not just JSON validity. Decide keyword vs.
  embedding capability routing when the operation count forces it.
- **Agent playtesting** deepens alongside each phase: richer episode policies, compact observation
  mode for long runs, replay-ready transcripts.
- **Documentation rule:** when a phase changes a durable contract, update the relevant doc in the
  same change.

## Suggested Sequencing

Phase 0 first: refactor the engine and install perception before adding the social spine. Then
Phases 1 and 2: both are small, deterministic, low-risk, and unblock everything social. Phase 3
turns the spine into stakes. Phase 4 is where the game becomes a social sandbox. Phase 5 is the
largest and can ship in a minimal multi-zone form before full geopolitics. Phases 6-8 deepen and
durabilize. Persistence (8) can be pulled earlier if save-format churn becomes painful - the longer
the lanes grow unserialized, the more expensive that retrofit is.

The non-negotiable order: **the deterministic spine (1-3) precedes the model-heavy lushness (7),**
because the skeleton must stand with zero model calls before we make it beautiful.
