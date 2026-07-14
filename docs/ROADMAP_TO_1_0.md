# Roadmap to 1.0 — From Prototype to Complete Game

Status: active product roadmap, written 2026-07-13 from a repository-wide review, the 2026-07-03
playtest report, and owner decisions recorded below.

How this document relates to the other plans:

- [GRAND_VISION.md](GRAND_VISION.md) owns intent — what the game is. It always wins on intent.
- [HOLISTIC_DEVELOPMENT_PLAN.md](HOLISTIC_DEVELOPMENT_PLAN.md) owns the current gameplay-loop
  program (freedom / expression / responsiveness). Its queue continues inside Milestones 0–2 here.
- [MATURITY_ROADMAP.md](MATURITY_ROADMAP.md) owns system depth relative to the parent prototype.
- [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) owns roadmap-to-code execution — shared seams,
  complexity control, work packages, and proof gates designed to maximize gameplay without
  disconnected machinery.
- **This document owns product completeness**: the ordered path from "impressive prototype" to a
  shipped game a stranger would place beside Caves of Qud or Dungeon Crawl: Stone Soup — not by
  imitating them, but by meeting the same bar of finish while staying Sorcerer.

When plans disagree about what to build next for the product, this document wins; on what the
game should be, GRAND_VISION wins.

## Owner decisions (2026-07-13)

These calibrate everything below.

1. **Canonical runs are 8–12 hours,** deliberate play, genuinely winnable. Winning stays hard;
   long greedy runs remain possible, but the tuned win path should not require 20 hours. (This
   revises the earlier ~20-hour note; "very hard to win" stands.)
2. **Two persistence modes ship at 1.0.** **Classic** is permadeath with save-and-exit suspension
   only; loading is never a retry path and death deletes the run save. **Roleplay** is the same
   simulation and difficulty with free save/load. It replaces the discarded checkpoint-restoration
   design. No meta-progression in either mode, ever. The chronicle records which mode a run used.
   Save design for both modes lands early (Milestone 1), because retrofitting persistence is the
   expensive kind of late change.
3. **Visual identity at 1.0 is illuminated ASCII plus portraits.** ASCII is the face of the game,
   polished to the Cogmind/Brogue tier — regional palettes, light, motion, cast choreography —
   with the existing portrait pipeline giving faces to people and moments. No tiles at 1.0; the
   renderer boundary keeps tiles possible later.
4. **Out-of-box LLM experience is guided local + BYOK cloud.** A first-run wizard installs or
   detects Ollama and pulls the floor model with progress UI; pasting a cloud API key is the
   quality upgrade; a no-LLM demo path guarantees the download is never a brick. True model
   bundling is evaluated post-EA, not promised.
5. **The score is bespoke.** Commissioned original music is an identity pillar, like Qud's
   soundtrack. Commissioning starts early (Milestone 1 timeframe) because music has the longest
   external lead time; integration matures through Milestone 3.
6. **Ordinary play is curiosity plus deterministic mastery.** Travel, inspectable cultural
   scenery, useful props, charter magic, exploration, relationships, and medium-infrequent combat
   carry the game between wild casts. Enemy patterns, intentions, ranges, and weaknesses are
   generous and learnable because the wild resolver supplies uncertainty elsewhere.
7. **Wild magic is allowed to be spectacular.** A fresh authored opportunity should usually appear
   at least every three minutes; very strong resolutions may solve major problems and answer with
   proportionate durable costs. Thirty seconds is the maximum experiential wait for the supported
   local floor model. Inventory is permissive; scarcity lives in desirable spell components and
   gear, not capacity chores.
8. **The slice includes a major old kingdom.** The target transect is Marble Containment Yard →
   Hollowmere Margin → Brall → Vigovian Capital. Brall's collaborative boasting about other
   people's deeds is the first full-density proof that cultural mechanics travel through shared
   systems.
9. **Before public Early Access, deletion outranks compatibility.** Break unreleased saves,
   transcripts, APIs, aliases, and schemas when simplification warrants it; regenerate fixtures
   and remove superseded paths. Public save migrations begin only when EA creates that promise.

## What the finished game is

The comparison to Caves of Qud and Dungeon Crawl: Stone Soup is a comparison of **product
confidence**, not a feature checklist. Sorcerer does not need their mutation catalog, fixed spell
schools, dungeon structure, hunger clocks, bestiary size, or decades of accumulated content. It
does need the same confidence that the rules will support a long run, the same density of
meaningful decisions, the same appetite for another seed after death, and the same absence of
prototype seams.

At 1.0, a player should be able to describe Sorcerer like this:

- **It is an improvisational roguelike, not a chat interface.** Most turns are immediate engine
  play: movement, positioning, stealth, combat, items, trade, talk, travel, and command. A wild
  cast is a high-attention authorship moment inside that game, not the only interesting button.
- **The world is a vocabulary.** People, bodies, doors, rumors, laws, promises, terrain, and
  ordinary objects can all become material for action. The local culture changes what is present
  and therefore what the player is likely to invent.
- **The world answers in actors and places.** The answer to a public spell is not `heat +2`; it is
  a witness's frightened memory, a distorted road rumor, a spent patrol, a warrant with the wrong
  body on it, a grateful stranger, or a promised site that later exists.
- **A build is a life, not a class.** Origin and stats begin it; charter forms, authored echoes,
  treasured items, bodies, curses, followers, favors, reputation, and promises make it particular.
  The player becomes more capable without filling an XP bar.
- **The campaign is systemic but not shapeless.** The Empire visibly escalates, the player creates
  a foothold, the regional conflict becomes a war, and the capital becomes reachable by several
  routes. The emperor is an ordinary entity at the end of an extraordinary accumulation of
  access, leverage, damage, and belief.
- **Failure belongs to the fiction.** Death is unfair only in the way a good roguelike can be
  unfair: dangerous, sometimes abrupt, but traceable to visible rules and choices rather than a
  provider failure or hidden prose decision. The chronicle turns the loss into an ending and the
  fresh world makes restart feel like reward.
- **The shipped application has no apology screen.** Installation, model setup, saving, provider
  failure, keybindings, settings, accessibility, and reporting are ordinary product flows. A
  player never needs the repository, a terminal, or a developer standing nearby.

### The experience in five scales

Sorcerer has to work at every scale below. A feature that improves one scale while making another
inert is not finished.

