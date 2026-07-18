# Content Sprint Plan — A Shockingly Lush First Hour

Status: owner-calibrated 2026-07-17. This is the execution plan for one uninterrupted day of
AI-driven development, intended to produce roughly three human-development weeks of player-visible
content progress. It is subordinate to `GRAND_VISION.md` on intent and to
`IMPLEMENTATION_PLAN.md` on engine discipline, but it supersedes their normal sequencing for this
specific owner-directed content sprint.

The sprint thesis:

> Make the reference transect feel shockingly lush by giving every few steps a specific object,
> creature, person, tactical problem, or found fact that invites action — without turning the
> world into noise, filler combat, or scripted story content.

The sprint prioritizes the first hour of the Marble Containment Yard → Hollowmere → Brall
transect. The waystation is the flagship set-piece. Items, props, creatures, enemies, encounters,
found documents, group conversations, charter forms, and legibility are in scope. Capital routes,
Odran, endings, portraits, palettes, and audio are not.

## Binding Owner Direction

- The desired outcome is a world that feels **shockingly lush**.
- Prioritize the reference transect and especially the **first hour**.
- The thinnest current surfaces are **items, props, and enemies**.
- Optimize for **wonder, tactics, and pace**.
- Reusable engine and schema improvements are encouraged when they materially enrich play.
- Weak existing content may be deleted or replaced.
- The waystation should support force, stealth, a clerk alliance, courier interception, forgery,
  body swap, and wild magic organically through shared state — never through route-specific
  completion handlers.
- Hollowmere loves exotic pets and contains honest disagreement between people who value imperial
  stability and people who want freedom.
- Brall should feel colder and more mountainous. Its signature sprint proof is a tavern
  conversation in which several NPCs collaboratively one-up one another's stories.
- Nonviolent resolution is important.
- NPC richness should primarily come from generated archetypes rather than a large fixed cast.
- The item catalog should become large; spell components and equipment should matter.
- Every player-visible promise belongs in the journal. Major game surfaces must be discoverable
  through ordinary affordances and rebindable hotkeys, not only typed-command knowledge.
- Near-term lore should favor found documents and actionable secrets.
- Add charter-spell content and support curses, debts, and altered items as story-generating costs.
- Organize regional content as packs and replace content-selection switches with data.
- Playtest personally through the real CLI with the live Gemini resolver. Do not delegate
  playtests to subagents. Mock and replay providers may support automated regression, but they do
  not count as play evidence.

## Current Baseline

The engine is already broad enough to support this sprint. The content corpus is not yet broad
enough to make that engine collide with itself often.

| Surface | Current authored baseline | Sprint diagnosis |
|---|---:|---|
| Regions / traditions | 14 / 13 | Broad map, thin density outside the first slices |
| Item definitions | 6 | Far too small; origins and wares already name items the catalog does not richly define |
| Prop bases / ensembles | 61 / 16 world-wide | Strong combinatorics, but only 4–5 bases and 1–2 ensembles in each priority region |
| Population archetypes | 44 world-wide | Only 2 opening, 4 Hollowmere, and 3 Brall archetypes |
| Encounter ingredients | 4 | Good proof, insufficient variety |
| Tactical actor catalog | none | Scenario actors and encounter casts duplicate small stat blocks and policy strings |
| Journey templates / handoffs | 4 / 10 | Mechanically useful, but not yet richly dressed by local situations |
| Charter forms | 7 | Good tactical floor, too narrow for repeated first-hour play |
| Lore cards | 25 | Adequate background; actionable documents are the higher-value gap |
| Journal | flat string list | All visible promises are present, but prioritization, filtering, navigation, and hotkey access are weak |
| Dialogue response | one speaker | Cannot express the requested Bralli group conversation or a Hollowmere household argument |

Several remaining region switches in `GenerationSystem`, `TextureGrammar`,
`ThreatArchetypeGenerator`, and `PromiseRealizationSystem.Helpers` demonstrate missing content
vocabulary. Remove those switches as the relevant pack fields arrive; do not add Brall or
Hollowmere branches beside them.

