# Playtest Fix Plan — remaining findings from the 2026-07-03 pass

Status: implementation handoff, 2026-07-03. Companion to
[PLAYTEST_PLAN.md](PLAYTEST_PLAN.md) (methodology), `logs/playtest/BUG_LOG.md` /
`logs/playtest/FEEL_LOG.md` (raw findings), and `logs/playtest/REPORT_2026-07-03.md`
(synthesis). Five engine bugs from that pass are **already fixed and tested** (open-attempt
promise realization + trigger-hint corruption, object-shaped `target/x/y`, unbindable
document-sourced promises, replay re-truncation, chronicle grammar) — do not redo them; their
regression tests must stay green. This document plans everything that was *flagged but not
fixed*.

All design decisions below are made. The implementer should not need to resolve open questions —
if something turns out to contradict the code, stop and flag it rather than improvising a
different design.

## Ground rules (binding, from AGENTS.md)

- Engine owns truth; fixes live in `Sorcerer.Core`/`Sorcerer.Magic`, never in renderer or prompt
  code masquerading as rules.
- World mutations go through typed consequences at the shared apply point. None of the
  workstreams below should need a new consequence type.
- No one-off handlers for a single spell phrase. Every fix here is a general rule.
- Technical LLM failures stay turn-free; intentional rejections stay turn-consuming. Workstream B
  changes rejection *messages*, never their turn accounting.
- CLI and GUI must stay in lockstep: message/view changes must flow through the shared
  `ActionResult`/`GameView` paths (they all do below; workstream E3 is the only one that touches
  a view builder — update both surfaces).
- After each workstream: `dotnet test` (468+ green), `dotnet run --project src/Sorcerer.Cli --
  --provider mock --eval` (44/44), and the workstream's own re-verification command (given
  below). Finish a workstream with a 5-episode mock smoke:
  `dotnet run --project src/Sorcerer.Cli -- --provider mock --episode --episodes 5 --max-turns 40`.
- Update `logs/playtest/BUG_LOG.md` entries with fix status when a flagged item lands.

Suggested order: **A → B → C → D → F → E.** A is the headline; keep it an isolated change.

---

## Workstream A (P0) — Concealment must reach the witness model