| Scale | Target experience | Required cadence |
|---|---|---|
| **Turn** | Read a threat or affordance, make one legible choice, receive immediate mechanical feedback | No model call for ordinary play; intent and result stay unambiguous |
| **Situation** (roughly 2–10 minutes) | Fight, evade, bargain, steal, rescue, transform, or otherwise author a local outcome | Usually one tempting wild-cast moment; several viable instant actions before and after it |
| **Journey** (roughly 15–45 minutes) | Carry a person, object, rumor, debt, or promise between concrete places and meet a changed answer | At least one prior consequence visibly returns through a named carrier or changed place |
| **Movement** (roughly 1–3 hours) | Establish a foothold, gain a distinctive capability, and change the regional balance | Power, pressure, and available routes all become visibly different |
| **Run** (8–12 hours) | Escape, make a life, wage a strange war, penetrate the capital, and end an emperor | The chronicle can reconstruct the actual path without inventing connective tissue |

This cadence is more important than raw map size. A hundred zones with no returning consequence
are less complete than twenty zones in which yesterday's scarf, witness, and promise all matter.

### The nested play loops

The **immediate loop** is: **notice → choose → resolve → read**. The map and entity cards expose
specific handles; the player chooses a deterministic verb or authors magic; the engine applies
typed consequences; presentation makes the tactical result and its cause clear.

The **world-response loop** is: **act → witness → record → circulate → respond → deliver**. Not
every act completes the whole chain, but every durable writer must have a plausible reader and
return point. A significant public act should normally produce one local answer now and one later
answer within one to three zone transitions. Slow promises can mature over hours, which gives the
fast responses contrast.

The **campaign loop** is: **read the world roll → choose leverage → gain capability → force an
imperial expenditure → exploit the opening → accept the consequence**. Weakening the Empire is not
a separate strategy layer. It happens through the same people, districts, roads, bodies, claims,
and spells used in moment-to-moment play.

The **many-run loop** is: **die or win → read the chronicle → see what this world made of you →
roll a new geopolitics and origin → discover the first meaningful difference quickly**. Memorials
may color the next world, but no power crosses that boundary.

### The canonical 8–12 hour arc

These are pacing movements, not chapters or gates. A sufficiently clever or reckless player may
skip, reverse, or collapse them. The tuned path should nevertheless make each movement legible.

| Movement | Approximate run time | Player experience | World answer | Proof of growth |
|---|---:|---|---|---|
| **Escape** | 0:00–1:00 | Improvise an exit from imperial custody; meet Lio or an equivalent pressure; learn that objects, witnesses, and promises matter | The first deed is attributed or remains suspicious; a patrol, rumor, bond, or promise stirs | One memorable authored outcome, one immediate hook, two future hooks |
| **Foothold** | 1:00–3:00 | Reach a living settlement; secure safety, income, transport, allies, a useful echo or charter form; decide whom to trust | The region recognizes the opening deed; the first warrant or aid arrives; an NPC acts on a want | A repeatable tactical rhythm and at least two run-defining assets or relationships |
| **War** | 3:00–8:00 | Work across roads and factions; build or betray a network; interdict supply, expose an official, free a place, fulfill or weaponize a promise | Named hunters, closures, counter-rumors, absences, aid, and faction expenditures reshape routes | At least one architecturally distinct capital route becomes credible; the player's legend changes available play |
| **Reach** | 8:00–12:00 | Enter the capital as a campaign: outer pressure, an institutional weakness, inner access, then the emperor | The Empire spends what remains, allies and enemies cash in old facts, unresolved costs arrive | The emperor is reachable through force, infiltration/identity, or social/prophetic leverage and dies by ordinary rules |
| **Reckoning** | minutes, not another act | Read victory or death, the unresolved cost, and the world's version of the sorcerer | Chronicle, epitaphs, unkept futures, memorial seed | One input starts a materially fresh world |

### Progression without XP

The game still needs a strong power curve. "No XP bar" cannot mean "no progression." Each run
uses several interoperable progression lanes:

- **Repertoire:** charter forms cover reliable needs; accepted casts can become named echoes;
  repetition fatigue and drift keep fresh improvisation valuable.
- **Material power:** equipment, foci, reagents, treasured objects, money, transport, and known
  services increase what the player can attempt and what costs they can survive.
- **Embodied power:** Vigor follows the body; rare body changes or soul exchanges can radically
  alter a build. Attunement, Composure, mana, legend, and authorship stay with the soul.
- **Social power:** bonds, followers, debts, organization access, faction standing, safehouses,
  and trusted specialists create options no personal stat can replace.
- **World position:** known routes, weakened defenses, liberated or controlled places, fulfilled
  promises, stolen credentials, and true information are campaign power.
- **Scars:** curses, vulnerabilities, marks, oaths, enemies, and terrible costs remove some
  options while creating others. A stronger sorcerer should often be a stranger, more constrained
  one too.

By the end of Foothold, the player must be obviously more capable than at escape. By Reach, their
power should be partly personal and partly embedded in the world they changed. Rare permanent
within-run stat changes are allowed through ordinary typed consequences; a universal experience
currency or level ladder is not.

## The bar: eight product pillars

"Complete game" is not a feeling; it is these eight statements becoming true. Each is testable,
and the 1.0 definition of done at the bottom is exactly this list at world scale.

- **P1 — Setup vanishes.** A stranger on a fresh Windows machine goes from itch.io download to
  their first accepted wild spell in under fifteen minutes with no manual configuration, or
  bounces into a playable no-LLM demo rather than a broken screen.
- **P2 — The first hour teaches by play.** The opening realizes
  [OPENING_SEQUENCE.md](OPENING_SEQUENCE.md): improvising in language, objects as spell material,
  witnesses, promises, and the Empire's character — learned by doing, never by modal tutorial.
- **P3 — The deterministic minute stands on its own.** Movement, combat, stealth, trade, and
  travel are a good turn-based game with zero model calls. The model makes play transcendent;
  it never carries a boring skeleton.
- **P4 — A run has an arc.** Escape → foothold → war → reach: pressure visibly escalates, power
  visibly grows, the capital is a campaign rather than a walk, and a deliberate player wins in
  8–12 hours.
- **P5 — Death is a good ending.** Killer-split death screens, the chronicle as the narrator's
  showpiece, and a fast re-roll into a legibly fresh world
  ([DEATH_AND_CHRONICLE.md](DEATH_AND_CHRONICLE.md)). Losing produces a story worth sharing.
