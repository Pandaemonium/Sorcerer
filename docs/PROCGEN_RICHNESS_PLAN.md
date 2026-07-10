# Procgen Richness Plan — Props, People, Places, and One Shared Canon

Status: active implementation plan, written 2026-07-07. WS0 through WS3 plus the first
significant-interior extension were implemented and
verified 2026-07-09; WS4 now has one complete generated-journey slice and is the active workstream.
This is the tactical plan for the full Phase 5 (procedural world) and the data-driven half
of Phase 7 (lush content) from [MATURITY_ROADMAP.md](MATURITY_ROADMAP.md), sequenced for
the product goals in play: a large region-broken map with **multi-zone cities and towns**,
more quest-giving NPCs, and a world that reads as lush and *coherent* — on a 20-hour-run
arc, shipping on itch, with qwen3.5-CPU as the model floor. The touchstone is **Caves of
Qud**: a big strange world of biome regions, sparse wilderness, dense weird settlements,
and objects everywhere that make you want to try things — but with larger, multi-zone
settlements than Qud favors. Companion to [EMERGENT_WORLD.md](EMERGENT_WORLD.md),
[SEMANTIC_EFFECTS.md](SEMANTIC_EFFECTS.md),
[WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md), and
[GRAND_VISION.md](GRAND_VISION.md).

## Principles (non-negotiable for every workstream)

1. **Deterministic-first, model-enriched second.** Nothing on the generation path calls a
   model. Richness comes from seeded rolls over data tables; the existing background-job
   lane may *enrich* what generation made (detail text on examine), never create it. On the
   CPU model floor, a richer world built on more model calls makes the worst problem
   (latency) worse; a richer world built on tables makes the game feel faster.
2. **Data over code.** Today regions live in `RegionRegistry.CreateMinimal()` and resident
   names live in switch statements inside `GenerationSystem`. Every table this plan adds is
   content (`content/regions/*.json` etc.), loaded exactly like origins, factions,
   capability cards, and charter spells already are. Lushness should be editable without
   recompiling — that is also what makes it moddable, which CONTENT_AND_MODDING promises.
3. **Semantic content *is* an affordance.** Props do not need mechanical hooks to earn
   their place: a prop's name, material, condition, and tags flow into resolver and
   dialogue context, where the model can seize on them — and SEMANTIC_EFFECTS already
   defines how semantic traits crystallize into mechanics on demand. A rusted harpoon
   winch, a jar of pickled lightning, a prayer-flag line: each is an *idea generator* for
   what wild magic to try. So the bar for a prop row is **evocative specificity**, not a
   hook; volume and variety are the goal. Mechanical hooks (readables, claim seeds,
   reagents, charter teachings, promise anchors) are sprinkled on a *minority* of rows as
   seasoning. People stay hook-bearing: a resident carries a want, service, wares, or
   knowledge — never only a name.
