# Holistic Development Plan — Freedom That Leaves a Mark

Status: active plan, written 2026-07-09 after a repository-wide design, architecture, test,
and playtest review. [GRAND_VISION.md](GRAND_VISION.md) owns the destination;
[SYSTEMS_VISION.md](SYSTEMS_VISION.md) owns the interlocking machine;
[PROCGEN_RICHNESS_PLAN.md](PROCGEN_RICHNESS_PLAN.md) owns the detailed props/people/places
work; this document decides how those efforts become a better *game* rather than a larger set
of subsystems.

## Executive assessment

Sorcerer's foundations are unusually strong for its age. One authoritative C# engine drives the
GUI and CLI. Wild magic is parsed, normalized, validated, and applied transactionally. The entity
model, typed consequence grammar, body/soul split, persistence, replay, dialogue claims, wants,
promises, rumors, factions, world-turn budget, background jobs, and agent harness are all real.
The 2026-07-09 baseline was 676 passing tests before this plan's first implementation slice.

The product is thinner than the architecture. The opening room has many deep rules, but the wider
world still offers too few people, objects, places, and recurring pressures for those rules to
collide often. The main risk is no longer “can the engine represent this?” It is **ledger sprawl
without lived experience**: facts are correctly recorded but do not come back soon, specifically,
or visibly enough to shape the player's next decision.

The next phase should therefore optimize for one loop:

> The player notices a specific affordance, authors an action, changes several durable facts,
> sees someone carry those facts onward, and later meets a concrete answer they can act on.

Every release should make that loop shorter, denser, and more surprising.

## Owner decisions (2026-07-09)

These decisions calibrate the plan and should be treated as defaults unless playtesting later
provides a concrete reason to revise them.

1. **NPCs may act toward the player.** NPCs can approach, interrupt, seek out, or follow the
   player because of wants, rumors, memories, promises, and deeds. Initiative stays bounded to
   explicit pump points and carries a legible cause; it does not become continuous off-screen life
   simulation.
2. **World response uses layered timing.** A significant action should usually receive one visible
   answer within one to three zone transitions. Larger consequences may mature much later, so the
   world feels responsive without making every payoff immediate.
3. **Player-authored names are opt-in and durable.** When a spell, enchanted object,
   organization, relationship, or other creation crystallizes, the player may explicitly name it.
   Once accepted, the exact name becomes authoritative semantic state that dialogue, rumors,
   resolver context, chronicles, and future reactions should reuse.
4. **Fast travel is diegetic.** Later travel acceleration comes from in-world transport—coaches,
   ferries, caravans, ships, mounts, guides, charter routes, or stranger regional equivalents—not
   an abstract teleport on the map. Any journey that advances time pumps bounded world-turn
   activity for the elapsed interval and may carry costs, encounters, news, or route conditions.
5. **Early wild magic deliberately errs permissive.** If an intention can be expressed by the
   validated consequence grammar and applied coherently, prefer allowing it—even when dramatic,
   durable, or badly balanced—over protective refusal. Preserve authority, transactionality,
   protected-item consent, and state invariants; postpone power-balance tightening until playtest
   evidence identifies an actual problem.
6. **Settlement hierarchy starts regional.** Each culture begins with one major multi-zone center
   plus smaller hamlets and sites; additional comparable towns should arrive only when the first
   geography is dense enough to justify them.
7. **Ordinary buildings stay seamless.** District maps hold ordinary interiors directly.
   Significant palaces, temples, archives, dungeons, and impossible spaces may become separate
   one-zone sites; multi-zone interiors wait until town traversal is fun.
8. **NPC movement is purposeful, not scheduled theater.** Named people keep home/work anchors and
   may relocate through bounded world turns when a want or event warrants it. Do not simulate daily
   schedules for every resident.
9. **Journeys do not require quest acceptance.** Wants, claims, and promises form readable journal
   threads that the player may solve, reinterpret, exploit, or ignore.
10. **Roads are both places and eventual transport corridors.** Roads are physical zone chains.
    Discovered in-world ferries, caravans, guides, ships, and similar services may later skip
    stretches while advancing time and pumping world activity.

## Product tests

These are the three standards behind all prioritization.

### Freedom

Freedom is not the number of operation types. It is the percentage of reasonable intentions for
which the player can find a legible route to a meaningful result.

- Objects, people, terrain, rumors, names, and promises must all be usable spell vocabulary.
- The resolver should prefer partial fulfillment or a strange price to a flat refusal.
- Non-magical routes—talk, trade, stealth, gifts, body swap, faction play—must reach the same
  world state instead of becoming prose-only alternatives.