**Finding** (BUG_LOG "DESIGN GAP", FEEL_LOG [10]): a successful stealth spell ("hide me in
shadow and silence") applied a status but the subsequent deed still recorded
`visibility: "public"` with distant soldiers as witnesses. Stealth is currently flavor for the
deed/witness lane.

**Key discovery that shrinks this task** (verify these anchors before starting):

- `StatusRegistry.CreateDefault()` ([StatusRegistry.cs:101-106](../src/Sorcerer.Core/Status/StatusRegistry.cs))
  already defines a canonical `concealed` status with `ConcealsBearer: true` and aliases
  (`hidden`, `shadowed`, `veiled`, `mist_cloaked`, `camouflaged`, ...). Wild-magic statuses pass
  through `engine.Statuses.Canonicalize` on apply
  ([WorldConsequenceApplier.ApplyApplyStatus](../src/Sorcerer.Core/Consequences/WorldConsequenceApplier.cs)),
  and `Canonicalize` does substring alias matching — so the session-10 spell's `shadow_hidden`
  **already landed as canonical `concealed`**. No resolver or status work is needed.
- `AiSystem.CanNoticeTarget` ([AiSystem.cs:172-187](../src/Sorcerer.Core/Engine/Systems/AiSystem.cs))
  already implements the notice rule: a bearer with an active conceals-bearer status is only
  noticed within **distance 2**. So AI targeting already respects concealment; only witnessing
  doesn't.
- `PerceptionSystem.WitnessesOf` ([PerceptionSystem.cs:47](../src/Sorcerer.Core/Engine/Systems/PerceptionSystem.cs))
  checks only living/sight-radius/line-of-sight — never the subject's statuses.
- `GameEngine.PlanDeedCapture` ([GameEngine.cs:596](../src/Sorcerer.Core/Engine/GameEngine.cs))
  computes `actorWitnesses = WitnessesOf(origin, actor.Id)` and
  `effectWitnesses = WitnessesOf(effectPoint, actor.Id)`, and **already** records suspicion when
  `actorWitnesses == 0 && effectWitnesses > 0`.
- `WorldReactionSystem.ClassifyVisibility` ([WorldReactionSystem.cs:435-455](../src/Sorcerer.Core/World/WorldReactionSystem.cs))
  already downgrades on witness counts: 0 actor-witnesses → `suspicious` (if effect seen) or
  `secret`. Rumor minting, witness memories, legend, and faction response all key off that
  classification downstream.

**Therefore the whole fix is:** filter *actor*-witnesses by the same concealment rule the AI
already uses, in one shared place. The classifier, suspicion lane, rumors, legend, and standing
all follow automatically.

**Design decisions:**

1. **One rule, one place.** Give `PerceptionSystem` the `StatusRegistry` (constructor param;
   wire at [GameEngine.cs:50](../src/Sorcerer.Core/Engine/GameEngine.cs) where `_statusRegistry`
   is already in scope). Add:
   - `public const int ConcealedNoticeRadius = 2;`
   - `public bool CanPerceiveSubject(Entity witness, Entity subject)` — returns false only when
     the subject has an active conceals-bearer status, no active `revealed` status, and the
     witness is farther than `ConcealedNoticeRadius`. "Active" means the same
     `IsStatusActive`-style expiry check AiSystem uses (status `ExpiresTurn` vs current turn).
   - Refactor `AiSystem.CanNoticeTarget` to delegate to this helper so there is exactly one
     concealment rule in the codebase. Preserve its current behavior otherwise.
2. **`WitnessesOf` gains an optional subject:**
   `WitnessesOf(GridPoint point, EntityId? exclude = null, Entity? subject = null)`. When
   `subject` is non-null, each candidate witness must also pass `CanPerceiveSubject(witness,
   subject)`.
3. **Only actor-witnesses are filtered.** In `PlanDeedCapture`, pass the actor as `subject` for
   the *origin* call only. Effect-witnesses stay unfiltered — an explosion is visible even when
   the caster isn't. This is what makes concealed-but-loud magic land as `suspicious` rather
   than `secret`, which is the correct fiction.
4. **`revealed` counters `concealed`.** The registry already has a `revealed` status that
   nothing consumes; this gives it mechanical meaning (and gives counterplay: an NPC or ward can
   `apply_status: revealed` through the ordinary consequence grammar to strip stealth).
5. **Out of scope, explicitly:** hiding NPCs from the *player's* FOV (player-side perception is
   untouched); disguise-as-misattribution (visible but unidentified is a different axis —
   attribution status already exists and is not touched here); any new status names or resolver
   prompt changes.
6. **`PlanEffectSuspicion`** keeps calling `WitnessesOf` without a subject (the effect is not
   concealed). No change.

**Tests** (new, in the existing engine-test style):

- Concealed actor (active canonical `concealed`), witnesses at distance > 2 with clear LOS,
  deed with an effect point → deed `Visibility == "suspicious"`, no `deedWitnessMemory` deltas
  for those witnesses, no rumor minted, no legend/standing shift.
- Same setup, no effect point (or no effect witnesses) → `Visibility == "secret"`.
- Witness at distance ≤ 2 → still an actor-witness; visibility `witnessed`.
- Concealed **and** `revealed` both active → concealment ignored; visibility as if unconcealed.
- Expired concealed status (ExpiresTurn passed) → unconcealed behavior.
- `AiSystem` regression: hostile AI still ignores a concealed player beyond distance 2 and
  notices within 2 (add if no existing test covers `CanNoticeTarget`).

**Re-verification (live):**
```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu --json --debug-state --seed 1010 `
  --transcript logs\playtest\fixA_stealth_verify.jsonl `
  --command "cast hide me in shadow and silence" `
  --command "journal"