## Scope

### Must ship

1. A versioned, recursively loaded region-pack format with the priority content migrated into it.
2. A large data-authored item catalog with meaningful components, equipment, foci, regional
   distribution, inspect text, and repeat controls.
3. District-aware prop and scene ensembles for the opening, waystation, Hollowmere, and Brall.
4. A shared actor-archetype catalog used by culturally specific enemies, named hunters, encounter
   casts, and Hollowmere pets.
5. At least the roadmap floor of tactically legible enemy roles, with nonviolent alternatives in
   representative encounters.
6. A waystation whose many approaches work through ordinary items, identities, wants, schedules,
   access state, witnesses, terrain, and consequences.
7. A reusable multi-participant dialogue response and one organic Bralli tavern story-circle
   proof, plus at least one Hollowmere group disagreement.
8. A developed first-hour Free Folk presence: watching cell, safe-haven possibility, sweep
   pressure, actionable petitions, and movement-flavored handoffs.
9. More charter forms plus curated curse, debt, and altered-item cost profiles with counterplay.
10. A structured, navigable journal and discoverable hotkeys/affordances for the game's major
    read-only surfaces.
11. Content validation, seeded diversity reports, save/replay coverage, and repeated personal
    live-Gemini CLI playtests.

### Explicitly deferred

- Capital approach content, Odran characterization, final confrontation, post-victory play, and
  ending work.
- Portraits, tiles, palettes, animation, ambience, score, and other presentation assets.
- Multi-zone interiors, full dungeons, weather, ecology, and NPC daily schedules.
- Full density for every world region. Non-slice regions remain generated-thin.
- A pet subsystem. Pets are ordinary entities using actor, ownership, trade, want, bond,
  follower, and AI lanes.
- A quest, mission, encounter, conversation, or scene ledger.
- A `BrallStorySystem`; Bralli tales use group dialogue, deeds, rumors, memories, wants, and
  ordinary reactions.
- Forced callbacks that return a specific earlier noun. Returns remain eligible and
  salience-driven, never script-mandated.
- The **Kindled** doctrine conflict. In `FREE_FOLK_MOVEMENT.md`, the Kindled are a proposed radical
  Free Folk wing that wants to answer the Shadow Purge with terror and possession-shaped tactics.
  It is valuable later moral pressure, but it is not required to make the first-hour Movement
  vivid. This sprint may seed one disputatious radical archetype, but it does not build the
  doctrine arc.

## Lushness Rules

### Specificity with rhythm

Lush does not mean every tile is crowded. Preserve empty roads, quiet reeds, closed offices, and
snowbound passes so that markets, halls, confiscation yards, pet gatherings, and waystations read
as events. Density must vary by district, habitat, and seed.

### Memorable scenes are rolled compositions

A scene is a compatible packet of ordinary content: fixtures, items, actors, wants, claims,
terrain, and encounter pressure. Extend the current ensemble/encounter grammar rather than
authoring room scripts. Completion continues to fall out of entity, inventory, access, want,
deed, promise, and faction state.

### Content-unit rule, calibrated by category

- An ordinary semantic prop is complete when it is specific, inspectable, targetable, and visible
  to resolver context. It does not need a deterministic hook.
- A hook-bearing prop or document needs an actionable claim, item, route, service, promise anchor,
  or other downstream reader.
- A distinctive item needs at least two plausible uses among equipment, focus, component,
  sacrifice, trade, gift, evidence, access, reading, and promise anchoring.
- A pet needs a recognizable temperament plus at least two actions among observe, trade/adopt,
  gift, recruit/follow, rescue, fight, calm, or exploit as terrain/spell context.
- An enemy needs a readable intent, positional question, cultural reason to exist, and a systemic
  counter besides damage.
