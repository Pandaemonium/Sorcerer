# The Free Folk Movement — from one rescue to a rising

Status: design brief drafted 2026-07-14 at owner request; revised same day per owner steer —
the Movement is an **existing faction** the player discovers, not a banner minted from the
player's deeds, and the imperial response rework (slow pressure, diegetic witchhunters) is
folded in as this plan's first slice. This document owns the campaign throughline that begins
when the player frees a captive in the opening and ends, if the player feeds it, in an allied
rising against the capital. Companions:
[OPENING_SEQUENCE.md](OPENING_SEQUENCE.md) (the seed this grows from),
[RUN_ARC.md](RUN_ARC.md) (pacing home), [WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md)
(arc and moral texture), [EMERGENT_WORLD.md](EMERGENT_WORLD.md) (deeds, rumors, factions,
organizations), [WORLDBUILDING.md](WORLDBUILDING.md) (canon this extends), and
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) (the phases its slices land in).

The guiding rule:

> The Free Folk already exist, and they are losing. What they lack is not will but
> capability — a sorcerer strong enough to be worth the risk of gathering again. Freeing Lio
> is how they find you; the campaign is the opening at scale.

The repo already contains almost every mechanism this arc needs: rescue handoffs that name real
contacts, claim sources on documents, promises that realize into places and people, deeds
classified by witness visibility, rumors that travel real roads, factions that spend finite
resources, and a campaign-verb table (plan §5.6) that explicitly forbids a `LiberationQuest`.
This design adds **content, readers, and one proving arc** — not a movement subsystem.

## Design goals

- **Momentum is a chain of scenes, not a chain of errands.** Every beat should end by pointing
  at a *place where something is about to happen*, with at least three honest routes through it.
- **The Movement pre-exists; the player is the missing capability.** Separatists exist in every
  conquered realm as quiet embers (canon). The Free Folk are those embers with a history and a
  memory of each other — worn down by a generation of Censorate pressure from a network into
  isolated cells. The player — an outlier-strong sorcerer whose deeds travel as rumors — is the
  thing they have waited for and the thing they fear: hope, which gets cells killed. The player
  can become their asset, their member, their leader, or their exploiter; none of it is free.
- **Wild magic is the privileged route, never the required one.** Every set-piece has force,
  stealth, social, and identity routes; wild magic gets the most tempting anchors and at least
  one magic-only *bonus layer* per scene. Irresistible, not mandatory.
- **The Empire's paperwork is both the villain and the loot.** Plans, ledgers, warrants, and
  schedules are the Empire's power made physical — which makes them stealable, forgeable,
  readable, and burnable. The campaign's central objects are documents with consequences.
- **The marble answers slowly, and in the flesh.** Imperial response is knowledge plus
  logistics: reports must travel before heat rises, and responders must walk in on real roads.
  Nothing pops in out of thin air. The witchhunters who chase the player are named, persistent
  people who ask questions in places the player can hear about.
- **Morally textured on both sides.** The Empire remains reasonable marble. The Movement
  contains mourners, opportunists, cowards, true believers, and at least one ember who wants to
  answer the Shadow Purge with a purge of their own. The player's power reopens every doctrine
  fight the elders thought settled.
- **Optional but insistent.** A player can ignore all of it; sweeps then happen to real people,
  and the world remembers the player knew. The rabbit hole pulls by petition — people come to
  you — never by gate.

## The shape of the Movement (canon extension)

This extends WORLDBUILDING's "separatists exist in every conquered realm, but they are usually
quiet — embers, not fires." The Free Folk Movement is what remains of the embers that once knew
each other's names.

- **Old, real, and losing.** The Movement is as old as the charter's conquests: folk
  practitioners, families of the taken, road-couriers, and quiet sympathizers who never
  accepted that the old ways belonged in a filing cabinet. A generation of informants and
  executions has worn it from a network into cells that no longer risk knowing each other:
  dead drops unchecked for years, elders who counsel only patience, a doctrine of surviving
  until something changes. The player is the something.
- **Cells, lines, and elders — no headquarters.** Structure is deliberately thin: local
  **cells** with folk names (Hollowmere's reed-folk — the existing `hollowmere` faction, role
  `resistance`, becomes the Movement's local cell; a Bralli hold's tale-circle; Vint's loud
  legal wing, the separatist party, deniably connected), **road lines** between them (couriers,
  ferry-keepers, Parn caravans carrying sealed nothing-in-particular for pay), and a few named
  **elders** who disagree about what the Movement is for. No center exists to capture.
