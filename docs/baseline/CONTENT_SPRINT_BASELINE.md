# Content Sprint — WP0 Baseline (before-evidence)

Pinned 2026-07-17, before any WP1+ content change. This is the committed "before" that later
lushness claims compare against (docs/CONTENT_SPRINT_PLAN.md, WP0). Regenerate the report with:

```
dotnet run --project src/Sorcerer.Cli -- --content-report --content-report-seeds 20 --seed 1 \
  --content-report-out docs/baseline/content_report_baseline.json
```

## Suite and build

- `dotnet build Sorcerer.sln` — succeeded, 0 warnings.
- `dotnet test Sorcerer.sln` — **1054 passed, 0 failed, 0 skipped**.

## Authored content counts (baseline)

| Surface | Count |
|---|---:|
| Regions / traditions | 14 / 13 |
| Item definitions (code minimal catalog) | 6 |
| Prop bases / ensembles | 61 / 16 (world-wide) |
| Population archetypes | 44 (world-wide) |
| Encounter ingredients | 4 |
| Charter forms | 7 |
| Lore cards | 25 |

## Seeded transect report (20 seeds, seed 1..20, 100 zones)

Route per seed: Marble Containment Yard (0,0) → the three Hollowmere-margin zones → a walk toward
Brall's seed-placed anchor. Raw per-seed exact names are in `content_report_baseline.json`.

Distinct across all sampled seeds:

- distinctive (non-commodity) items **67**, commodity items 4
- props **134**, documents **49**
- residents **137**, creatures **0**, encounter casts **0**
- services **2**

Freshness / feel:

- mean distinctive items per seed: **4.8** (sprint gate wants ≥12 distinct before the first
  Hollowmere settlement)
- pairwise item-set overlap: mean 0.02, max 0.71
- rhythm: **60 empty / 35 dense** of 100 zones
- mean resolver-context entities per zone: 9.4
- unresolved references: 0

### Baseline diagnosis (matches the plan thesis)

- **Items are the thinnest surface a player actually meets**: only ~4.8 distinctive items per
  run despite 67 distinct across all seeds. Item variety is there in the catalog tail but does not
  reach a single playthrough.
- **No creatures and no pets** anywhere on the transect (Hollowmere's exotic-pet identity is not
  yet expressed as content).
- **No encounter casts** fired on the sampled route — encounter grammar is proven but rare.
- **Only 2 distinct services** across 20 seeds.
- Props read as lush per-zone (mean 9.4 context entities) but the memorable ones are the authored
  district *features* that repeat every seed (cell door, ferry bell, tale chair); generated prop
  bases carry the variety.

These are the numbers WP2 (items), WP3 (props/scenes/documents), WP4 (actors/pets), and WP5
(encounters) must visibly move.

## Live-Gemini before-evidence

`before_live_gemini_transcript.jsonl` — a scripted first-hour route (`scripts/baseline_first_hour.txt`)
run with `--provider gemini --effort medium` (gemini-3.5-flash), seed 7, social quickstart. It
exercised a live wild cast, the Free Folk rescue objective handoff (Lio of Hollowmere → warn Nim
Red Thread at Ferryman's Knot before the sweep), live rumor propagation, and a combat telegraph.
This is the qualitative "before" for the wonder/tactics/pace/legibility scoring in WP10.