- **P6 — It looks and sounds like itself.** Illuminated ASCII, portraits, a bespoke score, and a
  wild-magic sound identity. Thirty seconds of footage is recognizably Sorcerer and recognizably
  finished.
- **P7 — It runs like a product.** Latency budgets hold on the local floor model; a 20-hour run
  survives without babysitting; saves are safe; provider failures degrade gracefully; settings,
  keybindings, and accessibility exist in the UI, not in config files.
- **P8 — Runs are yours.** Origin, tradition, echo repertoire, treasured items, followers, and
  legend make two runs feel like two different sorcerers' lives — build identity without XP bars,
  per [CHARACTER_AND_STATS.md](CHARACTER_AND_STATS.md) and [SPELL_ECHOES.md](SPELL_ECHOES.md).

## Where we are (2026-07-13, honest)

The engine is far ahead of the product. One authoritative session drives GUI and CLI; wild magic
runs the full untrusted-proposal pipeline; fourteen data-loaded regions with props, population,
districts, roads, journeys, interiors, factions, rumors, promises, and bonds are live; save/load,
replay, chronicles, and a killable emperor exist; 766 passing tests and an agent playtest harness
guard it. The 2026-07-03 playtest scored authorship and wonder 4–5/5. That is an extraordinary
foundation, and none of it is the gap.

| Pillar | State | The gap |
|---|---|---|
| P1 Setup | D | Assumes the player can configure Ollama or paste a key; no wizard, no demo mode |
| P2 First hour | C+ | Opening has good bones and the journey loop; not yet a designed, teaching first hour |
| P3 Deterministic minute | C | Combat "steady, not deeply tested" (3/5); stealth statuses don't affect witnessing at all — the flagged top design gap; mischief scored lowest (2–3/5) |
| P4 Run arc | C− | Win path exists but thin; capital defenses just gaining geography; hours 2–8 are an unpaced open field |
| P5 Death | C | Victory/defeat/chronicles exist as slices; death screens, chronicle showpiece, and next-run seeding are designs on paper |
| P6 Identity | D+ | ASCII functional, UiTheme/TerrainStyles exist, portrait plumbing exists; no lighting/motion pass, no audio at all beyond the bone-song synth |
| P7 Product | C− | Telemetry and provider adapters are real; latency still the top pain; no settings UI, no soak validation, no failure-mode UX |
| P8 Build identity | B− | Origins, stats, echoes, treasured items, followers all live; not yet tuned so runs *feel* like different builds |

Systems grade: A−. Product grade: D+. This roadmap is the closing of that spread.

## The shape: one excellent transect, then the world

We do not widen until a narrow path is excellent end-to-end. The **vertical slice** is:

> Under a pinned reference seed: the start region, Hollowmere, one major old kingdom, and the capital —
> a complete, winnable, classic-mode run of roughly 6–8 hours in slice form — with all eight
> pillars at shippable quality inside that footprint.

The reference transect is **Marble Containment Yard → Hollowmere Margin → Brall → Vigovian
Capital**. The opening, Lio handoff, and reed-memory frontier retain their current line; Brall adds
the first major old kingdom and proves witnessed deeds can become collaborative tall tales,
relationships, followers, and campaign leverage through the ordinary rumor chain. Pin the exact
seed and route in Milestone 0.

Other regions stay reachable and generated-thin. Everything built for the slice must
be general systems plus per-region data — "Lio is a proving ground, not a special case" remains
the law — so that widening later (Milestone 6) is authoring, not engineering. Sections below end
with a **Later breadth** note marking what deliberately stays narrow for now.

The slice run is shorter than the final 8–12 hour target because it compresses the regional War
movement into Hollowmere and one Bralli route. It must still contain the complete emotional and mechanical
arc; Milestone 6 widens choice and replay variety without padding the critical path.

### Vertical-slice content contract

Counts below are floors for coverage, not quotas to fill with interchangeable content. A smaller
set of things that cross-read one another is better than a larger catalog of isolated things.

| Surface | Slice floor | What it proves |
|---|---|---|
| **Geography** | The promise-rich imperial opening; dense Hollowmere districts/sites/road corridor; a Bralli hold or public hall plus road/harbor and smaller site; the capital's outer, institutional, inner, and throne geography; significant interiors on the route | Town traversal, travel, borders, interiors, a major kingdom, and the capital all use one place graph and ordinary access state |
| **Cultures and factions** | Hollowmere frontier identity, full-density Brall, and Vigovia; one further generated-thin culture in a world-tour proof; local loyalist, rival, civilian, and imperial pressures through existing faction lanes | Color-versus-marble is playable across distinct cultures, and Bralli tale escalation proves cultural mechanics compose without culture-specific engine code |
| **People** | Enough generated and authored roles to guarantee: ally/follower, merchant, folk practitioner, official, rumor carrier, journey giver, claimant, hunter, and conflicted civilian; each notable person has a want and at least one useful state lane | Talk, gifts, services, recruitment, memory, bonds, rumor, and initiative all hand off |
| **Encounters** | Roughly 10–14 specific enemy archetypes across bruiser, ranged, caster, leader, beast, investigator, and elite/hunter roles; no generic rat/wolf filler; each has learnable intent, pattern, weakness, and counter; noncombat resolution is representative | The zero-model tactics reward knowledge and social/terrain play without constant combat or a huge bestiary |
| **Magic vocabulary** | Representative object, terrain, movement, summoning, transformation, body/soul, social, memory, promise, delayed, and cost-bearing outcomes exercised in real play; four casting minigames remain optional latency masks | The general consequence grammar expresses the fantasy instead of a phrase library |
| **Objectives** | The current generated journey families exercised across at least one connected chain; one short promise payoff and one multi-hour payoff; at least one objective accepts two mechanically different solutions | Wants/claims/promises create direction without a quest engine |
| **Economy** | Money enters through trade, reward, theft, or service and leaves through tempting spell components/gear, transport, rest, information, bribery/access, and recovery; carrying remains permissive | Scarcity changes which semantic materials the player spends without becoming backpack maintenance |
| **Build identity** | At least three tuned origins; useful charter choices; strong wild resolutions; recurring within-run stat growth including rare components that magic can turn permanent; transformative bodies; up to roughly ten manageable followers | Two slice runs produce different verbs, bodies, relationships, and risks rather than inventory chores or flavor-only builds |
| **Imperial campaign** | Notices, patrols, warrants, a named hunter or equivalent elite answer, finite defenses on real capital geography, and at least three tested reach routes: force/resource depletion, identity/infiltration, and alliance/promise/legitimacy | Escalation is visible, spendable, and answerable through existing systems |
| **Endings** | Imperial and wild death treatments; victory; deterministic chronicle assembly; immediate classic restart; Classic suspension semantics; free Roleplay save/load | A run is a complete object whether it ends well or badly, under either save authority |
| **Presentation and product** | Three coherent palette/ornament/audio identities, portraits for slice notables, settings/accessibility, provider wizard, demo path, resilient saves, and distributable Windows build | The slice can be judged as a product, not excused as an internal build |