- An encounter needs at least three honest approaches where its fiction permits them, including a
  representative nonviolent route.
- A social scene needs participants with distinct wants or knowledge and must be able to create or
  clarify playable state.

### Variety before volume

Counts below are floors and caps, not permission to write filler. A smaller corpus that passes the
seeded diversity and content-unit gates is better than a larger corpus of synonyms.

## Sprint Content Targets

| Content surface | Shipping floor | Target if quality remains high |
|---|---:|---:|
| Total authored item definitions | 72 | 96 |
| Components/reagents within that corpus | 24 | 32 |
| Equipment/foci within that corpus | 20 | 28 |
| Tactical hostile/pressure actor archetypes | 12 | 14 |
| Hollowmere pet/companion creature archetypes | 8 | 12 |
| Total encounter ingredients | 10 | 14 |
| Opening regional population roles | 5 | 7 |
| Hollowmere regional population roles | 10 | 12 |
| Brall regional population roles | 8 | 10 |
| Opening prop bases / ensembles | 10 / 5 | 12 / 6 |
| Hollowmere prop bases / ensembles | 14 / 8 | 18 / 10 |
| Brall prop bases / ensembles | 12 / 6 | 14 / 8 |
| Actionable found-document templates | 18 | 30 |
| Multi-person social-scene templates | 4 | 8 |
| Total charter forms | 12 | 14 |
| Curse/debt/altered-item cost profiles | 9 | 12 |
| Free Folk first-hour handoff/scene variants | 6 | 10 |

The priority packs own most of these additions:

- **Marble Containment Yard / waystation:** confiscation tools, evidence furniture, courier and
  clerk roles, official components, credentials, charter gear, claims, schedules, and tactical
  imperial actor roles.
- **Hollowmere:** pet-keeping households and markets, wet folk craft, shelter politics, river
  equipment, unusual medicines, reeds as tactical material, local loyalists, Free Folk contacts,
  and pet-related services.
- **Brall:** cold fjord and mountain texture, snow/ice/stone/pass terrain, taverns and public
  halls, scrimshaw and rope equipment, charter bone-work, tellers and witnesses, occupation
  pressure, and group retelling scenes. This is a content identity, not a bespoke mechanical
  subsystem.

## Region-Pack Shape

Introduce recursively discovered packs:

```text
content/region-packs/<region-id>/
  pack.json
  region.json
  population.json
  places.json
  interiors.json
  props.json
  items.json
  actors.json
  encounters.json
  documents.json
  journeys.json
```

`pack.json` carries `schemaVersion`, stable pack id, region id, dependencies, and optional content
files. Files retain the existing top-level arrays where practical so catalogs can share readers.
A small `ContentPackLoader` owns recursive discovery, duplicate-id rejection, dependency ordering,
loose-file overrides, and embedded-resource parity. Catalogs remain responsible for parsing their
own records.

Migrate all existing region definitions mechanically so there is one shipping path, not old flat
files plus a priority-pack override. Global lore, operations, capabilities, charter forms, and
truly cross-regional items may remain under their existing global content directories.

Required pack validation:

- schema version, ids, duplicate definitions, and dependency resolution;
- referenced faction, realm, tradition, terrain, item, actor, encounter, charter, service, and
  journey ids;
- positive weights and non-empty pools;
- every region has population, places, props, at least one interior, and reachable roads;
- every notable actor has intent/counter or want/knowledge as appropriate;
- embedded build loads the same corpus as loose development files;
- same seed produces the same pack selection and generated state.

## Work Packages

The packages are sequential because the owner requires the primary agent to do the work and live
play personally. Each package ends in a runnable build and a live CLI checkpoint.

### WP0 — Pin the baseline and diversity report

Before changing content:

1. Record current full-suite result and content counts.
2. Add a read-only `content-report` CLI/dev command or test helper that samples the first-hour
   transect over at least twenty seeds.
3. Report exact item names, prop names, ensembles, residents, creatures, encounters, documents,
   services, and charter acquisition opportunities by seed.