- Failure must teach the player what was missing without leaking hidden state.

### Expression

Expression means the player's particular choice matters beyond raw power.

- Two casts with the same tactical category should differ because of wording, place, materials,
  witnesses, relationships, and cost.
- The game should remember player-created things: an imbued object, named effect, spell echo,
  changed body, broken promise, or peculiar ally.
- Naming those creations is opt-in; once named, downstream systems preserve the player's wording.
- Costs should become story material and future constraints, not only resource subtraction.
- Reliable charter magic and spell echoes should preserve pacing while keeping wild magic as the
  source of the run's identity.

### Responsiveness

Responsiveness means a consequence returns to play through a visible carrier.

- A deed should reach a witness, rumor, faction, relationship, or place—not merely a ledger row.
- A bound claim must become a real person, item, route, service, threat, or site.
- The reaction should name who carried it and why they care.
- Important consequences should return on a useful cadence: often within the next few zone
  transitions, and sometimes much later for genuine payoff.

## What to preserve

| Area | Strong existing piece | Do not lose it while deepening |
|---|---|---|
| Wild magic | Untrusted proposal pipeline; capability routing; “yes, at a price”; evaluations and audits | Never let a model mutate state or create a prose-only success |
| Simulation | Unified entities and shared typed consequences | Do not add separate quest, prop, NPC, or finale engines |
| Social world | Claims, memories, bonds, wants, services, promises, rumors | Make the lanes cross-read; do not expose them as approval bars |
| World reaction | Witness attribution, deeds, legend, finite faction resources, bounded world-turns | Reactions remain expenditures at pump points, not a life simulator |
| Interfaces | One `GameSession` for Godot, CLI, replay, and tests | Every player action and readout remains agent-playable |
| Reliability | Transaction rollback, state validation, save/load, transcript replay | Generated content and provider results remain evidence, never authority |
| Performance | Instant charter/echo lanes; measured provider telemetry | More world richness must not require more foreground model calls |

## Strategic diagnosis

1. **The world needs more handles before it needs more rules.** The consequence grammar is broad
   enough for the next several releases. Specific props, uneven populations, district identities,
   local wants, and real routes will create more freedom than another operation family.
2. **The hand-offs need a higher encounter rate.** The desired chains already have most of their
   links. Regional prop ensembles and population clusters now seed more inputs; the next job is to
   give those inputs district structure and deliberately verify end-to-end journeys.
3. **Geography must become shared canon.** Atlas, generation, dialogue, rumors, promises, and
   factions should quote the same rolled places and rulers. Agreement produces the feeling of a
   real world more cheaply than additional prose.
4. **Legibility is part of mechanics.** A reaction without a carrier or a cause reads as random.
   Each implementation slice needs a player-facing return path, not just debug-state evidence.
5. **The late game should wait for the world flywheel.** Court infiltration, coalitions, and the
   emperor become compelling when they reuse bonds, bodies, rumors, promises, and faction
   resources. Building bespoke capital content first would freeze the wrong abstractions.

## Program of work

### 0. Keep an experience scorecard running

This is continuous and blocks no feature work.

- Maintain a small prompt corpus for freedom: transformations, social magic, indirect terrain
  use, names, promises, item sacrifice, body/soul changes, and ambitious partial fulfillment.
- Add a deterministic 20-zone “world tour” episode beside combat-heavy episodes.
- After every major richness slice, run one mock tour and one live-provider feel session.
- Record not only bugs, but whether the run produced a memorable object, person, spell, return
  consequence, and decision that was not obvious at run start.

Score both machine invariants and human feel:

| Axis | Useful measures |
|---|---|
| Freedom | accepted/partially fulfilled intent rate; unsupported-capability reroutes; informative rejection rate |
| Expression | distinct outcome families; player-created traits reused later; casts involving local objects/materials |
| Richness | prop-name diversity and variance; peopleless stretches plus population clusters; wants/services encountered |
| Response | witnessed deeds that visibly return; rumor carrier hops; promises realized; consequences naming a cause |
| Coherence | atlas/dialogue/rumor agreement on rulers and places; raw-id leaks; contradictory facts |
| Pace | instant-action share; foreground model p50/p95; turns between authored action and visible response |

### 1. Give the world handles: regions, props, and context routing

The detailed specification is WS0–WS1 of
[PROCGEN_RICHNESS_PLAN.md](PROCGEN_RICHNESS_PLAN.md).

1. Load regions and traditions from content, with a canon-complete roster and deterministic map
   placement. **First slice implemented with this plan.**
2. Add regional combinatorial prop grammars and ensemble scenes. Specificity matters more than a
   mechanical hook on every object. **Implemented 2026-07-09.**