- **In data, the Movement is a rolled faction from turn one.** `free_folk` joins
  `initial_factions.json` beside `empire` and rolls with the world: which realm's cells are
  strong, which went dark after arrests, where the lines still run. Per-realm cells are small
  factions with finite resources mirroring the Empire's — `support`, `shelter`, `hands`,
  `secrets` — starved at run start. The Movement exists everywhere and is strong nowhere.
- **The Censorate has a file on them already.** "Coordinated unlicensed practice" — decades
  old, patient, mostly accurate. The player's deeds get appended to it, which is how both
  sides find the player: the Censorate by correlation, the Movement by reading the same rumors
  the Empire pays informants for. The Movement's recruitment intelligence *is* the rumor lane.
- **The player earns a name inside it.** Standing with the Free Folk reads deeds and rumors,
  not quest count. One flourish: the epithet the Movement uses for the player is seed-born
  from rumor distortion of the founding deed — escape through the reeds and the lines carry
  word of *the reedfire sorcerer*; leave the yard in a borrowed body and they whisper about
  *the borrowed one*. The Movement is old; what's new each run is what they call you.
- **Joining costs the world something.** A cell fed in a settlement means patrols in that
  settlement, informants bought, reprisals with names. Some of the most decent people the
  player meets will refuse the Movement, for reasons the tone bible respects: the roads really
  are safe, the charter really did stop catastrophes, and they have children.

## The momentum chain

Five beats. Each one ends by aiming the player at the next, through claims and promises the
engine already owns — never through a chapter gate.

### Beat 1 — The seed (in the opening yard)

Three data additions to the existing opening, all through existing machinery:

1. **The docket learns about the reaping.** The containment notice / clerk's docket gains an
   authored claim seed (`ClaimSourceComponent`, exactly like the current escape-route claim):
   the annex is a staging stop for the **Provincial Reconciliation Sweep** — the folk call it
   **the reaping** — a scheduled seasonal operation of audits, confiscations, and containments.
   The docket names *a target or two* (a settlement, a family) but not routes or dates. The
   full plans sit at a relay post on the imperial road — the **waystation**. Reading it binds a
   landmark/lead promise pointing there.
2. **The rescue handoff gains a sweep tier.** When `free_captive` fires for a captive held
   under a Thaumic Containment Order, the handoff generator prefers a new opening handoff
   template: the contact the captive names is *on the schedule* — and, where a Free Folk cell
   exists in the destination region, the generated contact belongs to it or stands one hop
   from it (population-grammar roles, not a bespoke NPC). Lio's seed-varying speech stops
   being "someone saw something" and becomes "they are coming for her next — the reaping list
   is kept at the waystation." Same generator, same journal objective machinery, higher
   stakes.
3. **Wants gain deadlines.** Lio's authored want ("word to Hollowmere") gains sweep stakes;
   the ward-captain's want ("no public folk-magic scandal") gains a reason — a scandal now
   would disturb the sweep his annex is staging for.

The player leaves the opening holding three pointers to one place: a person to warn, a document
that named the operation, and a waystation where the plans live. That is momentum by dramatic
irony plus deadline, not by quest marker. A player who ignores all three still has the ordinary
opening; nothing is gated.

The sweep itself is real from turn one: a standing scheduled imperial operation (extending the
§2.2 heat-ladder templates — `empire_sweep` beside `empire_patrol`/`empire_warrant`/
`empire_cordon`), spending finite imperial resources on a route the player can learn, beat,
redirect, or watch land on people with names.

### Beat 2 — The waystation (the set-piece the opening aims at)