4. **Seed-derived over serialized.** Anything derivable from `state.Seed` (the settlement
   graph, region tables' rolls) is recomputed, not saved. New save lanes are a last resort;
   zone snapshots already persist what the player actually touched.
5. **Legibility is the deliverable.** Generation the player cannot perceive is
   indistinguishable from randomness. Every workstream ends with the atlas, rumors,
   examine text, or dialogue *showing* what was made — and the playtest feel log, not the
   code, decides whether richness landed.
6. **Budgets bound the ceiling, not the texture.** Prop-dense zones may carry up to ~16
   props (they are inert: no AI turns, negligible turn cost); residents follow a density
   field with *no fixed per-zone count* — zero is a normal answer. Resolver and dialogue
   context now keep actors and hook-bearing objects in a protected full-detail lane while
   ordinary scenery uses a compact bounded lane, so a cluttered market cannot evict its
   hostiles or readable clues. WS6 measures save growth at scale before the map gets big.

## Where we start (measured, not guessed)

- Fourteen regions and thirteen traditions now load from the shipped/embedded content corpus;
  every rolled realm has a reachable, seed-jittered regional anchor.
- `WorldRoll` rolls realm status/ruler/grip, including the one-free-rival old-kingdom rule, and
  atlas, travel, resolver, dialogue, generation, and population all quote that shared roll.
- A generated zone now gets region terrain, one deterministic curio, a variable regional prop
  batch (including possible empty/dense zones and ensembles), and a region-authored population
  sample. Settlement centers produce crowds, shoulders thin out, and wilderness commonly has no
  people; every resident has a forged local name, archetype, want, and knowledge, with selected
  roles carrying wares or services.
- Playtest evidence (FEEL_LOG [21]): the capital is "at least 9 identical zones, each with
  exactly one imperial court functionary" — the flattest content in the game, in the seat
  of the Empire. That finding is this plan's acceptance bar: the same walk must read as a
  *place*.
- The quest plumbing (wants, services, claims, promises, realization kinds, bonds, trade)
  exists and cross-reads; generated archetypes now seed wants, knowledge, services, and stock.
  WS4 must turn those pressures into repeatable multi-step journeys.

## Workstreams

### WS0 — Regions become content, and the roster grows

**Status: implemented 2026-07-09.** Fourteen regions and thirteen traditions load from external
JSON with an embedded standalone fallback. Seed-jittered data anchors make every realm reachable;
the old-kingdom roll produces one rival and three conquered realms; border narration is shared by
CLI and GUI.

The foundation everything else keys off.

1. **`content/regions/*.json` loader.** New `RegionCatalog` (mirror `OriginCatalog`'s
   CandidateRoots/`LoadFrom` pattern) reading `RegionDefinition` plus the new generation
   tables below; `RegionRegistry.CreateMinimal()` becomes the fallback when no content
   loads, exactly like `OriginCatalog.DefaultOrigin`. Traditions load from the same files.
2. **Roster: at least one region per realm in `WorldRoll`,** written against the existing
   lore cards (`content/lore/`): empire core + capital, Hollowmere, Stalnaz mountains,
   Brall harbor, Ryolan, Parn desert, Threen, Ontria, Monteary, Vint, Gontark, Rentacosta,
   plus the Wild Border. Each region: terrain tags, floor terrain, voice summary, ambient
   lines, 1-3 affordance cards — the fields that already exist — authored in each realm's
   voice.
3. **Zone→region mapping grows up.** `RegionFor(zoneId)` currently bands the grid; give
   each realm a seeded rectangle/wedge of the zone grid (derived from `WorldRoll`) so
   travel crosses real borders. Border zones blend voice ("the reeds thin; marble posts
   begin").

**Files:** new `src/Sorcerer.Core/World/RegionCatalog.cs`, new `content/regions/`,
`GenerationSystem.RegionFor`, `WorldRoll`.
**Accept:** ≥ 13 regions load from content; `ContentCorpusIntegrityTests` verifies every
region names a real realm/tradition/faction and every `WorldRoll` realm has ≥ 1 region;
existing scenario zones unchanged; travel across a realm border produces a legible entry
line naming both sides.

### WS1 — Props: a huge, lush, semantic-first variety

**Status: implemented 2026-07-09.** Each region owns bases, materials, conditions, hooks, density,
and one or more ensemble definitions. Per-zone RNG is derived from world seed + zone id, ordinary
props use `spawn_fixture`, and readables/anchors are a minority. `MagicContextView` and dialogue
split full actor/hook cards from compact targetable scenery. Tests lock at least 25 names over a
ten-zone Hollowmere tour, ensemble/hook presence, determinism, inspection, and actor survival under
twenty nearby clutter props.

The Qud lesson: the world feels alive when nearly everything in it is *specific* — not "a
barrel" but "a brine barrel sweating salt rings," not "a shrine" but "a wasp-shrine whose
offerings hum." Props are the player's spell-idea generators; their job is to make hands
itch.

1. **Combinatorial prop grammar in region JSON.** Instead of a flat list, rows compose:
   `base × material × condition/quirk` (e.g., bases: loom, cairn, winch, cistern, drying
   rack, prayer-flag line, kiln, tally-post; materials/realm-tinted: whalebone, reed,
   marble, brass, salt-glass; quirks: cracked, votive, singing, confiscated, overgrown,
   mismatched, ancient). A few dozen rows per region yields thousands of distinct,
   deterministic props. Each resolves to: name, glyph, material (already a resolver
   input), tags, and a one-line description in the region's voice. **Semantic-only is a
   complete, shippable prop** (principle 3).
2. **Ensembles: props that arrive as scenes.** Weighted ensemble rows spawn coherent
   clusters — a cold campfire with bedrolls and a hanging pot; a roadside shrine with
   scattered offerings; an abandoned cart with spilled cargo; a gibbet with a censor's
   notice. Ensembles are what make a zone read as a *vignette* someone left behind, and
   they hand the model a whole scene to riff on.
3. **Density with texture, not a quota.** Prop counts per zone come from zone type plus
   seeded noise: a market square is cluttered (10-16), a salt flat may hold one lonely
   marker or nothing. Empty is allowed; empty next to cluttered is the rhythm.
4. **Hooks as seasoning (minority of rows):** `readable` text, `claimSeed`, `reagent`
   drops, rare `teaches_charter:<id>`, `CanAnchorMagic` — sprinkled by weight so the world
   stays mechanically alive without every object needing plumbing.
5. **Context routing so density never blinds the resolver.** Today `MagicContext` caps
   visible entities at 14 nearest-first — a cluttered square would evict the hostiles.
   Split the packet: actors + interacted-with + hook-bearing entities keep the full-card
   lane; remaining props enter as a compact one-line semantic list (`name — material —
   tags`, own cap ~10, nearest-first). The model still sees the crockery to riff on; it
   never loses the ward-captain behind it. Dialogue context gets the same compact prop
   line so NPCs can mention their surroundings.

**Files:** region JSON grammars, `GenerationSystem.GenerateZone` (replace single
`PropName`), `EngineViewBuilder` (context split), reuse the spawn-fixture consequence lane.
**Accept:** a 10-zone walk in one region encounters ≥ 25 *distinct* prop names with no
repeats reading as copies; at least one ensemble scene; `examine` gives a specific
one-liner on every prop; a cast in a cluttered zone still lists all living actors in
resolver context (test pins the split); same seed → identical props.

### WS2 — People: a population density field, not a quota

**Status: implemented 2026-07-09.** All fourteen regions now ship population grammars in
`content/regions/initial_populations.json`: density bands and centers, name forges, 44 culturally
specific archetypes, habitat weights, stats, wants, knowledge tiers, wares, and services.
`RegionPopulationGenerator` uses zone-derived RNG plus low-frequency noise and Poisson-style
sampling; center zones have a legible crowd floor, while wild transects are commonly empty with
rare hermits. `GenerationSystem.PopulateZone` deleted the one-resident switch path and spawns every
person, stock row, and service through `spawn_entity`, `offer_trade`, and `offer_service`. Focused
tests cover determinism, center/wild density contrast, packaged content, distinct names and
positions, wants/knowledge, and unchanged talk/give/bonds/trade/service behavior.

People spread the way people spread: clumped where life is (markets, gates, wells, roads),
absent for long stretches, with the occasional hermit exactly where no one should be. No
fixed per-zone count — **zero residents is a normal, common answer**, and that emptiness is
what makes the clusters feel alive.

1. **Density field.** Expected population per zone = settlement gravity (from WS3's graph:
   high in districts, shoulders along roads, falling off with distance) × region
   habitability × seeded low-frequency noise (so even wilderness has a warm valley and a
   dead one). Sample the actual count Poisson-style from the expectation: market district
   zones may hold 4-8, road shoulders 0-2, deep wilds 0 for stretches — then, rarely, a
   hermit, a poacher camp (an ensemble with a person in it), a lost patrol.
2. **Archetype tables in region JSON.** Rows: `{ archetypeId, roleTags, factionId, stat
   band, glyph, wares?, serviceChance, wantTemplates, knowledgeTier, voiceTags, weight,
   habitat }` — `habitat` weights where an archetype lands (market, gate, road, wild,
   shrine), so censors cluster at gates and reed-cutters at water. The capital roster
   alone fixes FEEL_LOG [21]: functionary, censor-clerk, squadron mage, petitioner,
   bone-broker — distributed, not one-per-zone.
3. **Name forge per realm.** Syllable/part pools in the region JSON (`Bralli: Aven, Nio,
   Sk-, -jarl…`; `Parn: sun-names…`) composing deterministic names; no two residents in
   nearby zones share a name (reroll against a recent-names ring).
4. **Interiority at spawn:** each resident rolls a `WantComponent` from its archetype's
   templates (realm-flavored fill-ins), a `KnowledgeComponent` tier, optional
   `MerchantComponent`/`ServiceComponent` — all through the existing spawn-entity
   consequence, so deeds/witnessing/dialogue all just work.

**Files:** region JSON, `GenerationSystem.PopulateZone`, `RegionPopulationGenerator`.
**Accept (met):** over a 20-zone transect from wilderness into a town: at least a third of wild
zones are empty of people, population visibly rises approaching the settlement, and the
market district holds a crowd; every resident carries a want, service, or wares;
`bonds`/`talk`/`give`/`wares` work against generated residents unchanged; deterministic
per seed.

### WS3 — Places: a large region-broken map with multi-zone cities

**Status: implemented 2026-07-09.** `WorldPlaceGraph` deterministically rolls one named 3×3
primary settlement per region, 0–2 hamlets, one landmark, and a connected road graph over the
seed-jittered regional anchors. Forty-two authored district profiles drive terrain, signature
sites, population multipliers, tags, and prose. The same place profile feeds generation, travel
narration, `WorldCard`, atlas, magic context, dialogue cards, and zone snapshot profiles. The
capital now has Censor Gate → Inner Court → Archive Quarter structure, with Odran only in the
center court. Tests serialize the graph, prove all settlements are road-connected, cross three
distinct districts in every primary footprint, and walk the capital through the shared CLI/GUI
backend.

The Qud-ish skeleton, but with settlements bigger than Qud favors: cities and towns that
take *several zones to walk across*, each zone a district with its own character. All
seed-derived, zero model calls.

1. **World shape.** A large zone grid (target on the order of 40×40+, sized by
   playtest-feel, not fixed here) partitioned into realm wedges from `WorldRoll`, each
   wedge holding one or more regions (WS0). Long travel is intended: distance is what
   makes a 20-hour run's geography mean something.
2. **Settlement footprints, multi-zone by design.** Per realm, roll a settlement graph:
   one capital city (footprint ~3×3 zones or larger), 1-3 towns (~2×2), villages and
   hamlets (1 zone), waystations, connected by road paths. Every settlement zone gets a
   **district identity** rolled from a per-realm district table — gate, market, temple,
   craft quarter, residential warren, harbor, court — which drives its WS1 prop grammar,
   WS2 density gravity, building footprints (wall terrain + door fixtures + interiors),
   and voice. Walking across a town should feel like crossing neighborhoods, not
   re-rolling the same screen: the gate district smells of censors and cart traffic, the
   market of brine and shouting, the temple quarter of wax.
3. **Between settlements: structured wilderness.** Roads with waymarkers, shoulders, and
   the occasional traveler ensemble; landmark zones (one bespoke-feeling set piece from a
   landmark table: a drowned mill, a bone circle, a burned watchtower); and true wilds
   where the strangeness budget rises and people vanish (WS2's empty stretches).
4. **The atlas learns geography:** current region, settlement and district names, nearest
   town with direction, the road you are on. Travel gains purpose — geography that
   resists and rewards.
5. **Emperor gating hook (cheap, now):** the capital's multi-zone footprint gives the
   imperial-defenses pool somewhere real to live — outer districts before inner court
   before throne — so the accidental turn-20 regicide path closes as a side effect of
   structure, with court content layered later.

**Files:** `WorldRoll` (graph + footprints), `GenerationSystem` (district dispatch),
`EngineViewBuilder`/`Atlas`, region JSON district and landmark tables.
**Accept (met):** same seed → same settlement graph and footprints (unit test serializes it);
walking across a rolled town crosses ≥ 3 districts with visibly different props, people,
and prose; atlas names settlement + district from inside one and the nearest town with
direction from outside; roads reach the towns they claim to.

**Significant-interior extension implemented 2026-07-09:** every regional primary settlement now
binds one authored one-zone interior, with separate capital palace and archive definitions.
Thresholds, exits, layout terrain, semantic fixtures, and residents use the unified entity,
generation, context, transition, and persistence paths. Access is a soft composition of item,
permission flag, and threshold tags rather than bespoke story gating. See
[SIGNIFICANT_INTERIORS.md](SIGNIFICANT_INTERIORS.md).

### WS4 — Quests: want templates that bind real promises

**Status: first complete slice implemented 2026-07-09.** Four shipped/embedded quest templates
seed the lead resident at each settlement center with a pressure tied to that region's real rolled
landmark. Examining the resident records a claim and binds a player-visible promise to the exact
destination zone; reaching it realizes a named evidence item, canon record, and promise-ledger
status through the existing promise machinery. The agent test completes this chain using only
inspect/atlas/journal/travel text and no provider call. Return delivery, escorts, threat clearing,
service payoffs, and richer bond/faction rewards remain in WS4.

"More NPCs to give quests" is mostly seeding — the ledgers already exist.

1. **Quest template library** (`content/quests/*.json`): fetch, deliver-to, escort,
   clear-a-threat, folk-magic service, verify-a-rumor. Each template: want text pattern,
   realization kind (the existing promise machinery: `site`, `item`, `person`,
   `escape_route`…), trigger hint, payoff consequences (gold, bond deltas, faction
   standing, a taught charter form, a lore-access bump, a revealed route).
2. **Seeding:** archetype `wantTemplates` reference template ids; fill-ins come from the
   settlement graph (a *real* neighboring town, a *real* landmark) so quests point at
   places that exist.
3. **Realize through existing lanes:** claims → promises → travel/interaction realization;
   completion routes through `update_want` + payoff consequences. No new quest engine —
   the promise ledger *is* the quest engine (EMERGENT_WORLD's thesis).

**Files:** new `content/quests/`, small `QuestTemplateCatalog`, want-seeding in WS2's
spawn path, `PromiseRealizationSystem` only if a realization kind is missing.
**Accept:** an agent playtest completes one generated quest end-to-end from player-facing
text alone (accept want → travel to a real place → act → payoff lands in ledgers);
`journal` shows the chain; no LLM call anywhere on the path.

### WS5 — Coherence: every system quotes one canon

Richness reads as *coherent* only when different systems agree.

1. **The rolled world is quotable:** ruler names, realm status, town names from
   `WorldRoll` flow into (a) atlas lines, (b) zone-entry rumor lines, (c) dialogue
   knowledge context (the lore router already injects region canon — add a `WorldRoll`
   card), (d) generated wanted posters/memorials. One fact, four surfaces.
2. **Canon records for generated landmarks:** WS3 landmarks write an `add_canon` record at
   generation; examine, background detail jobs, and the resolver's lore context all read
   the same entry.
3. **Rumors travel the graph:** rumor propagation prefers the road network (a rumor
   reaches the next town before the far coast), making geography legible through gossip.
4. **Fix the id leak while here:** live dialogue leaked "Captain soldier_2 at 11,6"
   (telemetry session, 2026-07-07) — add the display-names-only rule to the dialogue
   prompt and, for generated NPCs, ensure context cards carry display names.

**Accept:** pick any seed: the ruler of Brall named by the atlas is the same name a Brall
resident speaks in dialogue and a rumor repeats verbatim; a landmark's examine text and a
resident's directions agree; no raw entity ids or coordinates in any player-facing prose.

### WS6 — Verification and scale (continuous)

1. **Content integrity tests:** every table row's faction/tradition/terrain/charter/quest
   reference resolves; weights positive; name pools non-empty. (Extends
   `ContentCorpusIntegrityTests`.)
2. **Determinism tests:** same seed → byte-identical zone snapshot & settlement graph;
   save/load/replay stability across generated zones (episode runner already checks
   byte-stability — extend episodes to travel through towns).
3. **Richness invariants in the episode runner:** a ~20-zone "tour" policy asserting
   *distributional* shape rather than per-zone quotas — mean props per zone above a floor
   with high variance (clutter and emptiness both present), at least one personless zone
   and one cluster of ≥ 3 people, ≥ 25 distinct prop names, no nearby-zone name
   collisions, atlas names a settlement, at least one want encountered, and all living
   actors present in resolver context in the most cluttered zone visited.
4. **Scale measurement:** generate-and-save a 200-zone run; measure save size and load
   time; if snapshots bloat, add zone-snapshot compaction (drop untouched generated zones,
   regenerate from seed) *behind a measurement, not speculatively*.
5. **A dedicated FEEL_LOG playtest** after WS2 and again after WS4, scoring the wonder axis
   in the capital specifically against the FEEL_LOG [21] finding.

## Sequencing

```
WS0 regions-as-content  ──►  WS1 props   ──┐
                        └──►  WS2 people  ──┼──►  WS3 places  ──►  WS4 quests  ──►  WS5 coherence
WS6 verification ────────────── continuous ┘
```

WS0 is small and unblocks everything. WS1 and WS2 are independent and immediately visible
in play (worth a feel playtest on their own). WS3 is the largest single piece; its
settlement graph is what WS4's quests and WS5's coherence quote. Each workstream lands
playable and green on its own — no big-bang integration.

## What this plan deliberately defers

- **Model-voiced texture at generation time** — background enrichment of generated
  fixtures already exists and suffices; foreground generation stays deterministic.
- **Full faction resource geopolitics** (Phase 3 deepening) — the settlement graph gives
  it a stage later; not needed for richness now.
- **Interior multi-zone buildings / dungeon generation** — one-zone interiors first;
  descend-able sites are a later landmark archetype.
- **Weather/season systems** — ambient lines can fake it; real systems wait for a pull
  from playtesting.