### The content-unit rule

"Slice density" has a precise meaning. A new content unit is not complete when it merely spawns.

- A **place** needs a visual/sonic identity, physical affordances, people or a deliberate absence,
  at least one reason to return, and shared atlas/dialogue/resolver truth.
- A **notable NPC** needs a want, knowledge boundary, faction/allegiance posture, memory/bond
  surface, and some way to affect play: service, trade, claim, companionship, opposition, or
  initiative.
- An **enemy archetype** needs a readable intent, a positional question, at least one systemic
  counter besides damage, and a reason this culture or institution fields it.
- An **item or prop family** needs inspectable specificity and at least two plausible uses among
  equipment, trade, spell focus, reagent, target, gift, evidence, reading, promise anchor, or
  access.
- A **journey pattern** needs player-facing discovery, an authoritative target, more than one
  solution where the grammar allows it, a visible change on completion, and a later reader for
  the result.
- A **reaction** needs a cause, a carrier, a resource or rule that paid for it, a player-visible
  manifestation, and a way to answer or exploit it.

This rule is the handoff from vertical slice to breadth. Milestone 6 may create hundreds of data
rows, but every row should inherit one of these complete shapes.

### Vertical-slice release gates

The slice is excellent only when all four proofs hold:

1. **Complete-run proof:** a human can start cold, win or die, read a faithful chronicle, and begin
   again without a developer intervention.
2. **Zero-model proof:** with providers disabled, movement, combat, stealth, travel, economy,
   charter/echo play, world reaction, and the campaign remain a coherent roguelike. The demo need
   not fulfill the free-form magic promise, but it must be genuinely playable.
3. **Live-model proof:** on the floor model, local objects and prior facts regularly shape casts;
   valid-but-boring, technical-failure, rejection, and latency rates meet the measured budgets;
   provider death cannot corrupt or strand the run.
4. **Second-run proof:** a tester who completed or lost one run voluntarily starts another and
   encounters a meaningful difference in the first twenty minutes.

## Milestones

Ordered by dependency. Each ends playable and green under the existing slice definition of done
in [HOLISTIC_DEVELOPMENT_PLAN.md](HOLISTIC_DEVELOPMENT_PLAN.md). Milestones 3 and 4 have long
lead items (score commissioning, wizard platform testing) that should *start* during 1–2.

A milestone is not complete because its feature list merged. Its exit evidence is a small
**milestone packet** committed with the work: automated results, the relevant agent episode,
save/replay coverage, measured latency or soak data where applicable, one human playtest summary,
and the updated feel scorecard. Green tests without player-path evidence prove an engine change,
not product progress.

### Milestone 0 — Close the current program (in flight)

Finish what is already queued and flagged before adding anything new.

- Capital access routes and defenses acting on real geography (holistic queue item 8).
- The stealth/witness design gap — statuses like concealment must gate `WitnessesOf` and deed
  attribution; this is the playtest's top recommendation and the unlock for the mischief pillar.
- Cast-rejection legibility: "no target selected" and kin must say *why* (missing vs. out of
  range vs. needs explicit target) — repeated finding across three sessions.
- Latency sprint remainder from the 2026-07 plan: dialogue tiering, provider UX groundwork,
  telemetry consolidation.
- Establish the roadmap baseline: one pinned-seed slice transcript, current p50/p95 by action
  class on the floor model, current full-run blocker list, and a ten-minute human combat/stealth
  recording. Later milestones compare against this evidence instead of memory.

**Exit:** the playtest report's "what's next" list is empty; the capital cannot be accidentally
regicided; a hidden player is mechanically hidden; every later pillar has a reproducible baseline.

### Milestone 1 — A complete run (structure)

Make the shape of the final 8–12 hour arc real before making it dense. The reference transect
deliberately compresses that shape into a 6–8 hour run for iteration; final breadth restores the
full target through optionality, not filler. Design work here should produce a short RUN_ARC.md
companion doc; the roadmap only fixes the shape.

**Player-visible result:** the game can be finished for the right reasons. It may still look and
feel like a prototype between major beats, but escape, growth, escalation, capital access, victory,
death, and restart form one understandable arc.

- **Four movements, tuned:** Escape (the opening hour) → Foothold (first region: allies, echoes,
  income, first imperial answer) → War (multi-region faction play, defenses drained or bypassed)
  → Reach (the capital campaign). Pacing comes from tuning imperial heat, defense pools, and
  travel costs — not from gates or chapter breaks.
- **Three reach routes proven:** force/resource depletion, identity or body-based infiltration,
  and alliance/promise/legitimacy. These are test routes through common state, not three scripted
  quest lines; mixed and stranger solutions remain possible.
- **Escalation made visible:** the Censorate ladder (notices → patrols → warrants → named hunters)
  as the player-facing clock of the war, per the existing faction-expenditure model.
- **Progression curve v1:** a run-level capability inventory shows when the player gained a
  charter form, echo, treasured focus, follower/service, access, route, or lasting transformation.
  By hour three at least two of those lanes have changed how the player solves problems.
- **Both persistence modes:** Classic permadeath with a consumed save-and-exit suspension; Roleplay
  free save/load; the chronicle records the mode. Same difficulty, same world rules, one serializer,
  no checkpoint restoration or parallel simulation.
- **Death-to-new-run flow v1:** killer-split death screens, chronicle assembled from ledgers,
  one-command re-roll, prior chronicle seeding the next world's rumor layer (commemorate, never
  empower).
- **Difficulty envelope v1:** origins as the difficulty surface (a forgiving origin and a cruel
  one), not a global slider.
- **Economy pass v1:** money must matter across an arc — bribes, transport, lodging, reagents,
  and information as recurring sinks over the existing wares/buy/sell slice.