3. Split magic/dialogue context into a protected actor-and-hook lane plus a compact scenery lane,
   so lush clutter expands expression without hiding threats or important NPCs from the model.
   **Implemented 2026-07-09.**
4. Let a minority of props carry claim seeds, readings, reagents, services, routes, or magic
   anchors through existing components and consequences.

Acceptance: a ten-zone walk encounters at least 25 distinct prop names and one coherent ensemble;
every prop can be examined; a cast in the busiest zone still sees every living actor and the most
relevant scenery; same seed produces the same world.

### 2. Give people pressure: population shape and interiority

Implement WS2 before writing large amounts of dialogue copy.

**Implemented 2026-07-09:** the one-resident quota and inline regional switches are gone. Fourteen
population grammars now drive settlement/near/wild density, low-frequency clumping, name forges,
44 local archetypes, wants, knowledge, stats, wares, and services. All spawned social affordances
use the existing consequence and interaction lanes.

- Replace the one-resident quota with a settlement/road/wilderness density field. Empty stretches
  and crowds are both necessary texture.
- Load archetypes, name parts, habitat, wants, knowledge, services, and wares from regional data.
- Every notable generated person gets at least one reason to act: a want, service, stock, claim,
  allegiance, or relationship hook.
- Keep allegiance, organization, and personal bond independent.
- Route all changes through `spawn_entity`, `update_want`, `offer_service`, `offer_trade`, memory,
  bond, faction, and control consequences already in the grammar.

Acceptance: a wilderness-to-town transect contains peopleless zones, roadside outliers, and a
crowd; generated residents support talk/give/trade/service/recruit unchanged; at least one NPC
volunteers a locally grounded pressure that can become action.

### 3. Turn wants into playable journeys

Build quests as promise-driven hand-offs, never as a new quest state machine.

**Forward-momentum loop implemented 2026-07-12:** freeing any qualifying captive can now
transactionally generate and speak a seed-varying handoff assembled from real settlements,
regional contact identities, and content templates. The linked promise supplies a direct journal
objective, materializes the named contact at the exact destination, remains actionable until the
player speaks to that contact, then clears the source claim and produces another generated
handoff. Settlement journey-givers also volunteer their existing claim seeds through ordinary
dialogue. Follow-up templates now cover fetch, delivery, escort, threat, folk service, rumor
verification, and social leverage; their completion rules inspect authoritative state, route the
player back to the giver, satisfy the giver's want, record why, and apply a faction payoff. Threat
and leverage tasks accept multiple systemic outcomes instead of one privileged verb. Lio is the
first proving ground, not a special-case quest giver.

- Add data templates for fetch, delivery, escort, threat removal, folk service, rumor
  verification, and social leverage.
- Fill templates with real neighboring places, people, factions, items, and routes from the world
  roll.
- Use claims to bind promises; promises realize affordances; completion updates the NPC want and
  applies typed payoff consequences.
- Support several solutions where the existing grammar permits them. “Clear a threat” may mean
  kill, displace, recruit, trap, transform, or satisfy it; the authoritative completion rule should
  test resulting state rather than a single command verb.
- Record why the giver believes the task changed, so later dialogue sees an ordinary memory.

Acceptance: an agent can discover and finish one generated journey using player-facing
information only; the target exists before or is guaranteed by a bound promise; at least two
mechanically different solutions satisfy one general objective; the journal explains the chain.

### 4. Make geography resist and reward

Implement WS3 after the data vocabularies for props and people are stable enough to populate it.

- Roll realm territories, settlement footprints, district identities, roads, waystations, and
  landmarks from the seed.
- Cities span multiple zones. Districts change population, props, services, witnesses, control,
  and voice—not only their display name.
- Atlas output names current district, nearby settlements, road connections, borders, and known
  landmarks without exposing undiscovered tactical state.
- Discovered transport services eventually offer diegetic fast travel. Their routes, availability,
  elapsed time, price, and world-turn consequences belong to ordinary world state.
- Use one-zone interiors first. Add multi-zone buildings and dungeons only after town traversal
  is already interesting.
- Give the capital outer districts, an inner court, and a throne district so imperial defense
  resources have geography to inhabit.

Acceptance: crossing one town visits at least three distinct districts; roads reach the places
they claim; directions given by residents agree with the atlas; the capital is no longer an
accidental two-travel regicide without inventing a special “access meter.”

