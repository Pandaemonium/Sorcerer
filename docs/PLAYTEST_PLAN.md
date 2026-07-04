# Extensive Agent Playtest Plan

Status: active playtest charter, 2026-07. Companion to
[CLI_AND_AGENT_PLAYTESTING.md](CLI_AND_AGENT_PLAYTESTING.md) (mechanics of driving the CLI) and
[SYSTEMS_VISION.md](SYSTEMS_VISION.md) (what the systems are supposed to feel like together).
This document is the operating manual for a playtesting agent (typically Sonnet) running long,
methodical CLI campaigns against the live Ollama resolver.

## Mission

Play Sorcerer the way a curious, mischievous human would, via the CLI, and produce two outputs:

1. **Mechanical bugs — fix them.** Crashes, invariant violations, turn-accounting mistakes,
   rollback failures, orphan state, parity gaps, schema-repair failures that should have been
   repairable. When a bug is found: reproduce it with the shortest command script (mock provider if
   possible), fix it, add a focused test, and note the fix in the bug log.
2. **Feel findings — log them, do not "fix" them.** Whether magic feels vivid or boring, whether
   consequences read as caused, whether the world feels like it has opinions, latency pain, prose
   quality, pacing. These go in the feel log as observations and suggestions, never as unilateral
   design changes.

The distinction matters: a rejected spell that consumes a turn is *correct mechanics* that may
still be a *feel finding* if the rejection message is flat. Log accordingly.

## Ground Rules

- **Live Ollama is the default resolver.** Use `--provider ollama --model qwen3.5:9b-cpu`
  (confirm availability with `ollama list`; fall back to another local model and record which).
  The point is to see real latency, schema dialects, boring resolutions, and target mistakes.
- **Mock is for reproduction and regression**, not for judging feel. When a live run exposes a
  bug, try to reduce it to a mock-provider script before fixing.
- **Always record.** Every session gets `--json --debug-state --transcript logs\playtest\<session>.jsonl`.
  Keep `logs\wild_magic_audit.jsonl` and `logs\dialogue_audit.jsonl` for resolver forensics.
- **Use seeds deliberately.** Record the seed of every run. Re-run interesting seeds when
  comparing before/after a fix.
- **Never let the model be the engine.** If something looks wrong, check whether the engine
  applied what the model proposed or the model proposed something the engine should have rejected.
  `magic.resolvedMagicJson` in the transcript and the audit logs make this distinction checkable.
- **Debug state is for testing, not for play decisions that measure feel.** When judging
  legibility ("would a player understand why this happened?"), read only player-facing messages,
  `journal`, `rumors`, `standing`, `bonds` — then check debug state to see what the truth was.

## Logging Conventions

Create these once and append per session:

- `logs\playtest\FEEL_LOG.md` — the feel journal (format below).
- `logs\playtest\BUG_LOG.md` — mechanical bugs: symptom, repro script, root cause, fix commit,
  test added.
- `logs\playtest\<NN>_<topic>.jsonl` — one transcript per session, numbered.

### Feel log entry format

```markdown
## [NN] <session topic> — <date>, seed <seed>, <provider/model>
- **Moment:** what happened (quote the actual prose/messages)
- **Feel:** vivid / flat / confusing / delightful / tedious — and why
- **Axis:** authorship | wonder | mischief | tactical | legibility | pacing | latency | tone
- **Suggestion:** (optional) smallest general change that might improve it
```

Rate each session 1–5 on the four pillar feelings (authorship, wonder, mischief, tactical
cleverness) plus legibility and pacing, with one sentence each. Trends across sessions matter
more than single scores.

### Bug log entry format

```markdown
## [NN] <symptom>
- **Repro:** provider/seed/command script (shortest known)
- **Expected / Actual:**
- **Invariant violated:** (if any)
- **Root cause:**
- **Fix:** commit + files
- **Test:** test name added
```

## Standing Invariants (check every session)

These are hard failures regardless of session topic. The episode runner checks some automatically;
watch for all of them in manual sessions too:

- No crashes or unhandled exceptions; no `technicalFailure: true` that consumes a turn.
- Intentional magical rejections DO consume a turn; technical LLM failures do NOT.
- Turn counter increments by exactly what the commands imply; read-only commands
  (`inspect`, `map`, `atlas`, `journal`, `rumors`, `character`, `bonds`, `standing`, `wares`,
  `services`, `jobs`, `reagents`, `followers`, `help`) never consume turns.
- State validation passes after every command; no blocking actors overlapping or standing in walls.
- Rollbacks are total: a rejected child consequence must leave no partial mutation (check debug
  ledger counts before/after a rejection; look for `*Skipped` audit deltas paired with unchanged
  state).
- No duplicate promise ids; faction standing and resources stay within bounds; legend weights
  bounded.
- Save → load → save is byte-stable; loading preserves pending casts, ledgers, rumor trails,
  world-turn audits.
- Player-visible messages appear for every player-visible effect; audit-only deltas
  (`worldTurn` wrappers, hidden memories, `want_stir`) never leak into player-facing prose, and
  never duplicate it.
- Every consequence the player perceives carries its cause in-fiction (this is both an invariant
  aspiration and a feel axis — log violations as feel findings unless the cause is genuinely
  missing from deltas too).

## Session Plan

Run the sessions below in order the first time through; afterwards, re-run any session whose
system changed. Each session lists objectives, a seed command sequence (improvise beyond it —
scripted commands are the floor, not the ceiling), mechanical checks, and feel questions.
Sessions marked **[live]** must use Ollama; **[mock]** are deterministic; **[both]** means run
mock first for mechanics, then live for feel.

---

### Session 1 — Opening containment yard, straight play [live]

The first 20 minutes are the proving ground (see [OPENING_SEQUENCE.md](OPENING_SEQUENCE.md)).
Play the opening honestly: wake up, look around, read things, talk, escape however feels natural.

Seed commands: `inspect`, `map`, `character`, `read notice`, `examine brazier`, `talk Lio`,
then improvise an escape (key, magic, talk, or theft).

Mechanical: claim seeds from notice/brazier land in the claim ledger (`--debug-state`); the
cell-open promise realizes when the door opens; `free_captive` cascade commits atomically
(faction/control/bond/want/deed/message all present or all absent); patrol pressure responds to
public magic.

Feel: Does the room read as dense with hooks rather than a combat chamber? Are 3–5 promise
vectors discoverable? Does escaping by talk feel as real as escaping by magic? Does the Empire
feel competent and readable rather than cartoonish? Did you leave with a story you authored and
two hooks you care about?

### Session 2 — Opening replayed through every escape path [both]

Re-run the opening at least four times, forcing different paths: (a) unlock with the key,
(b) violent magic, (c) pure social (talk/give/recruit Lio, let events unfold), (d) body swap a
soldier (`possess` or a swap spell). Use different origins (`--origin fugitive_wild_sorcerer`,
`bone_singers_apprentice`, `deserter_charter_mage`, and any others in
`content/origins/initial_origins.json`).

Mechanical: each origin's stats/items/faction reactions apply; possession packet commits soul
swap + controllers + factions + statuses + control pointer + deed + narration as one unit or rolls
back with `possessionSkipped`; after a swap, CLI observation/FOV/inventory follow the new body;
the vacated body persists as an inert entity.

Feel: do the origins feel like different people or just different stat lines? Does body swap feel
transgressive and consequential? Does witness visibility differ between the quiet key escape and
the loud magic escape — and can the player *tell* it differed?

### Session 3 — Wild magic breadth: the operation families [live]

Systematically exercise spell families with varied phrasings. At least three casts per family:

- damage / heal / mana (`cast scald the far soldier with kettle-steam`)
- status apply/remove, status acceleration
- terrain create/update (`cast freeze the floor`, `cast raise a briar wall across the doorway`)
- movement/teleport of self and others
- summoning (`cast call a hound of chimney smoke`)
- transform_entity on props/fixtures (`cast make the brazier into a bell that hates soldiers`)
- resistance/weakness, delayed damage + release
- triggers/wards/auras (`cast ward this door to shriek at imperial boots`)
- persistent effects and combat hooks (thorns, venom)
- tile flows
- the generic `consequence` operation reaching social lanes from a spell
  (`cast promise that this notice will remember my name`, memory edits, bond-touching magic)