A small imperial relay post on the measured road: imperial name seed-varying ("Relay Post
Eleven", "the Licensed Waystation"), folk name likewise ("the Toll Kettle", "Last Stamp").
Built from existing seams: a significant interior, encounter-grammar ingredients (§3.4's
"guarded threshold with credential, stealth, social, force, and magic approaches" — this scene
is that ingredient's flagship), population roles, and claim-bearing fixtures.

The scene has two converging pointers, and the second is the Movement's introduction: the
docket claim aims the player here independently, and the local cell has **watched the
waystation for weeks**. The reed-folk know the plans are inside; they have counted the guards
and memorized the courier's schedule; what they do not have is anyone who can do it. A cell
contact can offer the job, the watching notes (guard counts, the courier's habits — real
tactical intelligence as claims), and a place to run to afterward. A player who never meets
the cell can still do the heist cold; a player who arrives through Beat 1's contact starts it
with allies, information, and obligations.

**Cast:** a post-captain (ward archetype), two road soldiers, one **weary clerk** in audit
season (the recurring Censorate clerk voice, with a want: survive the audit), a **courier** who
arrives and departs on a readable schedule (time pressure, body-swap and interception target),
a stabled Monteary post-gelding (a horse-singer's route in), and civilians queueing at the
stamp window (witnesses, cover, and consciences).

**Fixtures:** a charter-sealed dispatch case; the after-hours docket hatch; a signal bell
(alarm — and sabotage affordance); a warrant board (readable claims); a strongbox of sweep
confiscations (item promises); the stable; drainage to the reed side.

**The plans are three documents, not one McGuffin:**

- the **Sweep Schedule** — where the reaping goes and when (the campaign map of Beat 3);
- the **Requisition Ledger** — what was and will be confiscated, from whom (a promise mine:
  every line is an item claim pointing at a family who lost something — a dozen organic hooks
  in one stolen book);
- the **Warrant Packet** — who the Empire is hunting (identity play, and names the player may
  recognize: cell members, the Censorate's file on the Movement, and eventually the player).

**Routes and graded outcomes.** The route chooses what you get *and what the Empire knows* —
and "what the Empire knows" is not new machinery, it is the existing deed-visibility
classification given a strategic reader:

| Route | What you likely get | What the Empire knows | Later texture |
|---|---|---|---|
| Force | everything, plus the strongbox | immediately (public deed) | sweep reroutes; plans go stale but resources were truly spent; fear and awe travel together |
| Stealth | the originals | at the next audit (delayed) | accurate plans on a countdown; a quiet legend |
| Social — the clerk | copies | nothing | plans stay true; the clerk becomes a living asset, and a liability with a family |
| Courier interception | the schedule only | when the relay is missed | partial intelligence; a uniform, a satchel, a face to borrow |
| Forgery / alteration | the plans, and a false world | believes wrong things | the reaping marches somewhere empty; imperial embarrassment; the mischief legend |

Wild magic gets privileged anchors on every route rather than a route of its own: coax the
charter seal (mundane force destroys the case's contents — graded, not gated), ask the
post-horse what it carried, make the paper forget its route, read **the blanks** — the
confiscation arch's "deliberate blank for those that escape" motif recurs here, and only wild
magic can read what the Empire failed to file. That blank-reading is the magic-only bonus
layer: it yields the hooks the ledger itself cannot, the confiscations that never made it into
writing.

### Beat 3 — The fork (what the plans are *for*)

No menu appears. The documents are claim sources; each use is an ordinary action with ordinary
consequences, and each changes what the player is to the Movement:

- **Bring them to the cell.** The straight road in: the reed-folk get their first real victory
  in years, the player gets standing, shelter, and the lines — and the cell gets bolder, which
  is not purely good (see the Kindled, Beat 4).
- **Warn the targets yourself.** Travel the sweep route ahead of the Empire. Convincing
  strangers to hide, scatter, or stand is a persuasion scene where belief reads standing and
  rumor — the legend lane finally gating something that matters. Believed: evacuations, hidden
  folk, gratitude, a cell revived where one had gone dark. Doubted: stay and be proven right
  in the worst way, or leave.
- **Ambush the reaping.** A war footing from the first hour: real imperial resources
  destroyed, fear and gratitude rising together, the heat ladder answering — and the elders
  alarmed at a stranger spending their countryside's safety without asking.
- **Trade the intelligence.** Vintan buyers pay in cover and money; a Bone Jarl's hall pays in
  hospitality and hands; the rival realm pays best and owns you a little. The Movement notices
  who you sold their neighbors' fates to.
- **Alter and return them.** The forgery route: the sweep marches at nothing, or at something
  the player wants swept. The Empire loses face, which it spends resources to recover.
- **Do nothing.** The sweep lands. Travel later through a swept settlement and see the
  aftermath: audited hearths, a confiscated brazier, a family missing. The world remembers the
  player knew. This option must stay real, unpunished by any hand except memory.

### Beat 4 — Cells revived, connected, strengthened (Foothold into War)

The rabbit hole proper, ridden entirely on existing lanes plus the organizations layer that
EMERGENT_WORLD already promises:

- **Cells are small factions, not a meter.** Each is data-defined like `hollowmere`: role
  `resistance`, finite resources — `support`, `shelter`, `hands`, `secrets` — spent and
  restored through the same world-turn budget discipline. They start starved. Aiding a cell
  raises real resources; the Empire's `informants` resource attacks them. The war is a war of
  expenditure, which is already how the engine thinks.
- **The Movement grows by revival and connection, not spawning.** Cells pre-exist per realm in
  varying health from the world roll: alive, starved, or dark (arrested, scattered, waiting).
  The player's campaign is reviving dark cells, reconnecting lines between cells that stopped
  trusting the roads, and strengthening the living ones. A wholly new cell can still form
  where the player's deeds land on ground no ember survived — but that is the exception with
  a story, not the rule.
- **Trust is earned by deeds, not quest completion.** A cell shelters, petitions, or obeys the
  player because the rumor network says the player is real. Standing (gratitude, legitimacy)
  and legend tags gate what a cell dares ask and offer. This gives the deed→rumor→standing
  pipeline its first campaign-scale reader.
- **The asks arrive by petition.** Cells use the existing bounded NPC-initiative machinery:
  runners find the player with a cause named — "You're the one from the reed rumor. Hushwater's
  ledger says my sister's flute sits in Annex Nine." The rabbit hole pulls because people keep
  showing up, each carrying an ordinary generated contract (fetch, delivery, escort, threat,
  folk-service, rumor-verification, social-leverage) wearing movement stakes.
- **Missions are the §5.6 campaign verbs, given a giver and a why.** Establish a safehouse,
  build informants, interdict the requisition convoys, liberate a settlement, expose or frame
  an official, bind a prophecy route. The table already forbids subsystems; the Movement is
  the content that proves the compositions.
- **Captured members re-run the opening's proven loop.** When a cell is compromised — and the
  Empire spending `informants` means sometimes it is — members are taken, and `free_captive`
  fires for any of them, with the same handoff machinery Lio proved. The opening literally
  taught the campaign's recurring verb. Rescue, turn, mourn, or trade: all real.
- **The doctrine fight.** The player's arrival reopens every argument the elders thought
  settled. The patient elders want the player hidden and hoarded; the young want the player
  spent now; and one authored ember archetype, **the Kindled**, wants to answer the Purge with
  terror — possession-shaped tactics, fear as a weapon, the exact thing the Censorate's
  schoolbooks warn about. The player can lead the Movement away from it, refuse the Kindled
  and split a cell, or let it happen and watch the Empire's justification come true. The
  Movement must be capable of its own atrocity, or the Empire's moral texture collapses into
  cartoon.

### Beat 5 — The rising (Reach)

The rising is the Movement's generation-old dream — argued over by its elders for as long as
anyone can remember, never attempted because the strength was never there. The player is the
argument's end. It lands as one of the three organic capital approaches (owner call
2026-07-13: defenses = guard density, infiltration, allied war — never a binary gate):

- **Allied war:** when enough cells are strong, the lines can carry a coordinated rising — a
  campaign of world-turn expenditures that genuinely depletes `empire_bloc` defenses and thins
  the capital's guard density. The player chooses the timing. Premature, and cells burn; the
  cost lands on people the player recruited by name.
- **Infiltration:** cells supply what infiltrators need — credentials, a safehouse in the
  Outer Petition Camp, a clerk who owes the player everything.
- **Force:** hands, and a diversion bought with them.

All three capital routes gain a different Movement expression (mirroring §6.3's rule for
Brall); none requires the Movement at all. Post-victory follows the owner calibration: those
who approach are mostly the grateful; those who loved the peace are absent, wary, or grieving
in the chronicle.

## The marble answers slowly (imperial response rework)

Owner steer, 2026-07-14: the current response runs too fast — kill an imperial and another
pops in out of thin air almost immediately. It was intended as slow pressure, with the
witchhunters who chase the player arriving **diegetically**. This section owns that rework; it
is this plan's first slice because it is a live playability irritant, and because the pacing
fix and the Movement arc fight on the same seam: roads, reports, and logistics.

**Current behavior (audited from code):**

- Any witnessed kill or public cast raises empire `heat` instantly at the next pump
  (`WorldReactionSystem.RaiseEmpireHeat`) — the Empire receives knowledge for free, whether or
  not anyone lived to carry a report.
- At heat ≥ 3, `WorldTurnSystem.TryApplyFactionPressure` spends a patrol and schedules
  `empire_patrol` due in **2 turns**.
- When due, `TurnSystem.ResolveEmpirePatrol` spawns a patrol-censor at the first free tile
  roughly **5 tiles from the player** — in the same room, out of thin air.
- Every quiet recovery pump regenerates a patrol (`RecoverFactionPressure`), so the
  replacement queue effectively never empties.

**Redesign principles:**

1. **Reports travel before heat rises.** Heat is knowledge, and knowledge needs a carrier: a
   surviving witness, a paid informant, a report reaching an imperial post. Move heat
   adjustment from an instant world-reaction write to a consequence that fires when a
   deed-rumor **reaches an imperial carrier** (post fixture, informant NPC, another patrol) —
   the rumor lane already propagates along real roads; the Empire becomes one more reader of
   it. A silent massacre with no survivors raises nothing until the silence itself is noticed:
   a scheduled **overdue audit** ("Relay Post Eleven reports a patrol two days overdue") that
   is itself a readable memo and a fair warning to the player. Material facts stay instant —
   dead soldiers are dead, `defenses` erode on real losses — but *attribution and alarm* must
   travel. This is Phase 4's evidence philosophy applied early: the Empire is dangerous
   because its institutions are real, not because it receives hidden omniscience.
2. **Arrival is travel, not spawn.** A dispatched response enters the world at a real seam —
   the region's road gate, a zone edge, the nearest garrison or waystation — and walks.
   Cross-zone: it arrives in an adjacent zone first and traverses. Same-zone: it materializes
   only at a map-edge road tile **outside the player's line of sight**, and marches in.
   Nothing imperial ever appears within sight of the player. The `FindPatrolSpawnPoint`
   candidates 5 tiles from the player are deleted, not tuned.
3. **Longer fuses, telegraphed in the fiction.** The due-in-2 fuse becomes distance-scaled
   (frontier target: roughly 10–16 turns; exact numbers live in RUN_ARC once instrumented) —
   long enough that the player can finish the current scene and choose to leave, hide, or
   prepare before contact. The journal already forecasts ("an imperial patrol is expected
   around turn N"); keep it, and move the telegraph into the world: hoofbeats named on the
   road, an NPC who saw riders, a notice nailed up at the ferry. One clear warning beat before
   contact, always.
4. **Witchhunters are named, persistent, and diegetic.** The warrant rung stops being abstract
   legend-pressure and dispatches a **named hunter** — an archetyped persistent actor (plan
   §2.2/§3.1: wants, knowledge, memories, behavior tags, not a boss with a private scheduler)
   who travels zone to zone, questions NPCs along the way, and leaves memory and rumor traces
   the player can overhear: "a mustached man was asking the ferry-keeper about reed-fire."
   The hunter follows real evidence through the ordinary knowledge lanes, arrives on the road
   like anyone else, and can be evaded, misdirected, ambushed, bargained with, or fed a false
   identity. Killing one is a real loss the Empire cannot regenerate at a pump — a replacement
   is a person, and people are slow and angrier.
5. **Replacement is logistics.** Patrol regeneration slows sharply (every N quiet pumps, not
   every pump) and replacements come *from somewhere*: the nearest garrison or waystation,
   traveling the same roads. This makes road interdiction and the waystation heist real
   counterplay against pressure itself — cut the line and the marble cannot answer — which is
   exactly the seam the Free Folk campaign fights on. The pacing fix and the Movement arc are
   one design.
6. **The budget discipline is untouched.** Every response still spends a finite resource with
   a named cause through bounded world-turn moves; this rework changes *where and when* the
   spend becomes flesh, not whether it is bounded.

**Feel target:** after a first public kill, the player should have minutes of play — not
seconds — before the marble's answer is physically present, and when it arrives the player
should be able to say where it came from, how it knew, and what warning they ignored.

## One good first two hours (reference thread)

Not a script — a feel target for the reference seed.

You free Lio because the cell key was there and the soldiers weren't looking. Her thanks names
Hushwater and a contact — and the reason: the reaping list has them for the new moon. On the
way out you read the docket; the word "Reconciliation" sits in cold Latinate over a schedule
kept elsewhere, at a relay post on the measured road. You go, because a person, a paper, and a
place all pointed the same direction.

The contact turns out to be a reed-cutter who asks you three questions that are really one
question, then admits the waystation has been watched for a month: guard counts, the courier's
habits, the clerk who stays latest in audit season. The reed-folk have everything but someone
who can do it. You'd rather be charming than famous, so you carry boxes for the drowning clerk
for an hour, magic one uncooperative sum into agreement while nobody watches, and leave with
copies of everything and a friend the Empire doesn't know it's lost. In the reeds you read the
ledger's blank lines by willow-light — the arch's habit of leaving blanks for what escapes —
and find a confiscation that was never filed: somebody bribed somebody. Leverage.

You reach Hushwater two days ahead of the sweep. The rumor of the yard escape came down the
road before you did, and the reed-cutter's word came with it, so they listen. They hide what
matters. The sweep arrives, finds a village already reconciled, and spends its budget on
nothing. That night, for the first time, somebody uses the old name to your face — *free
folk* — and tells you what the lines have started calling you. You haven't decided if you like
it. At the ferry a week later, a man with a full bushy mustache has been asking the keeper
about reed-fire, polite as a filing cabinet, and he is still two villages behind you — because
someone on the line thought you should know.

## Engine audit (representation ladder)

| Piece | Rides on | Rung | New |
|---|---|---|---|
| `free_folk` rolled faction + realm cells | faction definitions (à la `hollowmere`), world roll, population grammar | 1–2 | faction data + per-realm cell roll (health: alive/starved/dark) |
| Sweep claim in the yard | `ClaimSourceComponent`, promise ledger | 1 | claim seed data |
| Sweep-tier rescue handoff | `free_captive` handoff generator, templates | 1 | handoff rows + cell-membership role bias |
| The sweep operation | §2.2 response templates, scheduled world-turn moves, finite resources | 2 | `empire_sweep` template + schedule data |
| Provenance ("does the Empire know") | deed visibility classes (public/witnessed/suspicious/secret) | 3 | sweep scheduler *reads* theft-deed visibility |
| Report-borne heat | rumor propagation on roads, imperial carriers, scheduled events | 3 | heat writes move behind report arrival + overdue audits |
| Edge/road arrival | zone graph, roads, spawn consequences, line of sight | 2 | arrival placement policy replaces near-player spawn |
| Named witchhunters | actor archetypes (§3.1), wants/memories, NPC initiative, rumor traces | 2 | hunter archetype data + dispatch template |
| Waystation | significant interiors, encounter grammar, population roles, services | 1 | site/interior/role data |
| The three documents | multi-seed claim sources, item promises, warrants (Phase 4 reads them later) | 1–2 | document templates |
| Graded outcomes | existing consequence packets per route | 2 | none |
| Trust gating | standing, legend tags, rumor records | 3 | cell-side readers |
| Petitions | bounded NPC initiative with named cause | 3 | movement-tagged ask selection |
| Missions | §5.6 campaign-verb compositions, existing contract kinds | 2 | movement-flavored handoff rows |
| Member capture/rescue | Empire `informants` spend, `free_captive` | 2–3 | capture as a faction-pressure template |
| Player epithet | rumor distortion jobs, founding-deed provenance | 3 | epithet-mint reader on the rumor lane |
| Censorate file | heat-ladder memos now; Phase 4 correlation later | 2 | memo template |
| The rising | capital-defense depletion (§1.2), world-turn expenditure | 2–3 | rising as a coordinated multi-cell expenditure |

Nothing here proposes a `MovementLedger`, `CellSystem`, or `SweepQuest`. The one durable-ish
addition — the Movement and its cells as plural small factions — is the proving content for
the organizations layer EMERGENT_WORLD already commits to, exactly as Lio was the proving
ground for handoffs.

## Where the slices land (phase mapping)

0. **S0 — The marble answers slowly** (now; extends Phase 2.2): report-borne heat, edge/road
   arrival replacing near-player spawn, distance-scaled fuses with a fictional telegraph,
   slowed patrol regeneration, overdue audits, and the first named witchhunter dispatched at
   the warrant rung. Exit: after a public kill, no imperial responder ever appears within the
   player's sight; a silent no-witness kill raises no heat until an overdue audit fires; a
   documentation-blind player can say where the response came from and how it knew.
1. **S1 — The seed** (with Phase 2): docket claim, sweep-tier handoff rows, want stakes,
   `empire_sweep` as a standing scheduled operation with a readable route, `free_folk` faction
   and cell roll in data. Exit: a fresh opening reliably points at the waystation through at
   least two of the three pointers, and the sweep lands somewhere real if ignored.
2. **S2 — The waystation** (Phase 2–3): site/interior data, cast with wants and services, the
   three documents as claim sources, the watching cell as optional giver with real tactical
   intelligence, all five routes playable, graded outcomes verified. Exit: force, stealth,
   social, interception, and forgery each end with different documents, deeds, and provenance;
   the heist works with or without the cell's help and remembers which happened.
3. **S3 — Provenance strategy** (Phase 2.2 extension): sweep scheduling reads theft-deed
   visibility; reroutes, countdowns, and false-plan marches work; aftermath texture in swept
   settlements. Exit: the quiet-heist and loud-heist runs produce different sweep behavior a
   documentation-blind player can explain.
4. **S4 — First contact** (Phase 2/5): the reed cell's resources, trust reading
   standing/rumors, first petitions, safehouse verb proven, a dark cell revivable by the warn
   route. Exit: warning Hushwater revives or strengthens a cell; ignoring the Movement leaves
   it starved but present.
5. **S5 — The war of expenditure** (Phase 5): multi-cell asks through NPC initiative,
   campaign-verb missions, informant pressure, capture→rescue loop, the doctrine fight and the
   Kindled tension beat. Exit: the wanting-stranger and price-that-comes-back proofs pass with
   movement content.
6. **S6 — Coalition and the loud wing** (Phase 6): Brall's tale-circle joins through the
   retelling chain (a boasted deed *is* recruitment), Vint's party as the loud legal wing,
   the Censorate file surfacing as a readable artifact. Exit: gate 8 of Phase 6 (a Bralli
   retelling changes a gameplay decision) is satisfied by movement content.
7. **S7 — The rising** (Phase 6, capital): coordinated expenditure path into §1.2's capital
   defense facts; all three approach routes gain movement expressions. Exit: the
   three-roads-to-the-emperor proof passes with and without the Movement.

## Failure modes to avoid

- Do not make the Movement a reputation grind or a faction bar to fill. Trust reads deeds and
  rumors that already exist; there is no movement XP.
- Do not gate any capital route, region, or system behind joining. The Movement multiplies
  routes; it never owns one.
- Do not make the Movement right. It must contain the Kindled, the opportunists, and the
  cowards, and it must be able to do harm the player answers for.
- Do not pre-place strength. The Movement pre-exists everywhere, but it starts starved and
  partly dark; the map does not open with rebel bases, and reviving a cell is play, not a
  checkbox.
- Do not let any imperial responder appear within the player's sight, and do not let heat rise
  without a carrier. If the Empire knows something, the player should be able to trace how.
- Do not script the waystation. If a beat matters, build the reusable ingredient beneath it —
  this scene is the flagship of the guarded-threshold grammar, not a bespoke room.
- Do not require wild magic anywhere; make it the most interesting thing in every room.
- Do not let sweeps become a spawn ratchet. They are finite scheduled expenditures on a
  readable route, and beating one genuinely costs the Empire.
- Do not add a `MovementSystem`, `CellLedger`, `SweepManager`, or `RisingQuest`. Return to the
  engine-audit table.

## Success criteria

- A new player leaves the opening able to say where they're going next and why, without a
  quest marker — and the answer is a scene, not an errand.
- The waystation supports at least five genuinely different playthroughs whose differences
  persist (documents held, provenance, clerk's fate, cell involvement, sweep behavior).
- The route taken through the heist changes campaign texture two hours later in ways a player
  can narrate causally.
- After a public kill, the marble's answer takes minutes, arrives by road, and was telegraphed;
  a player who watches the roads is never ambushed by a spawn.
- The witchhunter on the player's trail is a person: named, askable-about, evadable,
  bargainable, and mortal — and losing him costs the Empire something real.
- Two runs find the Movement in different health — which cells live, which went dark, which
  realm is loud — and call the player by different names.
- Ignoring the Movement produces a coherent, quieter run in which sweeps land and the world
  remembers — and the three capital routes still work.
- The Empire never stops being reasonable; the Movement never becomes clean; the player is the
  difference between a rising and a second Purge.
