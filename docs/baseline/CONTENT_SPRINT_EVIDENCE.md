# Content Sprint — Evidence (before → after)

Records the movement from the WP0 baseline to the state after WP1–WP10. Regenerate the after
report with:

```
dotnet run --project src/Sorcerer.Cli -- --content-report --content-report-seeds 20 --seed 1 \
  --content-report-out docs/baseline/content_report_after.json
```

## Seeded transect report (20 seeds, 100 zones)

| Metric | Before (WP0) | After (WP10) |
|---|---:|---:|
| Distinct non-commodity items (all seeds) | 67 | 81 |
| **Mean distinctive items per seed** | **4.8** | **16.0** |
| Distinct props | 134 | 212 |
| Distinct actionable documents | 49 | 66 |
| Distinct residents | 137 | 138 |
| Distinct creatures (pets) | 0 | 9 |
| Distinct services | 2 | 6 |
| Empty / dense zones (of 100) | 60 / 35 | 54 / 40 |

The headline diversity gate (≥12 distinct non-commodity items before the first Hollowmere
settlement) moved from failing (4.8) to passing (16.0), driven by the authored item corpus and
region-weighted loot plus culturally-stocked markets.

Authored-content counts grew: items 6 → 72; encounter ingredients 4 → 10; charter forms 7 → 12;
Hollowmere population roles 4 → 9; a new actor-archetype catalog (15 hostiles + 10 pets) and 10
curse/debt/altered-item cost profiles.

## Live-Gemini evidence

- `before_live_gemini_transcript.jsonl.gz` — WP0 first-hour route (gemini-3.5-flash, medium).
- `after_live_gemini_transcript.jsonl.gz` — post-sprint first-hour route, same provider/effort.

The after-run demonstrated the WP6 multi-participant conversation firing in real generated
content: a `gather` at the Hollowmere margin produced an honest stability-vs-freedom disagreement
between three generated residents —

> Lio of Hollowmere: "Keep your head down and the roads stay open. The last time someone sheltered
> a stranger, the reaping took two houses."
> Wynn Silt-Hand, reed apothecary: "Open roads for whom? For their carts and their warrants. We fed
> strangers before there was an Empire to forbid it."

alongside the Free Folk rescue→warning handoff, live rumor propagation, and legend accrual.

## Suite / integration

- `dotnet test Sorcerer.sln` — 1088 pass, 0 fail (was 1054 at baseline; +34 sprint tests).
- Region content ships from `content/region-packs/` (loose) with embedded-resource parity; items,
  actors, and cost profiles load the same way. Flat region files and the region-id switches in
  TextureGrammar/ThreatArchetypeGenerator/PromiseRealizationSystem.Helpers were deleted in WP1.

## Notes / remaining follow-ons (recorded honestly)

- Ambient encounters remain intentionally rare, so the settlement-heavy report route shows 0
  encounter casts; the encounter grammar (10 ingredients, archetype-referenced legible casts) is
  unit-tested and fires in open country.
- Not fully built this sprint: the dedicated multi-zone **waystation set-piece scenario** (the
  encounter grammar is its substrate); in-world **acquisition seeding** for the 5 new charter forms
  and live **resolver/journal wiring** of the cost profiles; the single-call **live-provider
  multi-utterance** group exchange (the deterministic path ships and is tested); and the Godot
  **rebindable-hotkey / controls-screen** presentation over the structured journal.
