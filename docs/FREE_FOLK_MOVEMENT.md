# The Free Folk Movement — from one rescue to a rising

Status: design brief drafted 2026-07-14 at owner request. This document owns the campaign
throughline that begins when the player frees a captive in the opening and ends, if the player
feeds it, in an allied rising against the capital. Companions:
[OPENING_SEQUENCE.md](OPENING_SEQUENCE.md) (the seed this grows from),
[RUN_ARC.md](RUN_ARC.md) (pacing home), [WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md)
(arc and moral texture), [EMERGENT_WORLD.md](EMERGENT_WORLD.md) (deeds, rumors, factions,
organizations), [WORLDBUILDING.md](WORLDBUILDING.md) (canon this extends), and
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) (the phases its slices land in).

The guiding rule:

> The Movement is not a quest line the player follows. It is what the world starts calling the
> player's consequences. Freeing Lio is the Movement in miniature; the campaign is the opening
> at scale.

The repo already contains almost every mechanism this arc needs: rescue handoffs that name real
contacts, claim sources on documents, promises that realize into places and people, deeds
classified by witness visibility, rumors that travel real roads, factions that spend finite
resources, and a campaign-verb table (plan §5.6) that explicitly forbids a `LiberationQuest`.
This design adds **content, readers, and one proving arc** — not a movement subsystem.

## Design goals

- **Momentum is a chain of scenes, not a chain of errands.** Every beat should end by pointing
  at a *place where something is about to happen*, with at least three honest routes through it.
- **The player kindles the Movement; they do not join it.** Separatists exist in every conquered
  realm as quiet embers (canon). Nobody has connected them, because connecting them is death.
  The player — an outlier-strong sorcerer whose deeds travel as rumors — is the missing proof
  that wild magic can still win. The Movement's growth tracks the player's actual remembered
  deeds, because the engine already computes exactly that.
- **Wild magic is the privileged route, never the required one.** Every set-piece has force,
  stealth, social, and identity routes; wild magic gets the most tempting anchors and at least
  one magic-only *bonus layer* per scene. Irresistible, not mandatory.
- **The Empire's paperwork is both the villain and the loot.** Plans, ledgers, warrants, and
  schedules are the Empire's power made physical — which makes them stealable, forgeable,
  readable, and burnable. The campaign's central objects are documents with consequences.
- **Morally textured on both sides.** The Empire remains reasonable marble. The Movement
  contains mourners, opportunists, cowards, true believers, and at least one ember who wants to
  answer the Shadow Purge with a purge of their own. Leading it is the player's problem, not a
  reputation grind.
- **Optional but insistent.** A player can ignore all of it; sweeps then happen to real people,
  and the world remembers the player knew. The rabbit hole pulls by petition — people come to
  you — never by gate.

## The shape of the Movement (canon extension)

This extends WORLDBUILDING's "separatists exist in every conquered realm, but they are usually
quiet — embers, not fires." It does not contradict it; the Movement is what happens when
someone finally carries fire between the embers.

- **The embers are plural and local.** There is no org chart, no headquarters, no ranks. Each
  region grows a **cell**: a small organization with its own folk name, wants, resources, and
  posture. Hollowmere's reed-folk (the existing `hollowmere` faction, role `resistance`) is the
  first. Brall's is a hold's tale-circle; Vint's is the one *loud* wing — the legitimate
  separatist party, which brings money and cover but also politicians. The rival realm is not a
  cell; it is a state, and it deals in arms and asylum, at state prices.
- **The Censorate names it first.** Long before the folk have a word for themselves, a clerk's
  correlation memo files the player's deeds as evidence of "coordinated unlicensed practice — a
  *movement*." The Empire's filing system literally creates the Movement by naming it, which is
  the most Vigovian thing imaginable. The memo is a readable artifact the player can steal,
  read, or frame someone else into.
- **The folk name is seed-born.** Each run, the Movement's common name is minted by rumor
  distortion of the player's founding deed, in earthy folk compounds: an escape through the
  reeds becomes *the Reedfire*; a body-swap escape becomes *the Borrowed*; a massacre becomes
  something the player may not enjoy hearing. "The free folk" is the stable generic members use
  for each other. The player hears their movement's name from a stranger's mouth before anyone
  asks them to lead it.
- **Joining costs the world something.** A cell in a settlement means patrols in that
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
   template: the contact the captive names is *on the schedule*. Lio's seed-varying speech
   stops being "someone saw something" and becomes "they are coming for her next — the reaping
   list is kept at the waystation." Same generator, same journal objective machinery, higher
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
  recognize, including eventually their own).

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
consequences, and each recruits a different kind of ember:

- **Warn the targets.** Travel the sweep route ahead of the Empire. Convincing strangers to
  hide, scatter, or stand is a persuasion scene where belief reads standing and rumor — the
  legend lane finally gating something that matters. Believed: evacuations, hidden folk,
  gratitude, the first cell. Doubted: stay and be proven right in the worst way, or leave.
- **Ambush the reaping.** A war footing from the first hour: real imperial resources destroyed,
  fear and gratitude rising together, the heat ladder answering.
- **Trade the intelligence.** Vintan buyers pay in cover and money; a Bone Jarl's hall pays in
  hospitality and hands; the rival realm pays best and owns you a little.
- **Alter and return them.** The forgery route: the sweep marches at nothing, or at something
  the player wants swept. The Empire loses face, which it spends resources to recover.
- **Do nothing.** The sweep lands. Travel later through a swept settlement and see the
  aftermath: audited hearths, a confiscated brazier, a family missing. The world remembers the
  player knew. This option must stay real, unpunished by any hand except memory.

### Beat 4 — Embers into cells (Foothold into War)

The rabbit hole proper, ridden entirely on existing lanes plus the organizations layer that
EMERGENT_WORLD already promises:

- **Cells are small factions, not a meter.** Each is data-defined like `hollowmere`: role
  `resistance` (generalized in fiction to *free folk*), finite resources mirroring the
  Empire's — `support`, `shelter`, `hands`, `secrets` — spent and restored through the same
  world-turn budget discipline. Aiding a cell raises real resources; the Empire's `informants`
  resource attacks them. The war is a war of expenditure, which is already how the engine
  thinks.
- **Trust is earned by deeds, not quest completion.** A cell recruits, shelters, or petitions
  the player because the rumor network says the player is real. Standing (gratitude,
  legitimacy) and legend tags gate what a cell dares ask and offer. This gives the
  deed→rumor→standing pipeline its first campaign-scale reader.
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
- **The internal enemy.** One authored ember archetype, **the Kindled**: a cell leader who
  wants to answer the Purge with terror — possession-shaped tactics, fear as a weapon, the
  exact thing the Censorate's schoolbooks warn about. The player can lead them away from it,
  refuse them and split a cell, or let it happen and watch the Empire's justification come
  true. The Movement must be capable of its own atrocity, or the Empire's moral texture
  collapses into cartoon.

### Beat 5 — The rising (Reach)

The Movement's endgame is one of the three organic capital approaches (owner call 2026-07-13:
defenses = guard density, infiltration, allied war — never a binary gate):

- **Allied war:** when enough cells are strong, they can offer a coordinated rising — a
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

## One good first two hours (reference thread)

Not a script — a feel target for the reference seed.

You free Lio because the cell key was there and the soldiers weren't looking. Her thanks names
Hushwater and a contact — and the reason: the reaping list has them for the new moon. On the
way out you read the docket; the word "Reconciliation" sits in cold Latinate over a schedule
kept elsewhere, at a relay post on the measured road. You go, because a person, a paper, and a
place all pointed the same direction.

At the Toll Kettle you watch the courier water his gelding and decide you'd rather be charming
than famous. The clerk is drowning in audit season; you carry boxes for an hour, magic one
uncooperative sum into agreement while nobody watches, and leave with copies of everything and
a friend the Empire doesn't know it's lost. In the reeds you read the ledger's blank lines by
willow-light — the arch's habit of leaving blanks for what escapes — and find a confiscation
that was never filed: somebody bribed somebody. Leverage.

You reach Hushwater two days ahead of the sweep. Nobody believes a stranger — but the rumor of
the yard escape arrived on the road before you did, and an old reed-cutter squints: "You're the
one the reeds are talking about." They hide what matters. The sweep arrives, finds a village
already reconciled, and spends its budget on nothing. That night someone uses a word you
haven't heard before for the people who warned them — your movement has a name now, and you
didn't choose it — and a runner from two villages east is already waiting at the ferry,
because their ledger page wasn't blank.

## Engine audit (representation ladder)