- **Run-state legibility:** the journal and standing views answer four questions without debug
  state: what am I trying, who currently cares, how is the Empire answering, and what plausible
  route toward the capital have I uncovered?

**Exit:** an agent and a human each complete a classic reference-transect win in 6–8 hours; a
Roleplay save/load run can continue to victory; each of the three reach routes completes in replay;
the chronicle names the route actually taken; both death screen families appear in play; Classic
save/exit/resume and Roleplay save/load work at every movement. Milestone 6 revalidates the 8–12
hour envelope after breadth.
**Later breadth:** more origins, more victory framings, post-1.0 conducts/challenge modes.

### Milestone 2 — The slice made dense (game feel)

The holistic plan's loop — notice an affordance, author an action, meet the answer — at full
density along the transect, plus the deterministic game's own depth.

**Player-visible result:** ten consecutive minutes are fun without novelty credit, and a full run
contains recurring decisions rather than long connective stretches between magical anecdotes.

- **Combat depth pass (P3):** enemy roles (bruiser, ranged, caster, leader, beast), telegraphed
  intents, positioning that matters with the existing terrain reactions, and charter/echo tempo
  between wild casts. Roughly 10–14 enemy archetypes across the slice — contextual, per the
  standing "no large bestiary" deferral.
- **Mischief pillar (P3):** on the Milestone 0 stealth foundation — disguise, distraction,
  evidence, framing; guards who investigate; crimes attributed to the body worn. Suspicion must
  have readable intermediate states and at least one way to redirect, erase, or exploit it.
- **Encounter grammar:** author reusable encounter ingredients rather than rooms: patrol plus
  civilian witnesses, guarded threshold plus alternate credentials, rival wants around one
  object, environmental danger plus valuable material. The same ingredients recombine across
  roads, settlements, interiors, and the capital.
- **The teaching opening (P2):** OPENING_SEQUENCE realized as a dense proving ground; diegetic
  instruction (Lio suggests, permits demonstrate charter forms, notices demonstrate reading);
  the player leaves with one authored story and two hooks, measured by new-player tests.
- **Middle-game campaign texture (P4):** the War movement gets its verbs along the transect —
  safehouses, supply interdiction, informant networks, liberating or losing settlements —
  expressed through existing faction resources, journeys, and promises, never a mission system.
- **Journey and reaction density:** the transect meets every acceptance bar already written in
  the holistic plan (props, population, journeys, road-borne rumor returns, NPC initiative).
- **Social play as tactics:** gifts, leverage, services, surrender, recruitment, betrayal, and
  body identity solve representative pressures through authoritative state. Dialogue can enrich
  or interpret, but no critical solution depends on the model volunteering the right sentence.
- **Wild-magic quality pass:** expand costs as story generators, complete the application of cast
  performance, test echo drift/fatigue, mine boring outcomes, and ensure local objects, prior
  traits, promises, or relationships materially affect later resolutions.
- **Balance and recovery:** tune damage, healing, mana, rest, travel, money, threat response, and
  retreat so danger bites without turning the run into attrition management. A losing player can
  usually name the warning they ignored.

**Exit:** feel scoreboard ≥4/5 on authorship, wonder, mischief, tactical, legibility, and pacing
within the slice across at least three human testers; a documentation-blind new player reaches the
neighboring region and can explain afterward why the world did what it did; two different slice
runs produce materially different repertoires, relationships, and capital routes.
**Later breadth:** region-specific enemy families, organization play, multi-zone interiors.

### Milestone 3 — It looks and sounds like itself (presentation)

Presentation is not decoration here; legibility is a feature, and juice is legibility with taste.

**Player-visible result:** the same systems now communicate through image, motion, sound, and
layout with enough coherence that raw play footage needs no explanation or prototype disclaimer.

- **Illuminated ASCII:** per-region palettes and ornament drawn from content definitions; light
  and ambient tinting; cast choreography (glyph and color motion when wild magic lands); impact,
  status, and promise-realization effects; motion restraint settings from day one. Effects must
  derive from state deltas and read-only views, never renderer-owned rules.
- **Portraits:** the existing pipeline goes from plumbing to coverage — notable NPCs, followers,
  officials on warrants, the emperor, chronicle epitaph faces — with a consistent style bible.
- **Sound identity:** full SFX foundation (UI, combat, movement, doors, coin); per-region ambient
  beds ("color under marble" audible); a distinct wild-magic sound language where bigger casts
  earn stranger sound.
- **The bespoke score, integrated:** title, the Empire's marble theme, two to three regional
  motifs, death/chronicle music — commissioned during Milestones 1–2, woven in here.
- **UI product pass:** real main menu; settings for video, audio, input, providers, and
  accessibility (font scale, colorblind-safe palettes, reduced motion) in the GUI; the
  keybindings system surfaced; an in-game manual and command reference. Targeting, nearby
  interactions, pending consequences, objectives, faction pressure, and provider state receive a
  clear visual hierarchy without turning the screen into a dashboard.
- **Whole-run screens:** character creation, loading, transition/travel, settlement rest,
  Classic suspension/continue, Roleplay save/load, victory, death, chronicle, and restart all
  receive the same art direction; no high-frequency flow falls back to debug presentation.

**Exit:** the trailer test — thirty seconds of raw footage reads as a distinctive finished game;
muting the game feels like losing half of it; a settings-only player never edits a file.
**Later breadth:** full regional motif coverage, more portrait variety, ambient VO experiments.

### Milestone 4 — Anyone can play it (product)

The unique product problem no roguelike peer has: the game needs a model, and the player must
never feel that as friction.

**Player-visible result:** a Windows player with no development tools can install, configure,
play, suspend, recover, update, and report the game without leaving its UI.

- **First-run wizard:** detect or install Ollama; pull the floor model with progress and disk
  math; validate a BYOK cloud key (Anthropic, Gemini, OpenAI-compatible) as the quality upgrade
  with a plain cost note; a no-LLM demo mode (charter magic, echoes, deterministic world) so the
  download always plays something.
- **Provider trust:** secrets are stored through an OS-appropriate secure path where available,
  prompts sent to cloud providers are explained plainly, telemetry is opt-in, and the player can
  test, switch, or disable a provider without risking the run.
- **Latency as a shipped budget:** per-action-class p50/p95 targets on the reference floor model,
  enforced in CI via the telemetry lane; the casting UI treats waiting as ritual (choreography,
  interruptibility), and charter/echo/dialogue-tier lanes keep the game moving.