Mechanical: every accepted cast maps to declared `effectTypes`; validation rejects
out-of-scope targets; costs/reagent consumption match `reagents` before/after; `spellBias` on
reagents visibly influences resolution (compare with/without grave salt etc.); audit log shows
repair steps when the model emitted a dialect.

Feel: this is the crown jewel. For each cast log: was the outcome *specific* to the wording, or
generic? Did costs feel strange-and-gorgeous or bookkeeping-ish? Track the **boring-but-valid
rate** — the fraction of accepted spells whose result could have come from any spell. Note the
best and worst resolution verbatim in the feel log.

### Session 4 — Cast lifecycle, latency, and failure modes [live]

Exercise the seam between resolution and application: `begin_cast` / `await_cast` /
`cancel_cast`; read-only commands while pending; save with a pending cast, load, `await_cast`
(must apply the materialized spell without a second model call). Force failures: nonsense
prompts, impossible targets, unreachable model (stop Ollama mid-session once).

Mechanical: state-changing commands blocked while pending; `cancel_cast` consumes no turn;
technical failure (provider down) consumes no turn; intentional rejection consumes a turn and
narrates in-fiction; `pendingCast` states transition `resolving → ready/failed` correctly.

Feel: how bad is the latency wait in practice? Does the pending-cast flow give the player
anything to do or watch? Is the difference between "the magic refused" and "the machine broke"
legible in prose? (Combat-as-latency is a named failure-mode watch item in SYSTEMS_VISION.)

### Session 5 — Targeting, equipment, inventory, reagents [mock]

`target`/`untarget`, `focus`/`unfocus`, `equip`/`unequip`, `pickup`/`drop`/`use`,
`protect`/`unprotect`, `read`, `examine`. Cast with and without a focus, with and without a
selected target, with protected vs unprotected reagents.

Mechanical: consumable use commits narration + effect + inventory spend atomically
(`useItemSkipped` on failure); protected items never burn as reagents; equipment changes show in
`character`/observation; focus alters resolution context; `target x y` binds and casts honor it.

Feel: is the reagent economy legible — can you predict what a cast might consume? Do treasured
vs fuel items feel meaningfully different?

### Session 6 — Dialogue, wants, and claims [live]

Long conversations with Lio and any other NPCs: open-ended (`talk Lio`), directed
(`talk Lio, what waits outside?`), probing their want, lying, promising. Always `wait` after
significant dialogue to let claim extraction land.

Mechanical: claim intake packet (claim + speaker memory + visible-claim rumor) commits together;
extracted claims appear in `journal` with source/confidence provenance; `debug.wants` shows the
NPC want and dialogue visibly leans into it; want changes route through `update_want` with memory
children; faulted extractions become technical-failure records, not silent drops.

Feel: does the NPC have direction, or does it feel like a chatbot? Does the want create
*organic quest* pressure without quest-giver machinery? Does leverage feel like understanding a
person, not paying a meter? Quote the best and worst dialogue exchange.

### Session 7 — Gifts, bonds, recruitment, followers [live]

`give grave salt to Lio`, gifts aligned vs misaligned with the want, `recruit`, `bonds`,
`followers`; then behave badly toward an ally and watch for drift.

Mechanical: gift packet (transfer + memory) atomic with `giftSkipped` rollback; recruitment
commits faction/control/bond/memory/message together; followers follow across zone travel;
`bonds` reports qualitative posture while debug holds raw values.

Feel: do bonds read as relationships rather than stat bars? Does the gift → memory → dialogue
hand-off surface later ("you gave me…")? Is betrayal/drift perceptible before it happens?

### Session 8 — Claims, promises, and payoff [both]