| Piece | Rides on | Rung | New |
|---|---|---|---|
| Sweep claim in the yard | `ClaimSourceComponent`, promise ledger | 1 | claim seed data |
| Sweep-tier rescue handoff | `free_captive` handoff generator, templates | 1 | handoff rows |
| The sweep operation | §2.2 response templates, scheduled world-turn moves, finite resources | 2 | `empire_sweep` template + schedule data |
| Provenance ("does the Empire know") | deed visibility classes (public/witnessed/suspicious/secret) | 3 | sweep scheduler *reads* theft-deed visibility |
| Waystation | significant interiors, encounter grammar, population roles, services | 1 | site/interior/role data |
| The three documents | multi-seed claim sources, item promises, warrants (Phase 4 reads them later) | 1–2 | document templates |
| Graded outcomes | existing consequence packets per route | 2 | none |
| Cells | faction definitions (à la `hollowmere`), faction resources, world-turn budget | 1–2 | cell faction data + per-realm generation grammar |
| Trust gating | standing, legend tags, rumor records | 3 | cell-side readers |
| Petitions | bounded NPC initiative with named cause | 3 | movement-tagged ask selection |
| Missions | §5.6 campaign-verb compositions, existing contract kinds | 2 | movement-flavored handoff rows |
| Member capture/rescue | Empire `informants` spend, `free_captive` | 2–3 | capture as a faction-pressure template |
| Movement naming | rumor distortion jobs, founding-deed provenance | 3 | name-mint reader on the rumor lane |
| Censorate memo | heat-ladder memos now; Phase 4 correlation later | 2 | memo template |
| The rising | capital-defense depletion (§1.2), world-turn expenditure | 2–3 | rising as a coordinated multi-cell expenditure |

Nothing here proposes a `MovementLedger`, `CellSystem`, or `SweepQuest`. The one durable-ish
addition — cells as plural small factions — is the proving content for the organizations layer
EMERGENT_WORLD already commits to, exactly as Lio was the proving ground for handoffs.

## Where the slices land (phase mapping)

1. **S1 — The seed** (with Phase 2): docket claim, sweep-tier handoff rows, want stakes,
   `empire_sweep` as a standing scheduled operation with a readable route. Exit: a fresh
   opening reliably points at the waystation through at least two of the three pointers, and
   the sweep lands somewhere real if ignored.
2. **S2 — The waystation** (Phase 2–3): site/interior data, cast with wants and services, the
   three documents as claim sources, all five routes playable, graded outcomes verified. Exit:
   force, stealth, social, interception, and forgery each end with different documents, deeds,
   and provenance.
3. **S3 — Provenance strategy** (Phase 2.2 extension): sweep scheduling reads theft-deed
   visibility; reroutes, countdowns, and false-plan marches work; aftermath texture in swept
   settlements. Exit: the quiet-heist and loud-heist runs produce different sweep behavior a
   documentation-blind player can explain.
4. **S4 — First cell** (Phase 2/5): Hollowmere cell resources, trust reading
   standing/rumors, first petitions, safehouse verb proven. Exit: warning Hushwater births a
   cell; ignoring it doesn't.
5. **S5 — The war of expenditure** (Phase 5): multi-cell asks through NPC initiative,
   campaign-verb missions, informant pressure, capture→rescue loop, the Kindled tension beat.
   Exit: the wanting-stranger and price-that-comes-back proofs pass with movement content.
6. **S6 — Coalition and name** (Phase 6): Brall's tale-circle joins through the retelling
   chain (a boasted deed *is* recruitment), Vint's party as the loud wing, movement naming
   from founding-deed rumor, Censorate memo artifact. Exit: gate 8 of Phase 6 (a Bralli
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
- Do not script the waystation. If a beat matters, build the reusable ingredient beneath it —
  this scene is the flagship of the guarded-threshold grammar, not a bespoke room.
- Do not pre-place cells on the map. They form where the player's consequences land, through
  claims and promises, or they stay embers.
- Do not require wild magic anywhere; make it the most interesting thing in every room.
- Do not let sweeps become a spawn ratchet. They are finite scheduled expenditures on a
  readable route, and beating one genuinely costs the Empire.
- Do not add a `MovementSystem`, `CellLedger`, `SweepManager`, or `RisingQuest`. Return to the
  engine-audit table.

## Success criteria

- A new player leaves the opening able to say where they're going next and why, without a
  quest marker — and the answer is a scene, not an errand.
- The waystation supports at least five genuinely different playthroughs whose differences
  persist (documents held, provenance, clerk's fate, sweep behavior).
- The route taken through the heist changes campaign texture two hours later in ways a player
  can narrate causally.
- A cell exists only where the player's deeds made it plausible; two runs grow differently
  named, differently shaped movements.
- Ignoring the Movement produces a coherent, quieter run in which sweeps land and the world
  remembers — and the three capital routes still work.
- The Empire never stops being reasonable; the Movement never becomes clean; the player is the
  difference between a rising and a second Purge.