**Significant-interior slice implemented 2026-07-09:** all fourteen regional centers now bind
authored, culturally specific significant interiors, with separate palace and archive spaces in
the capital. Thresholds and exits are unified entities; zones lazily generate and persist; local
residents, semantic fixtures, terrain, exploration, followers, and promise realization use the
ordinary engine lanes. `enter`/`leave` consume one normal turn through the same GUI/CLI session
path without pretending a threshold crossing is elapsed world travel. Restricted sites accept
keys, permission flags, or open/forced/access tags that dialogue, force, or wild magic can create;
they are world-state problems rather than absolute plot locks. See
[SIGNIFICANT_INTERIORS.md](SIGNIFICANT_INTERIORS.md).

### 5. Close the world-reaction flywheel

Once the world supplies enough carriers and routes, deepen response rather than adding more raw
content.

- Make rumors prefer the road and settlement graph, while carriers prefer rumors relevant to
  their wants, roles, memories, and factions.
- Let selected faction moves create concrete patrols, notices, informants, aid, closures, or
  depleted absences through typed consequences. A reaction must spend resources and appear
  somewhere.
- Let gifts, rescues, betrayals, transformations, and witnessed magic write memories that later
  bond/dialogue decisions actually read.
- Ensure player-created traits and named magical effects can be routed back into future resolver
  context when locally relevant.
- Allow qualifying NPCs to spend bounded initiative approaching, interrupting, seeking, or
  following the player, with the triggering want, rumor, memory, promise, or deed kept legible.
- Add explicit cause text at the return point: who heard, who paid, who objected, which promise or
  deed this answers.

Acceptance: a significant public deed produces one visible local response and one plausible later
response without duplicate messages; laying low measurably changes available imperial pressure;
an NPC can approach, refuse, help, or leave for reasons assembled from ordinary state lanes.

**Road/carrier and initiative slice implemented 2026-07-12:** rumor propagation can cross a
regional boundary only when the physical place graph has a road between the two regions. Spread
history and deltas retain the road id/name, while player text names the route, listener, and the
listener's relevant want or nearest-carrier role. The bounded world-turn pump can also spend one
move bringing a local rumor-carrier or explicitly player-seeking objective contact one step closer;
the move is cooled down, audited, typed, and announces the precise want or memory that caused it.

### 6. Deepen expression where playtests pull

Do this alongside the world work in small tranches, driven by failed or boring casts rather than a
speculative operation list.

- Mine live audits for unsupported intentions, overly generic outcomes, repetitive costs, bad
  targets, and operations that validate but do not matter.
- Bias early tuning toward acceptance. Do not add power ceilings merely because an effect is
  extreme; tighten only after observed play shows that the freedom damages expression or the
  long-run game.
- Prefer schema repair, reference normalization, consequence composition, and capability-card
  guidance over phrase handlers.
- Make more costs durable and re-usable: vows, marks, altered items, debts, vulnerabilities,
  delayed releases, and witnessed obligations.
- Let spell echoes and charter forms grow into a practical tempo layer; protect wild casts as the
  memorable, risky authorship moments.
- Connect casting-performance results to power/wildness/cost without hiding unique mechanics in
  the GUI. The CLI supplies equivalent explicit scoring inputs.
- Promote semantic traits into mechanics only when the resolver proposes an existing typed
  operation that current context justifies.

Acceptance: live evals improve specificity and consequentiality without reducing validity;
repeated tactical needs do not require repeated long model calls; at least one local object,
relationship, promise, or prior spell materially changes a later resolution.

### 7. Build the long arc from the same machine

- Imperial defenses become finite resources attached to districts, officers, routes, and
  institutions.
- Access paths reuse existing play: drain defenses, earn legitimacy, bind a prophecy, recruit an
  insider, steal credentials, body-swap, exploit a service, or force entry.
- Coalitions emerge from faction standing, deeds, bonds, promises, and concrete aid, not a
  separate diplomacy campaign.
- The emperor remains a normal actor. Reaching him is the hard part; killing him uses ordinary
  validated consequences.
- Victory narration should surface the real path the run took and the unresolved cost of removing
  the marble peace.

Acceptance: at least three architecturally distinct routes reach the emperor using systems already
present in the opening regions; no finale-only mutation path exists; replay and the episode runner
can complete each route.

### 8. Present the world as richly as it simulates

- Keep ASCII as the first renderer but make region palette, ornament, sound, and layout reflect
  the content definitions.
- Prioritize interaction clarity: nearby verbs, target feedback, cause-bearing messages, district
  identity, remembered people/objects, and promise leads.
- Give human players progressive disclosure while CLI debug views retain perfect state.
- Make pending model work feel like casting rather than a stalled interface; charter and echo
  options should remain usable while appropriate.
- Add tiles only after the read-only view contract can describe the same information without
  renderer-owned rules.