- **Resilience:** provider outages and timeouts degrade to instant lanes with the pending cast
  preserved; autosave with rotating backups; corruption recovery; a 20-hour soak episode with
  flat memory and bounded save size in CI.
- **Feedback loop:** opt-in telemetry (latency, rejection rates, run length, death causes) and
  an in-game report path that bundles the transcript.
- **Distribution discipline:** reproducible export, embedded content validation, version display,
  install/uninstall behavior, update notes, save-location documentation, and a clean-machine smoke
  matrix. The shipped build contains no Python/runtime/repository assumptions.

**Exit:** P1 passes on a fresh Windows machine cold; killing the provider mid-cast loses nothing
but time; the soak episode is green in CI; an update from the prior public schema preserves a
mid-run save and its chronicle.
**Later breadth:** evaluated model bundling, macOS/Linux, Steam packaging.

### Milestone 5 — Early Access on itch.io

Ship the slice-plus-thin-world honestly labeled, and turn players into the playtest lane.

- Closed alpha (chronicle-sharing friends round) → public itch.io Early Access.
- Store presence built on the Milestone 3 identity: trailer, screenshots, the pitch ("you can do
  anything, and it actually matters"), demo mode as the free taste.
- EA cadence promise: one meaty update per cycle, alternating breadth (new region to slice
  density) and depth (systems), chosen partly from telemetry and chronicles players share.
- Save compatibility discipline begins at EA: schema migrations, never wipes.
- Publish an honest support contract: reference hardware/model, known provider caveats, save and
  privacy behavior, expected run length, Classic versus Roleplay, and what "generated-thin"
  means for regions outside the slice.
- Treat chronicles and transcripts as qualitative evidence, not only marketing. Every update
  should name the observed player problem it addresses and re-run the four slice proofs.

**Exit:** stable EA build with real strangers playing; the retention signals that matter — most
players finish the first hour, a healthy share starts a second run after their first death, save
migrations survive one real update, and support load is sustainable enough to widen safely.

### Milestone 6 — Breadth to 1.0

The widening the whole plan deferred, now stamped from a proven template. This is authoring and
tuning at scale, not new engineering; if it demands new engine features, the slice was declared
excellent too early.

**Player-visible result:** the world stops feeling like one campaign with optional outskirts. A
new seed changes cultures, routes, allies, dangers, magic vocabulary, and political opportunity
soon enough to support long-term replay.

- All fourteen regions to slice density: props, journeys, enemy families, interiors, dialogue
  knowledge, lore, palettes, ambient beds, and motifs per region.
- The full map at the promised scale — breadth as optional space and replay variety, not as
  required distance; the 8–12 hour win path holds.
- Bestiary and organization breadth; multi-zone significant interiors and the first true
  dungeons; journey families beyond the current seven; minigame coverage growing from four
  toward one per major tradition.
- Score and portrait coverage completed; chronicle-sharing polished (the shareable morgue file
  as marketing engine, the DCSS lesson).
- Modding surface per [CONTENT_AND_MODDING.md](CONTENT_AND_MODDING.md) if EA demand justifies it.
- Widen in **region packs**, each required to pass the content-unit rule and a short culture
  transect before the next begins. Every pack must introduce a new combination of existing
  systems, not merely new nouns and palettes.
- Rebalance the critical path after every two region packs. More world should add optionality and
  replay variety, not silently push the canonical win beyond 12 hours.

**Exit:** every region passes its transect; the pinned full-world agents and varied human seeds
still finish in the target envelope; no region requires a private rules path; a stranger can sink
a hundred hours into the game and two runs do not rhyme.

### Milestone 7 — 1.0 release candidate

Stop adding systems. Turn the broad game into a build the project can support after launch.

**Player-visible result:** there is no remaining distinction between "working as designed" and
"ready to buy." The first-run path, the hundredth-hour edge cases, and everything between them
belong to the same stable product.

- Content and schema freeze; only fixes, tuning, accessibility, performance, compatibility,
  presentation polish, and text corrections enter the candidate branch.
- Full matrix: clean install, offline/demo, local floor model, each supported API family,
  provider loss/recovery, Classic/Roleplay, save/quit/resume/load, update migration, victory, every
  death family, chronicle/restart, common resolutions and aspect ratios, keyboard-only play,
  reduced motion, and color-safe palettes.
- Long-run confidence: seeded agent populations across origins and reach routes; repeated
  20-hour soaks; save-size and memory ceilings; replay/materialization checks; no unbounded
  ledger, world-turn, trigger, or background-job growth.
- Balance and fairness lock: death-cause distribution reviewed; opening completion and second-run
  rates healthy; no single origin, echo, body, faction route, or economy exploit collapses the
  game; no technical failure consumes a turn or destroys state.
- Release operations: versioned backup and rollback plan, crash/report triage, credits and
  licenses, privacy and provider disclosures, store assets, soundtrack delivery, support
  documentation, and the final shareable chronicle format.

**Exit:** the eight pillars are green at world scale; no open ship-blocking defects; two release
candidates survive the full matrix without a save-format change. This is the 1.0 definition of
done, not a calendar date.

## Continuous tracks (every milestone, never a milestone)

- **Resolver quality first:** specificity/consequence/surprise evals beside correctness; the
  boring-but-valid rate stays the first-order metric of the whole game.
- **Latency vigilance:** the binding constraint on all design; budgets re-measured every slice on
  the floor model, per the standing product direction.
- **Agent playtesting:** every milestone adds episodes exercising its exit criteria through the
  player-facing path; the feel scoreboard runs after every major slice.
- **Docs in the same patch;** GUI/CLI parity in the same change; the deterministic skeleton
  stands with zero model calls, forever.
- **Save/replay discipline:** every new durable lane gets round-trip, migration, transcript
  materialization, and bounded-growth consideration before it is considered finished.
- **Accessibility from first presentation:** keyboard-only and neutral-cast paths remain complete;
  motion, color, font, and audio alternatives are designed with the effect, not patched in after.
- **Content validation:** data schemas, ids, references, embedded assets, voice constraints, and
  seeded determinism are validated automatically as breadth grows.

## Production operating system

This roadmap is deliberately ordered but not dated. Calendar estimates depend on team size,
contracting budget, and the measured pace of content authoring. Milestone gates are the reliable
unit of planning.

### Workstreams and dependencies

| Workstream | Owns | Critical dependency |
|---|---|---|
| **Run spine** | pacing, progression, economy, Empire campaign, death/victory/restart | Milestone 0 geography and witness correctness |
| **Deterministic play** | combat, stealth, AI, items, charter/echo tempo, encounter grammar | Must be fun before presentation can disguise nothing |
| **Wild magic and response** | resolver quality, costs, context, latency, promises, reactions, narration | Must remain engine-authoritative and measurable on the floor model |
| **Presentation** | illuminated ASCII, portraits, UI, SFX, ambience, score integration | Consumes read-only views and deltas from proven play |
| **Product/platform** | onboarding, providers, saves, packaging, settings, accessibility, reporting | Prototyped during Milestones 1–2; gated after slice feel |
| **Content production** | region packs, people, items/props, enemies, journeys, interiors, lore, audio/visual data | Starts at scale only after the content-unit template passes Milestone 2 |

The run spine, resolver/latency work, first-run technical prototypes, and external score can advance
in parallel. Large-scale region authoring cannot. If breadth begins before the slice template is
stable, every design correction becomes fourteen corrections.

### Priority rule for the next task

When several tasks are available, choose in this order:

1. A blocker to completing, saving, resuming, dying, winning, or restarting a run.
2. A repeated source of boredom, confusion, technical failure, or waiting in ordinary play.
3. A missing hand-off that makes an existing consequence invisible or inert.
4. A slice-density gap required by the current milestone's exit.
5. Presentation and content that amplify a proven interaction.
6. A new system or content category.

Any proposal below line 4 must name the higher-priority problem it displaces. "This would be
cool" is welcome in the idea bank, not sufficient for the active milestone.

### Evidence ladder

Different tools answer different questions; no single green lane substitutes for the others.

| Evidence | Question answered | Cadence |
|---|---|---|
| Unit/integration/property tests | Is the rule authoritative, transactional, deterministic, and serializable? | Every change |
| Mock-provider CLI episode | Can the real player-facing backend complete the flow quickly and reproducibly? | Every gameplay slice |
| Pinned live-provider episode | Does the floor model produce specific, consequential, valid magic within budget? | Every resolver/context change and milestone gate |
| Human ten-minute loop test | Is the ordinary minute legible and enjoyable? | At least weekly during Milestones 1–3 |
| Documentation-blind first-hour test | Does play teach the promise without developer explanation? | Milestones 2–5 |
| Full-run human test | Does pacing, identity, escalation, death/victory, and restart hold together? | Every milestone from 1 onward |
| Clean-machine and failure matrix | Can a stranger actually own and keep playing the product? | Milestones 4, 5, and 7 |
| Long soak and agent population | Does state stay bounded and do varied builds/routes remain viable? | Continuous after Milestone 1; release gate in 4 and 7 |

### Product telemetry and feel scorecard

Telemetry is opt-in and aggregate. Raw player prose or provider keys are never silently collected.
Transcripts are attached only when a player explicitly submits them.

Track enough to answer these product questions:

- **Activation:** wizard completion, demo fallback, time to first accepted wild cast.
- **First hour:** escape route used, opening survival, first promise reached, first-region arrival,
  points of confusion, and whether the player can state why a consequence happened.
- **Ordinary play:** instant-action share, encounter duration, retreat/surrender/avoidance use,
  damage and recovery pressure, economy sources/sinks, and idle waiting.
- **Magic:** p50/p95 by provider and action class, technical-failure and intentional-rejection
  rates, boring-but-valid review rate, local-anchor use, cost-family variety, echo share, and fresh
  cast variety.
- **Responsiveness:** significant deeds with a visible local return, turns/transitions to later
  return, named carriers, promise realization, and orphaned ledger writes.
- **Run arc:** time in each movement, capital routes discovered/used, death causes, victory time,
  origin/build diversity, and save/resume success.
- **Many-run appetite:** chronicle completion/share, time to restart, and second-run start.
- **Reliability:** crashes, provider recoveries, save recovery, migration success, memory/save
  growth, and background queue bounds.

Milestone 0 records baselines; each later milestone writes its target thresholds in the relevant
test or playtest plan once measurement makes the number honest. Do not optimize retention by
weakening classic mode, hiding costs, flattening wildness, or adding meta-progression.

### Scope-change test

Before adding a roadmap feature, answer:

1. Which pillar and player decision does it improve?
2. Can it be expressed by, or cleanly extend, the shared entity/consequence/world-turn model?
3. How will a player discover it, perceive its result, and answer it?
4. How will the CLI and an unattended episode exercise it?
5. What is the smallest slice that proves it, and what is explicitly deferred?
6. What current milestone work is removed if this enters scope?

If those answers are weak, deepen an existing interaction instead.

## Risks

Risks need observable tripwires and scope-preserving fallbacks, not only optimism.

| Risk | Tripwire | Response | Fallback without betraying the game |
|---|---|---|---|
| **Floor-model latency poisons authorship** | p95 exceeds 30 seconds; players avoid fresh casts or spend encounters waiting | Instant charter/echo lanes, dialogue tiering, compact routed context, cancellation/retry, budgets in CI; casting UI treats waiting as ritual | Raise the documented floor model or narrow supported local hardware; never fake a successful cast or move simulation into prose |
| **Magic is valid but boring** | High acceptance with low specificity, surprise, local-anchor use, or downstream consequence | Curated live evals, boring-outcome audits, cost palette, context repair, capability guidance, model floor review | Ship fewer supported providers/models with a higher quality bar; do not add prompt-phrase handlers |
| **LLM setup bounces players** | Wizard abandonment or model-pull failure prevents first play | Detect/install/pull with progress and disk math; BYOK validation; same-build demo path | Let demo start immediately and offer model setup later; never strand at configuration |
| **The zero-model game is not fun** | Ten-minute tests rely on wild casts to excuse repetitive combat, travel, or interaction | Milestone 2 gates on combat, stealth, economy, social state, encounter grammar, and instant tempo | Cut encounter frequency and world distance before adding more model calls |
| **Ledgers do not feel like a living world** | Many durable writes, few remembered causes, orphaned promises/rumors, tester says reactions feel random | Content-unit rule, named carriers, fast local returns, cause text, orphan-write instrumentation | Reduce the number of active lanes in a situation and make fewer consequences return more strongly |
| **Middle-game sag (hours 2–8)** | Foothold ends but no capital route or regional pressure changes for long stretches | Campaign verbs, escalation ladder, recurring returns, capability milestones, run-movement telemetry | Compress geography and required defenses; never add mandatory filler journeys |
| **Danger reads as arbitrary model punishment** | Deaths trace to hidden resolver decisions, provider faults, or untelegraphed costs with no answer | Engine-owned legality; cost causes and valuable-item consent; threat telegraphs; technical failures free; chronicle audit | Bound severe cost families more tightly until their counterplay is ready |
| **Solo-development scope outruns capacity** | Milestones widen before exit evidence; support, content, or asset queue grows without closure | Gate discipline, one transect, priority rule, fixed external briefs, alternate breadth/depth EA cadence | Reduce number of dense 1.0 regions or audio variations only with an explicit owner decision; protect the complete arc and pillars first |
| **Commissioned score slips or mismatches** | No approved themes by Milestone 2 exit; integration reveals dynamic needs the brief missed | Commission early against a playable capture and clear stems/loop/delivery spec; integrate temp cues early | Ship fewer excellent pieces with strong regional reuse; never pad with generic stock fantasy music |
| **ASCII limits store appeal or legibility** | Trailer/store tests cannot parse action or identity; font/palette accessibility fails | Illuminated ASCII, portraits, restrained motion, light, strong UI hierarchy, trailer craft, color-safe modes | Revisit tiles only after EA evidence and only through the renderer boundary; do not derail the slice pre-EA |
| **Two persistence modes split the game** | Roleplay gains separate encounters/economy or Classic save authority leaks into simulation rules | Identical world rules, difficulty, serializer, and content; hosts differ only in whether saves may be freely loaded; chronicle labels mode | Simplify save-slot UX, not world balance; Classic remains canonical and checkpoint code stays deleted |
| **Breadth-by-authoring stalls** | A new region needs code, bespoke commands, or months of unique assets | Region packs inherit the slice template; fix missing general capability once, then resume data authoring | Ship fewer dense regions rather than fourteen thin ones only by explicit scope revision before 1.0 claims |
| **Content density re-inflates prompts** | Token/latency grows with props or NPC count; relevant actors disappear from context | Protected actor/hook lanes, compact scenery, lore/knowledge routing, clutter stress tests | Reduce routed context, not world state; keep detail inspectable and mechanically real |
| **EA save compatibility traps development** | Schema churn, bloated saves, or provider artifacts make real saves unmigratable | Versioned migrations, golden saves, rotating backups, materialized provider results, compatibility tests from first EA | Delay EA rather than promise wipes; after EA, cut a feature before invalidating saves |
| **Telemetry/provider trust is mishandled** | Players cannot tell what leaves the machine or where keys/logs live | Opt-in metrics, explicit transcript submission, secure key storage, plain provider/cost/privacy UI | Default to local/offline and disable collection; trust outranks diagnostic convenience |

## Anti-goals at 1.0

No tiles. No meta-progression in any mode. No quest engine, mission scripting, or bespoke finale
systems. No multiplayer. No generic rat/wolf filler encounters. No inventory-capacity or durability
chores. No model calls on the deterministic generation path. No separate demo
SKU — demo mode is the same build. No Steam until post-1.0 evaluation. No weather, ecology, or
NPC daily schedules unless the reactive world is already felt as alive without them. No account
requirement or mandatory cloud provider. No universal XP/level grind, giant crafting tree,
collectible rarity treadmill, or large bestiary added merely to resemble another roguelike. No
critical objective whose only solution is persuading a dialogue model to say the correct line. No
full voice acting, cinematic campaign, or authored branching questline. No widening a region that
has not inherited the proven content-unit template.

The plan also does not promise that every imaginable spell succeeds. It promises that reasonable
intentions usually find a coherent mechanical route, ambitious overreach is answered with a real
price more often than a flat refusal, and failure teaches without corrupting the world.

## 1.0 definition of done

Ship when, and only when, all eight are true at world scale:

| Requirement | Ship evidence |
|---|---|
| **1. Fresh machine → first wild spell in under 15 minutes, or a real demo instead of a brick.** (P1) | Clean-machine tests by people outside the project cover local install/pull, BYOK, offline/demo, failure, and retry; no terminal or file editing |
| **2. A documentation-blind player learns the game's promise inside the opening hour.** (P2) | Blind-testers use more than one escape route, author a memorable outcome, identify a returning consequence, and leave with understandable hooks without coaching |
| **3. The zero-model game is a good roguelike; the model makes it a singular one.** (P3) | Repeated ten-minute and one-hour tests show tactical, stealth, social, economic, and travel decisions with providers disabled; live tests add specificity rather than basic viability |
| **4. Classic runs are winnable in 8–12 deliberate hours through visible escalation.** (P4) | Human wins and seeded agent wins cover varied origins and at least the three reference reach routes; movement timing, economy, defenses, and pressure stay inside the envelope |
| **5. Death produces a chronicle worth sharing and a fresh world worth starting.** (P5) | Imperial/wild deaths, victory, unrealized promises, bonds, deeds, and route appear faithfully; restart is one input; second-run tests find an early meaningful difference |
| **6. Footage, stills, and audio are unmistakably Sorcerer and unmistakably finished.** (P6) | Raw trailer test, screenshot test, mute test, and region-recognition test pass; whole-run screens and accessibility modes share the art direction |
| **7. Twenty hours, one sitting, zero babysitting: latency budgets, saves, and failure modes hold.** (P7) | Soak, provider-kill, backup recovery, save migration, bounded-state, memory, disk, input/settings, and supported-machine matrices are green on release candidates |
| **8. Two runs are two different sorcerers' lives, without a single XP bar.** (P8) | Run summaries differ in repertoire, treasured material, bodies/scars, relationships, legend, route, regional opportunities, and chronicle—not only seed names or palette |

Release blockers are: data loss or unrecoverable save corruption; a technical provider failure
that consumes a turn or mutates state; a GUI/CLI capability split; an unwinnable canonical seed;
an inaccessible mandatory casting flow; a critical path that depends on hidden debug knowledge;
or any renderer/provider path bypassing engine authority. These block 1.0 even if the aggregate
scorecard is strong.

The roadmap succeeds when the honest scorecard above reads A across both rows — systems *and*
product — and the game can finally be judged the only way that matters: by a stranger, alone,
with no one from the project in the room.
