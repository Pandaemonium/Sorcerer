# Content Sprint — Evidence (before → after)

Records the movement from the WP0 baseline through WP1–WP10 and the subsequent holistic CLI
review. Regenerate the committed report with:

```
dotnet run --project src/Sorcerer.Cli -- --content-report --content-report-seeds 20 --seed 1 \
  --content-report-out docs/baseline/content_report_after.json
```

## Seeded transect report

| Metric | Before (WP0, 20 seeds) | Claude WP10 (20 seeds) | Holistic review (20 seeds) |
|---|---:|---:|---:|
| Distinct non-commodity items (all seeds) | 67 | 81 | 90 |
| **Mean distinctive items per seed** | **4.8** | **16.0** | **17.4** |
| Mean pairwise item-set overlap | not recorded | not recorded | 0.28 |
| Maximum pairwise item-set overlap | not recorded | not recorded | 0.65 |
| Distinct props | 134 | 212 | 249 |
| Distinct actionable documents | 49 | 66 | 71 |
| Distinct residents | 137 | 138 | 140 |
| Distinct creatures (pets) | 0 | 9 | 10 |
| Distinct encounter casts | not recorded | not recorded | 0 |
| Distinct services | 2 | 6 | 8 |
| Empty / dense zones (of 100) | 60 / 35 | 54 / 40 | 54 / 40 |
| Unresolved references | not recorded | 0 | 0 |

The headline diversity gate (≥12 distinct non-commodity items before the first Hollowmere
settlement) passes at 17.4. No distinctive item appears in every seed; the most common item in the
committed sample appears in 18 of 20 seeds.

The review also ran a 200-seed / 1,000-zone stress sample. It found 604 distinctive item names,
512 props, 340 actionable documents, 1,106 residents, 13 creatures, 15 encounter casts, 9 services,
17.1 mean distinctive items per seed, 0.27 mean / 0.68 maximum pairwise item overlap, 515 empty /
395 dense zones, 9.9 mean resolver-context entities per zone, and zero unresolved references. Its
most common item, the bone luck-token, appeared in 161 of 200 seeds; that repetition remains useful
tuning evidence.

Authored-content counts grew: items 6 → 72; encounter ingredients 4 → 10; charter forms 7 → 12;
priority-region population roles now meet the plan floors (opening 5, Hollowmere 10, Brall 8), as
do prop bases / ensembles (opening 10 / 5, Hollowmere 14 / 8, Brall 12 / 6). The actor-archetype
catalog contains 13 hostile and 10 pet archetypes, and the cost-profile catalog contains 10
curse/debt/altered-item profiles.

## Live-Gemini evidence

- `before_live_gemini_transcript.jsonl.gz` — WP0 first-hour route (gemini-3.5-flash, medium).
- `after_live_gemini_transcript.jsonl.gz` — post-sprint first-hour route, same provider/effort.
- `review_live_gemini_transcript.jsonl.gz` — integration-review route (seed 37, same provider/effort).

The holistic review additionally recorded nineteen personal live-Gemini CLI transcripts under
`logs/holistic_playtest/`. They cover quiet and loud containment escapes, body swap, async casting
and save/load, charter/trade paths, Hollowmere pets and politics, open-country encounters,
nonviolent play, Brall ensembles, found documents, debt costs, and targeted regression reruns.
The final route (`15_final_settlement.jsonl`) used `gemini-3.5-flash` at medium effort and replays
with `--replay-assert-final`, zero validation issues, and no provider calls.

The after-run demonstrated the WP6 multi-participant conversation firing in real generated
content: a `gather` at the Hollowmere margin produced an honest stability-vs-freedom disagreement
between three generated residents —

> Lio of Hollowmere: "Keep your head down and the roads stay open. The last time someone sheltered
> a stranger, the reaping took two houses."
> Wynn Silt-Hand, reed apothecary: "Open roads for whom? For their carts and their warrants. We fed
> strangers before there was an Empire to forbid it."

alongside the Free Folk rescue→warning handoff, live rumor propagation, and legend accrual.

The integration-review run independently exercised a successful live-Gemini spell, containment escape,
Hollowmere travel, the rescue→warning handoff, a three-participant `gather`, and the structured
journal without technical provider failures. It also caught and fixed the group action's incorrect
turn-before timestamp.

The follow-up pass recorded `logs/followup_playtest/06_live_gemini_followups.jsonl` and
`07_live_gemini_group_sentence.jsonl`. In the first, Gemini turned the sorcerer's shadow into a
licensed courier, wrote false-paper memories and mimic compulsions onto both guards, charged mana
plus an Imperial Ledger Entry, and the runtime cost system later delivered a mire-throated
claimant. The same run exercised a one-call four-speaker political exchange. The second verified
complete, compact utterances: a relay clerk defended orderly queues, a Free Folk shelterwright
answered with neighbors and drainage routes, and an imperial captain contradicted her with a
concrete threat. Both new `inventory` and `threats` readouts worked turn-free in that live session.

The final debt route demonstrated a complete nonviolent systemic handoff: Gemini transformed the
guards' weapons into singing iron wrens and selected an authored imperial-ledger debt; travel
materialized an eelglass-eyed claimant; live dialogue named three concrete settlement options;
and `settle claimant` ended immediate hostility while preserving the exact 300-crown, surveyor's
lens, or land-rights terms as a claimant-bound journal agreement and durable canon. The engine does
not pretend that a generated payment was already made.

## Integration-review corrections