4. Record exact-name repetition, pairwise seed overlap, empty/dense rhythm, unresolved references,
   and resolver-context counts.
5. Play one current first-hour route with live Gemini and preserve the transcript as the
   before-evidence.

Exit: later claims of lushness compare against a committed baseline rather than memory.

### WP1 — Region packs and data-owned vocabulary

1. Implement the recursive pack loader and embedded-resource path.
2. Migrate the existing fourteen regional rows into pack directories without behavioral change.
3. Add pack-owned vocabularies for generated fixture names, threat adjectives/nouns, threat entry
   prose, default promised sites, and terrain texture currently selected by region switches.
4. Delete the superseded switches from `TextureGrammar`, `ThreatArchetypeGenerator`,
   `GenerationSystem`, and promise helpers as each vocabulary receives a real reader.
5. Extend integrity tests to load loose and embedded packs and reject duplicates or bad references.

Exit: adding cultural vocabulary to a region is data authoring; no priority region needs an id
branch for content selection.

### WP2 — Large item corpus and equipment that matters

1. Replace `ItemCatalog.CreateMinimal()` as the normal shipping source with a versioned
   `content/items` plus region-pack item loader. Keep only a compile-safe fallback in code.
2. Expand `ItemDefinition` with description, rarity, region/habitat weights, repeat policy, and a
   narrow equipment-modifier payload.
3. Support derived equipment effects without mutating base stats on equip/unequip. Initial shared
   modifiers may cover attack, defense, max HP, max mana, resistance/weakness tags, and resolver
   focus bias. All readers use one equipment-effect service.
4. Surface exact effects, material, tags, value, spell bias, protection, focus state, and
   provenance in shared inventory/equipment views.
5. Give distinctive items `unique` or bounded repeat policies. Staple commodities may repeat;
   named components, gear, curios, documents, and evidence should not casually duplicate within a
   run.
6. Author the item corpus by function and culture. Components must be tempting both to spend in a
   spell and to retain for equipment, trade, gifting, evidence, or later leverage.
7. Route region ground loot, encounter rewards, merchant wares, origin packages, promise items,
   and documents through the same catalog.

Exit: the first hour produces real equipment/component decisions, and item variety changes visibly
across seeds without inventory-capacity or durability chores.

### WP3 — District-aware props, documents, and scene ensembles

1. Add habitat/district/site tags and weights to prop bases, hooks, and ensembles.
2. Let an ensemble stage compatible fixture, item, actor/creature, readable, and claim-source
   children through the detached generation consequence sandbox. Do not add a scene ledger.
3. Extend generated prop hooks beyond readable/anchor where existing components suffice: claim
   source, contained item, evidence provenance, promise anchor, and service/route hint.
4. Keep actors and hook-bearing objects in protected resolver/dialogue context; measure cluttered
   zones after every density increase.
5. Author varied scene families rather than exact rooms:
   - opening confiscation spills, misfiled evidence clusters, civilian queues, audit desks, courier
     exchanges, and dangerous pet crates;
   - Hollowmere pet markets, escaped companions, household shelter debates, ferry disputes,
     medicinal work yards, shrine gatherings, and occupation inspections;
   - Bralli snowbound taverns, mountain-pass shelters, scrimshaw arguments, charter bone-work
     inspections, public tale circles, and cold harbor labor.
6. Author actionable documents with concrete carriers and causes: schedules, ledgers, warrants,
   care instructions, household shelter signs, pet pedigrees, confiscation tags, scrimshaw witness
   records, and mountain travel warnings.

Exit: each priority district has a different prop/scene distribution, and dense zones remain
legible to the player and the resolver.

### WP4 — Shared actor archetypes, enemies, and Hollowmere pets

1. Add a compact data-authored actor-archetype catalog. Fields include category, region/faction
   eligibility, stats, material, equipment, charter forms/abilities, behavior tags, terrain
   preference, intent language, inspectable defense/weakness/counter, wants/stakes, and interaction
   verbs.
