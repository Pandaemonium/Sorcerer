# Phase 0.1 Baseline & Dependency Map

Status: **complete — 2026-07-13.** This is the evidence note the execution order in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) requires before Phase 0.2 (decompose
`WorldConsequenceApplier` by cohesive family). It pins current behavior strongly enough that the
0.2 structural movement cannot silently alter turns, messages, rollback, replay, or provider
semantics, and it is the checked-in architecture inventory the package asks for.

The inventory below is a map to reveal duplicates, **not** a second registry of behavior. The
authoritative, executable version of the consequence-family half lives in
`tests/Sorcerer.Tests/ConsequenceCatalogTests.cs`, which fails if the catalog drifts.

## Baseline metrics (pinned reference point)

| Metric | Value | How measured |
|---|---|---|
| Build | clean, 0 warnings / 0 errors | `dotnet build tests/Sorcerer.Tests -c Debug` |
| Full suite (pre-0.1) | **766 passing**, ~12.3 s | `dotnet test --no-build` |
| Full suite (post-0.1) | **927 passing** (+161 catalog cases) | same |
| Reference scenario | `GameSession.CreateImperialEncounter()` (Marble Containment Yard opening) | only shipped opening; sole quickstart scene is `social` |
| Reference seed | **7** | used by `EpisodeRunnerTests`, transcript replay |
| Save schema version | **1** | `GameSaveService`, `schemaVersion` field |
| Save size (reference seed, 36-turn mock move/wait episode) | **272,322 bytes** (~266 KB) | CLI `--provider mock --seed 7 --script … save` |
| Save size composition | terrain grid dominates (1,200 tiles) | see state counts below |

### Long-episode state counts (reference seed 7, turn 36, mock provider)

Serialized `state` carries **29 durable collections**. After a movement/wait episode:

- `terrain` 1200, `messages` 23, `entities` 15, `souls` 4, `factions` 4, `memories` 2,
  `worldTurns` 2 (bounded world-turn audits), `promises` 1, `zones` 1, `exploredBySoulId` 1.
- Empty in this episode (populated by social/magic/combat play instead): `deeds`, `claims`,
  `rumors`, `bonds`, `suspicions`, `canonRecords`, `scheduledEvents`, `triggers`,
  `persistentEffects`, `tileFlows`, `echoes`, `backgroundJobs`, `legendTags`, `worldFlags`,
  `blockingTerrain`, `terrainExpirations`, `appliedDeedIds`, `backgroundSettings`,
  `lastControlledMoveDelta`.

Boundedness reading: over 36 turns the world-turn budget produced 2 audited moves and did not grow
entities or ledgers unboundedly. This is the reference figure to compare against after 0.2 and in
the Phase 2 soak.

### Floor-model latency (cite, do not re-measure here)

The per-purpose floor-model p50/p95 baseline already exists in
[OPTIMIZATION_PLAN.md](OPTIMIZATION_PLAN.md) (§ "DONE — 2026-07-07 live telemetry check",
`qwen3.5:9b-q4_K_M`, CPU): warm wild casts ~20–25 s after a one-time ~23 s cold load; cast prompts
~4,200–4,500 tokens (prompt-eval bound, ~112 output tokens); dialogue speech ~16 s at
~1,184–1,540 prompt tokens. The p50/p95 tables are in that doc's item 10.

Note: the local Ollama box currently serves `llama3.2:3b` (smaller than the pinned floor model), so
a fresh sweep would measure a different model. Re-run the per-purpose sweep against the pinned floor
model as part of Phase 1.4 telemetry consolidation, not here — 0.1 is behavior-preserving.

## New safety net added in 0.1

`tests/Sorcerer.Tests/ConsequenceCatalogTests.cs` (161 cases). It pins the **observable dispatch
contract** that Phase 0.2's `ConsequenceFamilyRegistry` must preserve, testing behavior rather than
the switch's shape so it survives the refactor unchanged:

- **Catalog drift guard** — the exact set of 72 canonical consequence types is pinned; adding or
  removing one forces a conscious test update (the "exactly one family owner" intent).
- **Per-type dispatch pin** (72 cases) — applying a minimal consequence of every canonical type
  through the real engine apply point routes to a handler and never falls through to the
  unknown-type reject.
- **Known/idempotent pin** (72 cases) — every canonical type is `IsKnown` and a fixed point of
  `Normalize`, catching drift between the constants, `IsKnown` chain, and normalize table.
- **Alias pin** (15 cases) — semantic aliases (`raise_dead`, `free_prisoner`, …) and format
  variants (casing, separators, spacing, padding) collapse to the intended canonical type.
- **Reject pin** — an unknown type is not known, normalizes unchanged, and is rejected with the
  unknown-type error at the apply point.

## Architecture inventory (duplicate-revealing map)

**Command spine** — ~50 `GameCommand` records (`src/Sorcerer.Core/Commands/GameCommand.cs`):
move/travel/wait, cast/beginCast/awaitCast/cancelCast/charter/echo, target/clearTarget/focus,
talk/give/recruit, buy/sell/wares/trade, request/services, read/examine/open/enter/leave,
pickup/drop/equip/unequip/useItem, possess, journal/rumors/standing/bonds/followers/atlas/map/
inspect/reagents/jobs, save/load, protectItem/unprotectItem, character/help/quit/unknown. All route
through `GameSession.ExecuteAsync`; CLI and Godot must share this path (AGENTS.md "Do Not Split The
Game").

**Consequence spine** — **72 families** through `GameEngine.ApplyConsequence` →
`WorldConsequenceGuard` → `WorldConsequenceApplier.Apply`'s single `normalizedType switch`. This is
the 7,656-line hotspot 0.2 will split. The four parallel lists that must stay in sync — the 72
constants, the `Normalize` alias table, the `IsKnown` chain, and the dispatch switch — are exactly
the drift risk `ConsequenceCatalogTests` now guards. Grouped by the cohesive families 0.2 proposes:
tactical (damage/heal/mana/actor-resource/status×3/resistance/weakness/delayed-damage×2), space
(move/terrain×2/flow×2/open_or_unlock/create_route), entity lifecycle (spawn×3/transform/tags×2/
behavior×2/control/set_controlled/swap_souls/animate/persistent_effect×2), inventory/trade
(modify_inventory/transfer/equipment/merchant_stock/trade×2/service×2), social records
(memory/edit_memory/bond/want/claim×2/rumor×2/deed×2/legend/canon/suspicion×2), scheduling
(promise×2/schedule×2/trigger×2/background_job×2), world/run (world_flag/run_status/selected_target/
exploration/faction_standing/faction_resource/change_faction/record_world_turn/message), compound
(`free_captive`).

**Time spine** — `TurnSystem` (tactical) + bounded `WorldTurnSystem` (initiative at pump points);
deferred timing is the explicit `Timing` field routed by `ShouldScheduleByTiming`, never inferred
from subsystem.

**Knowledge/legibility spine** — `GameView` + subviews (`CasterView`, `MagicContextView`,
`OperationCardView`, `PendingCastView`, `ResolverLensView`, `LoreCardView`, `DebugStateView`); CLI
JSON and Godot consume these, never rule internals.

**Provider calls** — 7 purposes (`LlmPurpose`): `Wild`, `Router`, `Dialogue`, `DialogueRouter`,
`DialogueParser`, `DialogueParserRouter`, `Background`. Per-purpose routing configured in
`Program.BuildLlmConfiguration`; telemetry via `Sorcerer.Core/Telemetry/ProviderCallStats.cs`.

## Eight golden-proof coverage map (honest)

Per the plan, produce golden transcripts "where the current engine supports them; mark genuinely
missing links rather than faking success." Existing coverage cited; missing links map cleanly onto
later phases (they are not regressions).

| # | Proof | Status | Evidence / missing link |
|---|---|---|---|
| 1 | Quiet escape | **Partial** | Witness→attribution tiers exist: `DeedAndFactionTests.{WitnesslessDeedIsSecretAndUnattributed, EffectWitnessedDeedIsNoticedButNotPinnedOnTheActor, ActorWitnessedDeedIsAttributedToTheActorSoul}`. Missing: one unified concealment/cover/body-swap policy across all four variants → **Phase 1.1**. |
| 2 | Deed that walks | **Supported (mechanical)** | `ForwardMomentumSystemsTests.{RumorsCrossRegionsOnlyAlongNamedRoads, RumorsDoNotTeleportBetweenRegionsWithoutADirectRoad, InterestedNpcInitiativeMovesOneStepAndNamesItsCause}`. Full witnessed-spell→rumor→carrier→NPC-decision single golden episode not yet scripted. |
| 3 | Gift becomes a place | **Supported (golden episode)** | `EpisodeRunnerTests.SocialQuickstartEpisodeExercisesSocialFlywheel` (gift → promise ≥1 → journal/rumors, replayed with `assertFinal`) + `PromiseRealizationTests.Travel*Promise*` delivery. |
| 4 | Borrowed face | **Partial** | `BodySoulIdentityTests.{PossessionMovesControlAndSoulButLeavesVacatedBodyReal, SaveLoadPreservesPossessedState}` (body swap + soul coherence). Missing: institutional identity attribution → **Phase 4**. |
| 5 | Price that comes back | **Supported (core)** | `PromiseRealizationTests.WaitRealizesDebtPromiseAsCollectorThreat` (debt → returning claimant). Cost-ladder breadth (curse/vulnerability variants) → **Phase 5.5**. |
| 6 | Wanting stranger | **Supported** | `ForwardMomentumSystemsTests.InterestedNpcInitiativeMovesOneStepAndNamesItsCause`, `GameSessionCharacterizationTests.OpeningNpcsHaveAuthoredWantsForDialoguePressure`, social episode. |
| 7 | Three roads to emperor | **Partial** | `WinConditionTests.{EmperorExistsAsNormalActorInReachableCapital, KillingEmperorThroughOrdinaryDamageWinsRun}`, `GameSessionCharacterizationTests.EmperorReachabilityUsesImperialDefensePool` (ordinary actor + one force path). Missing: three distinct force/identity/alliance routes → **Phase 1.2**. |
| 8 | Good ending to failure | **Supported** | `WinConditionTests.{KillingControlledBodyCompletesRunAsDefeat, RunChronicleFallbackTextIsGrammaticalWithNoLegendOrDeeds, RunChronicleCanPersistAsInertMemorialInLaterWorld}`. |

Existing compound-rollback / visibility / save-replay characterization already covers much of the
0.1 ask (e.g. `GameSessionCharacterizationTests.{WorldReactionRollsBackWholeDeedWhenAChildConsequenceRejects,
OpenDoorRollsBackWhenCaptiveReleaseChildFails, FactionRecoveryRollsBackResourcesWhenWorldTurnAuditRejects,
ScheduledSilentNonApplyConsequenceRollsBackAndExpiresEvent}`; `GameSessionTests` guard/rollback
suite; `EpisodeRunnerTests` deterministic replay with `assertFinal`; `PersistenceTests` save/load
including possessed state).

## Exit assessment (0.1 → ready for 0.2)

- Behavior is pinned: 927 green tests including an exhaustive per-type dispatch/alias/reject
  contract, compound-rollback, visibility, and deterministic replay coverage.
- The consequence-family boundaries, durable lanes, command surface, views, and provider purposes
  are inventoried and the four parallel type-lists are now drift-guarded.
- **Proceed to Phase 0.2**: decompose `WorldConsequenceApplier` behind the stable public apply
  facade, migrating one cohesive family at a time under `ConsequenceCatalogTests` + the existing
  rollback/visibility/replay suites, deleting migrated methods each step.