The promise pipeline end to end. Collect promises from all five source archetypes: dialogue
(Lio's reed-town), documents (containment notice), props (brazier), deeds, and player spells
(`cast promise that…`). Then travel to fresh zones and watch realization.

Mechanical: promise cards in `view.promises` carry status/kind/trigger/salience/source and
recent eligibility failures; destination-zone payoffs stage against real global ledgers and
commit canon/memory/rumor/scheduled-event writes only on success; `promiseRealizationPlan`
deltas stay agent/debug-only; rejected realizations leave `promiseRealizationSkipped` and an
intact promise, not a half-built site.

Feel: the core magic trick. When a confided site realizes as a generated town, does it feel
*promised* — does the narration connect it back to the confidence? Do payoffs land "later but
not always where expected" in a way that reads as fate rather than randomness? Are unrealized
promises kept warm in the journal so the player still cares?

### Session 9 — Rumor transport and distortion [both]

Create a public deed (flashy cast with witnesses), then travel/wait repeatedly. Track the rumor
via `rumors` (player-heard) and `debug.rumors` (full cards). Let a high-hop rumor queue a
`rumor_distortion` background job and come back distorted.

Mechanical: non-secret deeds mint rumors with duplicate-source protection; propagation is
bounded per pump and hop metadata is consistent; carrier selection favors narratively primed
NPCs; new local carriers get hidden heard-memories; stale rumors fade from player views; the
propagation packet rolls back cleanly (`rumorPropagationSkipped`); distortion jobs apply through
`update_rumor` with `distortion:` history, or fail without half-mutating; completed-but-unapplied
jobs resume at the next pump.

Feel: the signature vignette — do you eventually hear a *version* of your own deed, grown in the
telling? Is the distorted retelling in the region's voice, exaggerated along the teller's fears?
Does hearing what the world has wrong about you delight?

### Session 10 — Visibility, stealth, and witness gating [live]

Same deed under different visibility: secret (no witnesses), suspicious, witnessed, public.
Use disguise/concealment magic (`cast hide me in river-color`) before acting. Kill or spare
witnesses. Compare downstream: rumors minted, memories written, standing shifts, patrol pressure.

Mechanical: visibility classification actually gates rumor minting, witness memories, legend,
and faction response; suspicion records (`record_suspicion`) appear where identity is uncertain;
identity/attribution respects disguise and body swap (a deed done in a stolen body should
attribute to that body's identity where the witness model says so).

Feel: is stealth *real play* — can the player predict and manipulate what will be known? Does
getting away with something feel earned? Is the difference between "no one saw" and "someone
suspects" legible?

### Session 11 — Factions, pressure, standing, and laying low [both]

Escalate against the Empire: public magic, freed prisoners, dead soldiers. Watch `standing`,
`journal` pressure lines, patrol/warrant responses. Then lay low: `wait` and quiet travel for
many turns, verifying recovery.

Mechanical: faction responses are resource expenditures, not thresholds — check debug faction
resources drain and recover; `faction_pressure` world-turn moves commit their compound packets
atomically; deed-free pumps run `faction_recovery` (laying low refills patrol/warrant pools) but
deed turns don't recharge the Empire for free; standing stays bounded.

Feel: does the Empire feel like a competent adversary with a budget — does pressure *relent*
believably when you go quiet? Is "the ferryman won't meet your eyes" legibility present, or is
standing just a number? Do warrants/patrols create dread rather than annoyance?

### Session 12 — The world-turn: does the world move first? [both]

Long passive play: 30+ turns of waiting, short travel, minor actions. Catalog every
`debug.worldTurns` card: `rumor_spread`, `promise_stir`, `want_stir`, `faction_pressure`,
`faction_pressure_blocked`, `faction_recovery`.

Mechanical: world-turn budget is bounded per pump; compound moves roll back wholly on any child
rejection (`worldTurnSkipped`); `want_stir` memories never surface as player-facing journal
truth; player-facing messages filter audit wrappers correctly (no duplicate narration).

Feel: does the world feel like "a place that has decided to have opinions about you," or a
mirror that only answers? Are the world's own moves perceptible in-fiction at the right rate —
neither invisible nor spammy?

### Session 13 — Travel, worldgen, regions, and lore texture [live]

Travel widely in one run: `travel` in all directions, `atlas` at each stop, `examine` generated
fixtures twice (before/after background detail lands), cast region-flavored magic
(`cast ask the reed shrine to hide me under river-color`). Repeat with two different seeds and
compare atlas output; repeat one seed twice and confirm identity.

Mechanical: same seed ⇒ same realm status/ruler/imperial grip; zone re-entry preserves prior
state; edge-step travel works and followers come along; generated zones are built through the
detached sandbox (no orphan entities, no player-visible generation failures — rejected children
become skipped content); fixtures have concrete region-colored names; second `examine` shows
applied known detail.

Feel: color under marble — do regions feel culturally specific and lush, or like tile salads?
Does the atlas surface lore worth reading? Does generated place-texture give free-form magic
"something to bite into"? Is travel narration doing work or filler?

### Session 14 — Merchants, trade, and services [both]

`wares`, `buy`, `sell`, `services`, `request <service> from <provider>`; services that complete
provider wants; trading with an NPC whose bond/want you've shaped.

Mechanical: `execute_trade` moves gold and items correctly; `request_service` commits effect +
payment + want completion + memory + narration atomically (`serviceRequestSkipped` rollback);
successful requests consume a turn via the command wrapper; dialogue-revealed services produce
one `offer_service` mutation and one claim update, not duplicates.

Feel: does commerce feel embedded in the social fabric (a service satisfying a want reads as a
favor between people) or like a vending machine? Are prices/stock coherent with region and story?

### Session 15 — Readables, books, and canon [mock]

`read` every readable encountered; check journal and canon afterwards. Verify readable text and
payoff receipts write lore through `add_canon`, `ClaimSourceComponent` seeds record through the
shared pipeline (`claimSeedSkipped` on failure), and re-reading doesn't duplicate claims.

Feel: is reading rewarded — do documents feel like dormant mechanics (claims you can chase)
rather than flavor text?

### Session 16 — Background jobs under pressure [mock]

`--max-background-jobs 2 --background-jobs-per-turn 1`, then generate demand: examine several
fixtures, mint distortion-eligible rumors, `jobs` every turn. Also run a session with
`--disable-background`.

Mechanical: queue limits respected; foreground never starves; stale running jobs fail visibly
instead of blocking duplicates; deterministic fallback fills in when the provider is disabled;
durable content surfaces through `examine`/world views, never as raw queue narration.

Feel: with background disabled, what texture is lost? Is the game still whole (deterministic
skeleton stands with zero model calls)?

### Session 17 — Persistence, replay, and long-run stability [mock]

Save/load at awkward moments (mid-pending-cast, after unrealized promises, with live rumors and
scheduled events). Replay full transcripts with `--replay --replay-assert-final`. Then run the
episode runner: `--episode --episodes 5 --max-turns 40 --episode-log ...` and the spell eval
harness `--eval`.

Mechanical: byte-stable save→load→save; replay consumes materialized outputs without model
calls and re-validates spell JSON against replay state; final-summary counts (promises, claims,
rumors, world turns, bonds, memories) match; episode invariants all pass; eval corpus selects
expected operation families.

### Session 18 — Death, chronicle, and run completion [both]

Die on purpose (pick a fight while weak). Also complete a run objective if reachable. Check the
chronicle and `update_run_status` behavior.

Mechanical: run completion commits status + closeout narration + chronicle canon atomically
(`runCompleteSkipped` rollback); completed runs have chronicles; death produces a coherent
end-state, not a hung session; no meta-progression leaks into the next run.

Feel: does death read as a story ending (worthy of a chronicle) or a whiff? Does the closeout
prose honor what actually happened in the run?

### Session 19 — The capital and the emperor [live]

Travel to the capital (`travel east` repeatedly per the phase-8 smoke). Big magic against the
throne (`cast make the emperor's marble shadow kneel and crack`), standing checks, infiltration
attempts via body swap/disguise, and observing imperial resource depth.

Mechanical: the capital is the same machine at scale — no bespoke finale code paths; emperor and
Empire respond through ordinary deeds/factions/witnesses; anything you could do in the
containment yard works here.

Feel: the anti-port acceptance test. Does the endgame feel like the flywheel at full speed —
dense witnesses, deep pools, tight paper — or like a boss room? Does drained faction pressure
from earlier play visibly matter here?

### Session 20 — Emergent chain hunts (free play) [live]

The capstone: 3–5 long free-play runs (40+ turns) with no script, each chasing one flywheel
vignette end to end. Candidate chains:

- witnessed spell → rumor → distortion → a stranger who has heard of you reacts/recruits
- gift → memory → bond → confided secret → promise → generated place → arrival narration that
  remembers the confidence
- theft/evidence claim → merchant debt/item site realized → recovered item used in a spell
- promise spell on an object ("let this door remember me") → later callback
- rescue → follower → follower's want → helping it → devotion threshold
- crime in a stolen body → attribution lands on the body's identity → consequences for the
  original owner

Mechanical: every hand-off in a chain should be findable as typed consequences in the transcript
— if a link happened "for free" outside the grammar, that's a bug; if a link silently dropped,
that's a bug.

Feel: the whole game's thesis. For each chain, log: did it complete? where did it stall? could
you *tell the story afterwards* from player-facing output alone? Rate: could you distinguish
authored from emergent? (Ideal answer: no.)

---

## Cross-Cutting Feel Rubric

Maintain a running scoreboard in `FEEL_LOG.md`, updated after each session:

| Axis | Question |
|---|---|
| Authorship | Do outcomes trace to *my words and choices*? |
| Wonder | Did anything make me want to screenshot the prose? |
| Mischief | Does the game reward being a pest in clever ways? |
| Tactical | Are fights/escapes solvable by thinking? |
| Legibility | Does every perceived consequence carry its cause in-fiction? |
| Pacing | Turn latency, dead turns, spam, boring stretches |
| Tone | Wonder-with-teeth, color-under-marble — or generic fantasy / grimdark drift? |
| Resolver quality | Specificity, consequence, surprise vs boring-but-valid rate |

Also track two named failure modes from SYSTEMS_VISION as standing watch items:
**ledger sprawl** (lanes that work but never cross-read — note any ledger whose contents never
influenced anything you saw) and **combat-as-latency** (any session where waiting on the model
was the dominant experience of magic).

## Bug Triage Workflow

1. Capture: transcript path, seed, provider/model, command index where it appeared.
2. Reduce: shortest reproducing `--command` script; prefer mock provider; use `--replay` on the
   live transcript when the bug depends on a specific model output.
3. Diagnose against the pipeline: parse → repair → normalize → validate → preflight → apply →
   state-validate → results/audit. Name which stage failed.
4. Fix in the engine, never in the renderer/prompt layer, and never as a one-off handler for one
   spell phrase.
5. Add a focused test (turn consumption, rollback, target binding, action results, CLI parity —
   per AGENTS.md testing expectations). Run `dotnet test` and the `--eval` corpus.
6. Log it in `BUG_LOG.md`, re-run the session that found it, and re-run any earlier session the
   fix could plausibly affect.

Resolver *quality* problems (valid but boring, dialect repairs, target mistakes) are not bugs to
patch with hard-coded fallbacks — log them with the audit-log evidence
(`logs\wild_magic_audit.jsonl`) as feel/resolver findings.

## Suggested Schedule

- **Pass 1 (mechanics-heavy):** Sessions 1, 2, 5, 15, 16, 17 — establish a stable baseline,
  fix what falls out.
- **Pass 2 (systems depth):** Sessions 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14 — one system per
  sitting, live provider, feel log growing.
- **Pass 3 (endgame + emergence):** Sessions 18, 19, 20 — long runs, chain hunts, scoreboard
  finalized.
- After any significant engine change: re-run the eval harness, a 5-episode mock run, and the
  most relevant numbered session.

## Final Report

When a full pass completes, write `logs\playtest\REPORT_<date>.md`: sessions run, bugs found and
fixed (with tests), the feel scoreboard with trends, the top five feel findings with concrete
smallest-general-change suggestions, the boring-but-valid rate, and which flywheel chains
completed end to end. That report — not the raw logs — is the artifact a designer should read.