2. Make encounter casts reference actor archetypes instead of repeating small stat blocks.
3. Keep one AI candidate pipeline. Add an actor action only when at least two archetypes and a
   non-enemy actor can use it.
4. Author at least twelve hostile/pressure archetypes across imperial bruiser, ranged, caster,
   leader/support, investigator, beast, and elite-hunter roles plus Hollowmere and Brall pressures.
5. Give every hostile a telegraphed commitment and one non-damage counter such as terrain,
   credential, bribery, want satisfaction, status, separation from support, theft of equipment, or
   escape.
6. Author Hollowmere's exotic-pet culture as ordinary creature archetypes. Pets should come from
   Hollowmere and imported realms, appear in homes, markets, ferries, shrines, and accidental
   escapes, and carry temperament plus actionable tags.
7. Adoption, sale, rescue, calming, gifting, and following reuse trade, wants, bonds,
   `update_control`/follower consequences, and ordinary AI. Do not create pet ownership meters.
8. Add pet-related people and services: keepers, trainers, healers, importers, worried owners,
   enthusiasts, smugglers, and imperial inspectors.

Exit: a cluttered scene can contain civilians, a culturally specific enemy, and several distinct
pets without confusing targeting, inspection, context routing, or turn behavior.

### WP5 — Encounter grammar and the organic waystation

Expand the encounter library with reusable ingredients:

- patrol crossing civilian witnesses;
- guarded threshold with credentials, stealth, social, force, body, and magic approaches;
- two wants contesting one object/person/place;
- valuable component inside environmental danger;
- courier exchange or interception window;
- escort/follower under positional pressure;
- investigator arriving after evidence;
- elite plus support units with interlocking roles;
- escaped or endangered creature complicating a social situation;
- public gathering whose witnesses change the strategic value of violence.

Use those ingredients to make the waystation the flagship proof. Its state should contain the
possibility of every route, not a route selector:

| Approach | Ordinary enabling state | Durable difference |
|---|---|---|
| Force | actors, destructible/openable fixtures, alarm, witnesses | public deed, casualties, damaged documents, immediate report risk |
| Stealth | sight lines, concealment, schedules, alternate entrance | delayed discovery, intact originals, uncertain attribution |
| Clerk alliance | clerk want, bond, evidence, payment/service | copies, living informant, no automatic discovery |
| Courier interception | timed/forecast actor movement, satchel, uniform | partial documents, identity/evidence trail |
| Forgery | writing materials, seal access, document transformation | false schedule/claims the Empire may act upon |
| Body swap | ordinary body/soul identity and credentials | access plus severe identity consequences |
| Wild magic | all local objects as resolver grammar | strongest shortcut/bonus, validated cost and witness consequences |

Completion reads which documents exist, who holds them, what their claims now say, which actors
live or cooperate, what access state changed, and what deeds were witnessed. There is no
`WaystationRoute` field.

Exit: each route completes through shared commands/consequences in CLI and Godot; at least one
nonviolent run and one mixed route are proven live.

### WP6 — Multi-person conversation and Brall's tavern proof

Generalize dialogue rather than adding a Brall-only exchange:

1. Replace the single spoken string with validated utterances carrying speaker entity id, text,
   and optional delivery. A single-person conversation is one utterance through the same schema.
2. A group conversation includes the player plus two to four eligible nearby participants. One
   provider call may return a short exchange; every speaker id must resolve to an authorized
   participant.
3. Preserve recent dialogue by a stable participant-set/conversation key so a repeated command can
   continue the exchange without entering a modal dialogue game.
4. Apply each participant's claims, memories, wants, bonds, and actions through the existing typed
   proposal and consequence grammar. Invalid child proposals reject transactionally without
   inventing speaker state.
5. Expose `talk`, an explicit group-talk form, and nearby group-conversation context actions
   through the shared command surface. Godot and CLI use the same command and result envelope.