- Content-pack discovery now supports nested directories and manifest ids that differ from their
  folder names. Items, actor archetypes, and cost profiles reject duplicate authored ids instead of
  silently overwriting content.
- Group conversation classification no longer turns generic imperial witnesses or halls into
  Bralli story circles. Hollowmere disagreement requires stability and freedom voices; Brall keeps
  three distinct story voices; participant replies acknowledge the player's opener; and authored
  ensemble roles are guaranteed to appear before decorative population fills the zone.
- All five new charter forms have data-authored, readable acquisition hooks in the transect.
- Resolver prompts expose only mechanically supported debt-profile ids. Unknown, curse, and
  altered-item profiles are rejected until those families have authoritative runtime semantics;
  selected debts become player-visible journal threads with counterplay and can now be negotiated.
- The Godot journal now renders the typed sections rather than the legacy flat list and has a
  rebindable F2 action visible in the controls screen.
- Region markets use deterministic chance-weighted assortments, every merchant role has stock,
  every service role has an offer, diagonal interaction reach is consistent, and Brall merchants
  no longer share three mandatory items in every seed.
- Priority-region population, prop, and ensemble floors are enforced by tests.
- Settlement anchors, encounters, residents, pets, props, and services are placed near zone arrival
  points before outward scatter, so generated richness is visible without combing an empty map.
- Directional geography no longer calls the first western leg a capital approach. Explicit remote
  first names no longer silently retarget a nearer byname match, personal claimant settlements
  suppress threat telegraphs in the correct actor→player direction, and empty unknown factions no
  longer clutter `standing`.
- Door and body-swap narration now use player-correct grammar, realized promises are not mislabeled
  as completed objectives, and deed rumors carry the live spell's specific witnessed description
  instead of a generic auto-rumor.

## Suite / integration

- `dotnet test Sorcerer.sln --no-restore` — 1169 pass, 0 fail (was 1054 at baseline; +115 sprint/review/follow-up tests).
- `dotnet build src/Sorcerer.Godot/Sorcerer.Godot.csproj --no-restore` — succeeds with 0 warnings.
- Final live transcript replay — exact final assertion passes; 3 promises, 2 canon records, 0
  validation issues.
- Region content ships from `content/region-packs/` (loose) with embedded-resource parity; items,
  actors, and cost profiles load the same way. Flat region files and the region-id switches in
  TextureGrammar/ThreatArchetypeGenerator/PromiseRealizationSystem.Helpers were deleted in WP1.

## Follow-up systems completed

The holistic follow-ons are now implemented through shared state rather than route-specific flags:

- `journey` compresses overland legs but advances every world turn, carries nearby pursuers, stops
  immediately for pursuit/authored payoff/destination, and spends at most two ambient scene pauses;
- typed bargain terms cover currency, items, services, standing, concessions, and deadlines;
  conversation prose alone no longer clears a claim, and service agreements persist until performed;
- ordinary encounters expose `offer`, `bargain`, `concede`, `intimidate`, and atomic `exchange`;
- live group dialogue requests exactly one multi-utterance provider response, authorizes every
  speaker/target before applying typed proposals, and retains the deterministic offline path;
- curse and altered-item profiles create persistent runtime state, equipment/merchant consequences,
  journal surfaces, and explicit cleansing counterplay;
- actor-archetype intent now produces a one-turn visible commitment window, item-based counters,
  and equipment-dependent bracing;
- the relay waystation is a data-authored staffed threshold/interior with tested force, stealth,
  clerk-alliance, courier-exchange, forgery, body-swap, and magic entry routes.
- restricted interiors preserve credential/body/agreement authorization after transition while
  forced and overt-magical entry correctly leave interior guards hostile;
- `inventory`/`items` and `threats`/`intents` are first-class text and JSON commands, remain
  available during pending casts, and render the same authoritative cards used by the GUI;
- live group utterances are requested as complete sentences and repaired at sentence boundaries
  rather than being hard-cut mid-thought.
- threat cards now read active coward/dance/dread/mimic behavior from the same state as AI, so a
successfully compelled guard is not falsely advertised as about to make its ordinary attack.
- the opening containment soldier and ward-captain now carry distinct authored archetypes and
  commit a visible intent before attacking, so the first encounter teaches the tactical grammar.

The final personal verification routes are under `logs/followup_playtest/`:

- `14_live_gemini_altered_item.jsonl` made an equipped charcoal wand permanently wild-stained,
  exposed the alteration and focus bias in `inventory`, and retained it as authoritative item state;
- `16_live_gemini_group_bargain_retest.jsonl` produced one successful three-speaker Gemini response
  with authorized typed memory consequences;
- `22_live_gemini_typed_bargain_repaired.jsonl` grounded Gemini's explicit red-tincture demand into
  an auditable typed offer, then transferred the real item, cleared only the satisfied claim, and
  changed the soldier's posture;
- `24_transect_brall_settlement_retest.jsonl` verified bounded journey pacing through the landmark
  and into Deep-Tale Hold rather than stopping in empty country;
- `25_live_gemini_brall_tale_hall.jsonl` repeated that route live and resolved one three-voice
  tale-hall exchange in one Gemini call with four typed memory deltas and no technical failure.

Focused acceptance tests cover all seven waystation routes, journey pursuit/scene bounds, one-call
group dialogue, tactical commitment/countering, and worn-equipment bracing. The remaining GUI note
is human visual QA: the shared context actions, typed journal, rebindable F1/F2 controls, and command
line compile through Godot, but headless automation cannot judge font/layout feel.