Acceptance: a player can explain where they are, what nearby things invite, who currently cares,
what consequences are in motion, and what several plausible next actions are without consulting
debug state.

**Current-objective slice implemented 2026-07-12:** the shared `GameView` exposes a prioritized
objective card with kind, status, giver, destination, and a state-sensitive next step. Godot shows
that line in the persistent HUD, `inspect` prints the same next step, the journal distinguishes
active from truly completed generated contracts, and CLI JSON receives the same fields.

## Near-term implementation queue

This is the recommended order for the next changes. Each item lands playable and green.

1. **Regions as content and reachable geography — implemented 2026-07-09.** Load 14 regional
   definitions and 13 traditions from JSON; enforce the one-free-rival/three-conquered old-kingdom
   roll; deterministic seed-jittered placement; canon coverage tests; border narration through
   shared action results.
2. **Semantic prop grammar plus protected resolver context.** Data schema, deterministic
   composition, ensembles, density variation, actor/hook/scenery routing, inspect coverage.
   **Implemented 2026-07-09:** all fourteen regions have authored grammars; generation is
   zone-seed-derived; compact scenery remains targetable; actor and hook cards are protected.
3. **Population archetypes and density — implemented 2026-07-09.** Region data, name forge,
   habitat weights, zero-to-crowd distribution, wants/services/wares at spawn.
4. **Promise-driven generated journeys — contract loop implemented 2026-07-12.** Captive rescue
   and destination contacts form a provider-free, seed-varying spoken claim → objective →
   exact-place realization → state-based completion → return/payoff → next-objective chain.
   Settlement landmark/evidence journeys share the path. Seven follow-up families and alternate
   threat/social solutions are data-driven; durable claim tags provide contracts without a quest
   ledger.
5. **Settlement graph and three-district town — implemented 2026-07-09.** Every region now has a
   named 3×3 center, hamlets, a landmark, district terrain/sites/population, and connected roads.
6. **Road-aware rumor return — first route/carrier slice implemented 2026-07-12.** Cross-region
   spread requires a real named road and affects a concrete local carrier whose cause is visible.
7. **Significant one-zone interiors — implemented 2026-07-09.** One regional-center interior per
   culture plus distinct capital palace/archive; persistent linked snapshots, followers, soft
   access, ordinary turn semantics, resolver/dialogue/world-card context, and GUI/CLI parity.
8. **Capital access routes and defenses.** Censor Gate, Inner Court, Archive Quarter, palace, and
   archive now exist; make defense resources and alternate entry routes act on that geography.

## Slice definition of done

Every gameplay slice must satisfy all of these:

- It creates a reusable combination, not a handler for one story or phrase.
- A player can discover, use, and perceive it through ordinary play.
- GUI and CLI reach the same `GameSession` path and expose equivalent information.
- It works deterministically with providers disabled; model enrichment is optional.
- All mutations use the shared consequence apply point and roll back transactionally.
- Tests cover turn use, rejection, rollback, persistence/replay where durable, and seed stability
  where generated.
- At least one agent episode exercises the player-facing path rather than seeding debug state.
- Relevant architecture and subsystem docs change in the same patch.

## Explicit deferrals

- No weather, seasons, ecology simulation, or daily NPC schedules until geography and reaction
  already feel alive.
- No model calls on the deterministic generation path.
- No separate quest ledger or bespoke story scripting framework.
- No large bestiary before props, people, and objectives make encounters contextual.
- No multi-zone interiors before multi-zone settlements are fun.
- No full item-identification game until ordinary items are already tempting spell material.
- No bespoke finale systems; the opening-region machine must scale to the throne.

## Main risks and responses

| Risk | Response |
|---|---|
| More content makes prompts slower | Protect actors/hooks; compact scenery; measure provider tokens in cluttered zones |
| Data schemas ossify too early | Add the minimum fields for the next playable slice; keep C# fallbacks; version once a second content pack needs migration |
| Infinite map, repetitive play | Build one excellent transect and one excellent town before map scale |
| Ledgers grow without payoff | Every new writer names its reader and visible return path in the same change |
| Procedural output feels random | Derive from region, settlement, faction, history, and promise; make systems quote one rolled canon |
| Wild magic becomes valid but bland | Keep live specificity/consequence/surprise review beside correctness evals |
| The model becomes the pacing bottleneck | Continue instant lanes; no generation calls; route compact context; measure real CPU sessions |

The plan succeeds when a run can be remembered as a chain of authored particulars: *the scarf I
changed, the ferryman who noticed, the lie he told for me, the checkpoint that appeared because of
it, and the officer who was waiting there with my own rumor in her mouth.*