6. Provider-disabled play receives a deterministic, state-grounded fallback exchange; it cannot
   manufacture critical claims.
7. Author social-scene eligibility from fixtures and roles rather than ids: tavern benches,
   story-circle tags, household tables, waiting rooms, and market gatherings.
8. Brall proof: several tellers take a real deed/rumor and one-up concrete details. Retellings keep
   provenance and may alter hospitality, wants, bonds, service/access, recruitment interest, or
   danger through ordinary rumor and consequence readers.
9. Hollowmere proof: a household or pet-market group disagrees honestly about imperial stability,
   wild freedom, risk to family, and the fate of a specific creature or person.

Exit: group dialogue changes real state, remains bounded and replayable, and no code checks a
Bralli region or character id to produce the exchange.

### WP7 — First-hour Free Folk content

Develop the Movement where the sprint can make it playable now:

1. Add watching-cell roles near the waystation, each with tactical knowledge, personal risk, and
   different opinions about using the sorcerer.
2. Give the first cell finite shelter, hands, support, and secrets through existing faction
   resources, with visible services/absences rather than a faction bar.
3. Make warning or saving a settlement capable of creating a real safe-haven composition:
   threshold access, resting/recovery service, storage or provider, local memory, and a reason to
   return.
4. Add movement-flavored versions of existing journey families plus safehouse, document theft,
   warning, animal rescue, supply redirection, and informant-pressure scenes where the existing
   state grammar can recognize completion.
5. Use NPC initiative for petitions with named causes. Do not create a Movement request queue.
6. Let pro-Empire Hollowmere residents credibly refuse aid because roads, trade, family safety, or
   fear of another Purge matter to them.
7. If the player ignores the sweep or Free Folk, let scheduled consequences land on named places
   and people without moralizing through an invisible penalty.

Exit: the first hour presents the Free Folk as an existing, vulnerable network and gives the
player meaningful reasons to help, exploit, or ignore it.

### WP8 — Charter forms and story-generating magical costs

1. Grow the charter spellbook from seven forms to at least twelve using existing operations.
   Prioritize deterministic mobility/repositioning, dispelling or cleansing, detection, defensive
   control, and one terrain interaction. Enemy charter users draw from the same spellbook.
2. Seed acquisition through manuals, notices, equipment, services, and confiscated records rather
   than a skill tree.
3. Author curse, debt, and altered-item profiles as cost guidance/content, never spell-phrase
   handlers.
4. Every curse has a mechanical condition, cause, journal surface, and at least one general route
   to clear, transfer, exploit, bargain over, or endure for an upside.
5. Every debt can realize as an ordinary claimant, service obligation, item demand, reputation
   exposure, or scheduled pressure with provenance and counterplay.
6. Altered items preserve original identity/provenance plus new description, traits, material,
   value/equipment changes where validated, and later resolver/dialogue visibility.

Exit: at least one live cast in each priority region produces a cost that creates future play
rather than merely subtracting HP or mana.

### WP9 — Journal, affordances, and hotkeys

The current journal already lists every `PlayerVisible` promise. Preserve that truth and replace
the flat presentation with a typed shared view:

- current objectives and explicit next steps;
- all active promises, regardless of salience;
- known wants and notable relationships;
- actionable rumors and found secrets;
- approaching pressure, reports, sweeps, debts, hunters, and deadlines;
- completed, cleared, contradicted, or transformed threads;
- source, named carrier, destination/region hint, and cause where known.

Add filters and stable ids to the shared `GameView`; CLI JSON receives structured fields while the
human CLI and Godot render readable sections. Mystery may hide truth or outcome, but not every
available next step.

Add rebindable interface actions and visible buttons for journal, inventory/equipment, atlas,
rumors, character, standing, followers, and help/controls. Choose defaults only after checking the
existing vi movement, spell, talk, pickup, inspect, autoplay, and debug bindings; do not steal a
movement key. The controls screen remains the single discoverable binding table.