```
Expected: the cast's own deed (soldiers are 6+ tiles away at seed 1010's opening positions)
records `suspicious`/`secret` in the `recordDeed` delta, **not** `public`; no "A rumor begins"
for it; no legend gain in `journal`. Note the *first* cast is itself the deed under test —
its status applies before its own deed capture? **Check ordering**: `PlanDeedCapture` runs after
effects apply in `WildMagicController`, so the just-applied `concealed` status *does* cover the
casting deed. That is accepted and desirable ("the spell hides its own casting"); if ordering
turns out otherwise in code, keep code behavior and adjust the test to cast twice.

**Docs:** update [EMERGENT_WORLD.md](EMERGENT_WORLD.md) (witness model) and
[SEMANTIC_EFFECTS.md](SEMANTIC_EFFECTS.md) with the crystallized rule: *concealment is the
canonical `concealed` status family; it gates actor-witnessing and AI notice beyond radius 2;
`revealed` counters it; effects are never concealed.* Also note it in
[CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md) only if that doc discusses stealth.

---

## Workstream B (P1) — Targeting rejections must say why

**Finding** (FEEL_LOG [03], [04], [19] — three independent sessions): every failed target
resolution surfaces as bare "No target is selected.", whether the real cause was (a) the model
chose `selected_target` and nothing is selected, (b) the named target isn't visible/known, or
(c) the phrasing needed an explicit `target` first. Players and agents can't distinguish these.

**Anchors:** `ReferenceBinder.cs:184` and `:365`
([ReferenceBinder.cs](../src/Sorcerer.Core/References/ReferenceBinder.cs)) are the two
"No target is selected." sites; named-entity resolution failures elsewhere in the same file
produce their own `BoundReference.Failure` / `ResolvedEntitySet.Failure` strings — survey all
`Failure(` call sites in that file while in there.

**Design decisions:**

1. Replace the two selected-target failures with:
   `"No target is selected. Choose one with 'target <x> <y>' or name something you can see."`
   (Same string both sites; it reaches the player through the ordinary rejection path.)
2. Named-target failures become: `"Nothing you can see answers to '<name>'."` — and this message
   **must be identical whether the entity doesn't exist or exists but is hidden from the
   player** (session 19's emperor was `hidden_from_player`; the message must not leak that a
   hidden entity is real). Verify the failure path cannot distinguish them from the player's
   perspective.
3. Do not change turn accounting: these remain intentional rejections (turn consumed) exactly as
   now. Do not change which references resolve — messages only.
4. Optional stretch (separate commit, only if cheap): one guidance line in the wild-magic prompt
   builder (`Sorcerer.Magic`) telling the model to prefer `nearest_enemy`-style references over
   `selected_target` when the spell text says "nearest". Do **not** add engine-side rewriting of
   model output for this.

**Tests:** unit tests on `ReferenceBinder` for each failure string; one `GameSession` cast test
asserting the new selected-target message appears in `ActionResult.Messages` and
`ConsumedTurn == true`; one asserting the named-target message is byte-identical for a
nonexistent name vs. a real-but-hidden entity id.

**Re-verification:** re-run the session-19 command set (seed 1919, two `travel east`, the
emperor cast) and confirm the rejection now reads as the named-target message.

---

## Workstream C (P1) — Near-duplicate dialogue claims should not fork parallel promise/rumor threads

**Finding** (FEEL_LOG [06]): the model repeated the Jimmer fact in different words; the claim
pipeline minted a second claim, second bound promise, and second rumor for the same fact
(claim_1/claim_3, promise_2/promise_4, rumor_2/rumor_4). No invariant broke, but it's concrete
ledger-sprawl.

**Anchor:** `GameSession.MatchingActivePromise`
([GameSession.cs:3453](../src/Sorcerer.Core/GameSession.cs)) — currently requires
`promise.Text.Equals(record.Text)` (exact, case-insensitive). Everything downstream of a match
already works: the matched path emits "A repeated claim points back to an existing promise",
links the claim via `claimPromiseLinked`, and merges trigger/realization via
`ShouldRebindExistingPromise`/`MergeRealizationKind`. **Reuse that path; add no new lifecycle.**

**Design decisions:**

1. Extend `MatchingActivePromise` with a fuzzy second layer, tried only when the exact-text layer
   finds nothing. A promise matches when **all** hold:
   - not `cleared`/`realized` (as now);
   - `PromiseRealizationKindsCompatible` and `TriggerHintsOverlap` (as now);
   - same speaker (`promise.SourceSpeakerId` equals the incoming speaker id) **or** same
     normalized non-empty subject;
   - significant-token overlap of the two texts ≥ **0.6 Jaccard**. Implement one small
     deterministic helper: lowercase, strip punctuation, split on whitespace, drop tokens shorter
     than 4 chars, Jaccard = |intersection| / |union|. (For the session-6 pair this scores well
     above 0.6; for genuinely different facts from the same speaker — "Jimmer sells blades" vs
     "an old oak marks the drainage tunnels" — it scores near 0.)
2. Guardrails: never fuzzy-match across different speakers *and* different subjects; never match
   incompatible realization kinds; keep the threshold a named constant with a comment.
3. Claim records themselves may still be recorded (a restatement is real evidence; confidence
   may differ) — the dedup is at the *promise/thread* level, which is where sprawl hurts.
4. Optional stretch (separate commit): when a claim fuzzy-links to an existing promise, skip
   minting a fresh visible rumor if an active rumor already points at the linked promise's
   source claim; the restatement then only re-propagates the existing rumor. Only do this if the
   rumor-mint call site makes it a ≤10-line change; otherwise leave rumors alone and note it.

**Tests:**

- Two same-speaker claims with reworded identical facts → one promise; second claim ends
  `promised` pointing at the first promise's id; "repeated claim points back" delta present.
- Same speaker, different facts (low overlap) → two promises (no overreach).
- Different speakers, similar text, no shared subject → two promises (guardrail).

**Re-verification (live):** re-run session 6's command set (`--quickstart social`, seed 6006,
the four `talk Lio` variations) and confirm the journal shows one Jimmer thread, not two — model
nondeterminism means eyeball the journal rather than assert exact counts.

---

## Workstream D (P2) — Out-of-range interaction failures should point at the target

**Finding** (FEEL_LOG [01]): `read`/`examine`/`talk`/`open`/`pickup` require adjacency and fail
with generic "nothing readable within reach" strings even when the target is plainly visible a
few tiles away; both agent and human wasted turns rediscovering the map.

**Anchor:** `InteractionSystem.ResolveNearbyEntity` / `NearbyCandidates`
([InteractionSystem.cs:1267-1309](../src/Sorcerer.Core/Engine/Systems/InteractionSystem.cs))
and each caller's failure message.

**Design decisions:**

1. Add one helper on `InteractionSystem`:
   `private string? OutOfReachHint(string? target, Func<Entity, bool> predicate)` — searches the
   same predicate over entities that are **currently visible to the player** (use
   `PerceptionSystem.SnapshotForControlled().VisibleEntityIds` — perception-bound, so hidden
   entities are never leaked), at any distance, nearest first; honors the same name/id/tag
   matching as `ResolveNearbyEntity` when `target` is provided. Returns e.g.
   `"posted containment notice is out of reach — 3 tiles southeast."` (8-way compass direction
   from the controlled body; distance = Chebyshev, matching movement).
2. Wire it into the failure branches of `Open`, `Read`, `Examine`, `Talk`, and `Pickup`: when the
   hint is non-null, use it (or append it) instead of the bare generic string; when null, keep
   the current generic message exactly (don't churn existing test expectations for the
   no-candidates case).
3. These failures stay non-turn-consuming, exactly as now.

**Tests:** read-from-3-tiles-away names the notice with direction and distance; the same command
with no visible readable anywhere keeps the old message; a hidden-but-matching entity produces
the old generic message (no leak).

---

## Workstream F (P2) — Verification and documentation debts

1. **Lock in Bug [03]'s generality.** The fix routes claim-seed promises through
   `GameSession.ShouldBindToRegion`, which covers `site`/`town`/`landmark`/`item`/`person`/
   `threat`/`merchant_stock`/`stock`/`trade`/`service`/`escape_route`/`door_rule`/`route` — but
   only `escape_route` was exercised. Add a parameterized test: authored `ClaimSourceComponent`
   seed with `BindAsPromise: true, PromiseKind: "rumor", TriggerHint: "travel"` for each of
   `site`, `person`, `item`, `merchant_stock` → after `read`, promise `Status == "bound"` with
   `BoundPlace` set.
2. **Terrain "blind spot" truth.** Session 15 noticed the `imperial_cover` region trait text
   ("official blind spots"). Investigate whether any region trait feeds `HasLineOfSight`/
   `WitnessesOf` today (almost certainly narrative-only). Outcome is a **doc note**, not a
   feature: state in [EMERGENT_WORLD.md](EMERGENT_WORLD.md) whether cover is currently
   positional-LOS only, so nobody mistakes trait prose for mechanics. Implement nothing unless
   a trait is already half-wired.
3. **CLI post-conclusion behavior.** Session 18: after a run concludes (`ShouldQuit`), remaining
   `--command` batch entries are intentionally skipped. Document this in
   [CLI_AND_AGENT_PLAYTESTING.md](CLI_AND_AGENT_PLAYTESTING.md) (agents should branch on
   `runStatus`/`ShouldQuit` rather than expect post-mortem commands to run). Doc-only; do not
   change the loop.

---

## Workstream E (P3) — Small polish batch (each item independent; skip any that fights back)

1. **Free casts should say so.** Verified: `SpellCostApplier`
   ([SpellCostApplier.cs](../src/Sorcerer.Magic/Costs/SpellCostApplier.cs)) emits per-cost
   "Cost:" lines, so an empty `costs` array means a genuinely free cast with no line at all
   (FEEL_LOG [03] ambiguity). In `WildMagicController`, after `SpellCostApplier.Apply`, if the
   cast was accepted and produced zero cost deltas, emit one message: `"Cost: nothing."` through
   the same message pathway as other cost lines. Check the eval harness doesn't assert exact
   message lists (it checks operation families; should be unaffected).
2. **Bump-attack framing.** [MovementSystem.cs:105](../src/Sorcerer.Core/Engine/Systems/MovementSystem.cs)
   converts a blocked move into an attack with `Action = "attack"` but no framing beat
   (FEEL_LOG [01]). Prepend one message before the strike line, e.g.
   `"Your step becomes a strike."` — exact wording implementer's choice, but it must read as the
   *player's* transition from walking to fighting, stay one line, and appear only on
   move-resolved-as-attack (not explicit attacks).
3. **Borrowed-body legibility.** (FEEL_LOG [02]) When the controlled entity has an active
   `borrowed_body` status (the swap packet applies it; aliases `soul_swapped`/`possessed`),
   append one line to the `character` output: `"You wear this body; the mind and magic are your
   own."` Wire it in the shared character-view builder so CLI `character` and the GUI sheet both
   get it (parity rule) — find where `character`/`sheet` text is built (the CLI command and
   `GameView.Character` share a helper per the docs; if they don't, fix in both).
4. **Resolver grammar nit** ("You gains fire_resistant", FEEL_LOG [03]): add one line of prompt
   guidance in the wild-magic prompt builder about writing `message`/`outcomeText` in correct
   person/number. Explicitly do **not** add engine-side text rewriting. If the prompt builder has
   an evals/snapshot test, update it.

---

## Explicitly not in scope

- Disguise/misattribution mechanics (visible-but-unidentified) — future design; workstream A
  deliberately leaves `AttributionStatus` semantics untouched.
- Hiding NPCs from the player's own FOV via concealment.
- Rumor-layer changes beyond C's optional stretch.
- Any auto-pricing of spell costs (AGENTS.md forbids early spell-budget pricing; E1 is a
  message, not a pricing rule).
- The "combat-as-latency" watch item (no finding yet; nothing to build).

## Definition of done

- All workstreams A–D and F landed with their tests; E items landed or individually waived with
  a note in BUG_LOG.
- Full suite green (was 468), eval 44/44, 5-episode mock smoke passes.
- Re-verification commands for A, B, C run with transcripts saved under `logs/playtest/` and a
  short outcome note appended to the corresponding FEEL_LOG/BUG_LOG entries.
- EMERGENT_WORLD.md, SEMANTIC_EFFECTS.md, and CLI_AND_AGENT_PLAYTESTING.md updated per A and F.
