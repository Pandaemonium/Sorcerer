# Run Arc — the four movements

Status: created 2026-07-13 at the start of IMPLEMENTATION_PLAN Phase 2 (make a complete run before
making more content). This doc owns **pacing and tuning** for the reference transect. The four
movements are **derived labels for debug/telemetry and player-facing chronicle structure — they do
NOT gate commands or own a chapter state machine** (plan §2.1). Keep thresholds here, not in code
names.

## The four movements

The product reference transect is **Marble Containment Yard (`imperial_encounter`) → Hollowmere
Margin (`hollowmere_margin`) → Brall → Vigovian Capital (`vigovian_capital`)**. Brall is the first
full-density major old kingdom and carries the War movement's cultural/campaign proof. The current
implemented world spine still travels directly from Hollowmere to the capital; Phase 6 must insert
a connected Bralli route and data without hard-coding a chapter gate. The current movement is
derived read-only in `EngineViewBuilder.BuildRunArc` and surfaced as `GameView.World.RunArc`:

| Movement | Derived when | Meaning |
|---|---|---|
| **escape** | still in the starting region (`imperial_encounter`) | breaking out of imperial custody |
| **foothold** | left the start region, imperial defenses still at full | building strength on the frontier |
| **war** | left the start region **and** imperial defenses spent below max; Brall should normally carry much of this movement once connected | actively bleeding the empire through force, liberation, deception, tale/coalition, or promise |
| **reach** | in the capital region (`vigovian_capital`) | at the marble heart, the throne within reach |

Derivation is furthest-progressed-condition-wins, so `reach` overrides `war` overrides `foothold`
overrides `escape`. It reads only authoritative facts (current region + `empire_bloc` `defenses` vs
`max_defenses`); there is no `RunArcLedger` and no hidden progress meter.

## Tuning notes (this is the home for pacing)

- **v1 signals are region + imperial defenses.** As the capability portfolio (§2.3) and route/pressure
  state land, fold them into the derivation here (escaped-custody flag, known settlements, opened
  routes, acquired repertoire/material/social capabilities) rather than adding code paths.
- The `war` bar is currently "any imperial-defense spend below max." If the empire's own escalation
  spending makes that trigger too early, raise the bar (e.g. defenses ≤ 1, or require a
  player-attributed depletion) here.
- Target: the classic reference run wins in **6–8 hours** (plan §2, roadmap Milestone 1). Instrument
  elapsed turns/real time per movement to expose pacing gaps; keep the numbers in this doc.
- The target route must enter Brall as ordinary geography while preserving mixed and surprising
  capital approaches. Brall is a dense opportunity, not a mandatory quest-state check.