Nearby entities continue to expose context actions, including talk/group talk, trade, services,
read, examine, open, enter, and pet-related ordinary verbs. No major content surface should
require memorizing an undocumented typed command.

Exit: a documentation-blind player can answer what to do, what they promised, what is approaching,
what an item does, and how to open every major read-only surface without debug state.

### WP10 — Integration, deletion, and tuning

1. Delete migrated flat content, minimal duplicate catalogs, superseded switches, weak generic
   messages, duplicate item definitions, and any route-specific experimental helper.
2. Run formatting, focused tests, the full suite, content validation, save/load, transcript replay,
   and the seeded diversity report.
3. Personally play at least three different first-hour seeds through the real CLI with Gemini 3.5
   Flash at medium effort. Use ordinary player-facing commands; debug state may diagnose but may
   not substitute for discovery.
4. Play at least one nonviolent waystation route, one loud or mixed route, one Hollowmere pet and
   household sequence, and the Brall group-tavern proof.
5. After each run, score wonder, tactics, pace, legibility, item temptation, scene memorability,
   and seed freshness. Fix the most damaging break and replay it live before moving on.
6. Preserve useful transcripts and add a concise evidence section to this document or a linked
   sprint report.

Exit: the definition of done below is met; a green suite alone does not end the sprint.

## Dependency Order

```text
WP0 baseline
  ↓
WP1 region packs and vocabularies
  ↓
WP2 items ─────┐
WP3 scenes ────┼─→ WP5 waystation/encounters ─┐
WP4 actors ────┘                               │
                                                ├─→ WP10 integration and live tuning
WP6 group dialogue/Brall ───────────────────────┤
WP7 Free Folk first hour ───────────────────────┤
WP8 charter/cost content ───────────────────────┤
WP9 journal/hotkeys ────────────────────────────┘
```

Within the single-agent day, WP2–WP4 are implemented sequentially even though their designs are
independent. Do not open three half-finished schema migrations at once.

## Verification Matrix

| Change | Required automated proof | Required live proof |
|---|---|---|
| Region packs | loose/embedded parity, duplicate/reference rejection, seed stability | travel through every priority pack |
| Items/equipment | load validation, derived modifiers, equip/focus/protect, trade/loot/promise, save/replay | choose between keeping, equipping, and spending a component |
| Props/scenes | district filtering, ensemble transaction rollback, actor/hook context protection | inspect and cast through a dense scene |
| Actors/pets | intent/action agreement, weakness/counter, AI stability, recruit/trade/follow | resolve one enemy nonviolently and interact with/adopt one pet |
| Encounters/waystation | each approach changes ordinary state, no private completion flag | nonviolent and loud/mixed live routes |
| Group dialogue | speaker authorization, multi-speaker proposals, rollback, memory/provenance, replay | Brall tavern and Hollowmere disagreement |
| Free Folk | resource spend, safe-haven state, ignored-sweep result, provider-off completion | discover and answer or refuse the first cell |
| Charter/costs | fixed zero-provider charter resolution; curse/debt/item persistence and counterplay | one useful new charter cast and one future-generating wild cost |
| Journal/hotkeys | every visible promise included; structured CLI parity; binding conflicts rejected | navigate all major views without typing their command names |

## Diversity And Feel Gates

Run the content report over at least twenty seeds and retain the raw summary.

### Item freshness

- Before reaching the first major Hollowmere settlement, each sampled run encounters at least
  twelve distinct non-commodity item names when ground loot, stock, carried equipment, documents,
  and encounter objects are combined.
- No distinctive `unique` item repeats within one run.
- Pairwise overlap of non-commodity first-hour item sets stays below 50% across sampled seeds.
- Staple commodities may repeat, but no seed should make more than half of its interesting item
  encounters the same three catalog entries.
- At least three first-hour items present a real keep/equip/spend/trade/gift decision.

### Prop and scene freshness

- A ten-zone priority-region walk encounters at least forty distinct exact prop names, at least
  five scene ensembles, both quiet and dense zones, and no unexplained exact duplicate ensemble in
  adjacent zones.
- Every generated prop is inspectable and targetable; hook props expose their action through
  shared views.
- Each first-hour seed produces at least four memorable scene candidates assembled from different
  families.

### People, creatures, and encounters

- Across eight Hollowmere seeds, at least eight pet species appear; an individual run sees a
  smaller surprising subset rather than the complete catalog.
- The first hour exposes at least three tactically distinct hostile/pressure roles when conflict
  occurs, without increasing combat merely to meet the count.
- No encounter ingredient appears twice consecutively in a first-hour route unless persistent
  state explains it.
- At least one representative encounter per run can be resolved without killing.

### Wonder, tactics, and pace

- Personal live-play scores reach at least 4/5 for wonder, tactics, pace, legibility, item
  temptation, and scene memorability.
- Ordinary play rarely goes more than three real minutes without a meaningful discovery,
  relationship/pressure return, tactical decision, or tempting fresh-cast opportunity.
- Richer content does not inflate Gemini context enough to violate the existing thirty-second
  experiential ceiling; actors and hooks remain protected in cluttered scenes.
- After each live run, the tester can name at least eight specific encountered people, creatures,
  objects, documents, or scenes. Two seed summaries should not substantially rhyme.

## Definition Of Done

The sprint is complete only when all of these are true:

1. The first hour is visibly denser in specific, actionable content without becoming uniformly
   crowded or combat-heavy.
2. Items and equipment create repeated decisions; distinctive items do not recur monotonously
   across seeds.
3. Hollowmere's love of exotic pets is visible in people, places, services, trade, escapes, and
   potential companionship.
4. Hollowmere contains legible pro-stability and pro-freedom positions attached to concrete wants,
   not generic ideological dialogue.
5. The waystation supports every requested approach through ordinary state and produces different
   durable facts without a route flag or special completion handler.
6. At least twelve enemy/pressure archetypes have learnable tactical identities, and nonviolent
   resolution is representative rather than theoretical.
7. A Bralli tavern supports a real multi-NPC exchange that one-ups a provenance-bearing story and
   can change ordinary gameplay state.
8. Brall reads colder and more mountainous while preserving its bone, whalehold, ale, and
   collaborative-story canon.
9. The first-hour Free Folk arc feels present and vulnerable without becoming a questline or
   gating the run.
10. Every player-visible promise is legible in the journal; all major read-only game surfaces have
    discoverable GUI affordances, CLI commands, and rebindable hotkeys where appropriate.
11. New charter forms, curses, debts, and altered items create usable tactical or future-play
    options.
12. Loose and embedded content validate, deterministic generation is seed-stable, all durable
    additions save/replay, the full suite passes, and at least three personal live-Gemini CLI runs
    satisfy the diversity and feel gates.

## Stop Rules

- If a content row needs region-specific engine code, first repair the shared pack, actor, item,
  ensemble, encounter, dialogue, or consequence seam.
- If a schema refactor consumes effort without producing a visible first-hour improvement, finish
  the narrow safe migration, record the remainder, and return to playable content.
- If a target count encourages filler, stop at the shipping floor and improve cross-readers,
  distribution, and scene composition.
- If group dialogue grows toward a modal conversation game, keep the bounded multi-utterance turn
  and defer larger conversation UX.
- If pet mechanics suggest a pet subsystem, express the next slice through ordinary actor,
  follower, want, bond, trade, service, and equipment state.
- If live Gemini produces valid but generic use of the new content, improve compact context,
  descriptions, capability guidance, and examples; do not add item-name or spell-phrase handlers.
- Do not spend this sprint on the capital merely because the route reaches it. Finish the lush
  first-hour proof and the Brall group-conversation proof first.
