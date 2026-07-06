# FEEL_NOTES

Playtest feel observations — things that felt *off* while playing, distinct from outright bugs
(bugs are fixed in code and noted in commits). Gathered from live playtests driven through the
Ollama resolver (`qwen3.5:9b`) via `--live-playtest`, ~20 wild spells and ~12 dialogue rounds
across ≥5 NPCs per run.

Each note: **[area]** what felt off — why it matters — possible direction. Severity: `minor` /
`medium` / `major`.

> Status: in progress. This file is filled as playtest transcripts are reviewed.

## Magic / costs

- **`nearest_enemy` binds a friendly NPC** `medium` — casting "bind the nearest enemy in
  webbing" with no hostile in range produced *"You bind Lio of Hollowmere"* (the friendly
  prisoner). A `nearest_enemy` selector should never resolve to a non-hostile; when there is no
  enemy it should fizzle, not grab an ally. (ep1 s2) — *investigating as a real targeting bug.*
- **Self-cast area effects tangle the caster** `minor` — "grow thorned vines around me" produced
  *"vines erupt… tangling with you"* and the next move failed with *"You struggle against binding
  magic."* Realistic, but a player buffing/zoning around themselves and then being unable to move
  feels like a trap rather than a tool. Consider excluding the caster from self-anchored hazards,
  or warning. (ep1 s18→s21)
- **`restoreMana` bundled into unrelated spells** `medium` — the resolver keeps appending a
  `restoreMana` effect to spells that have nothing to do with mana: "raise a wall of ice" and
  "strike with blue fire" both came back as `[createTiles/damage, restoreMana]` while also costing
  mana. So a spell spends mana as its cost and hands some back as an effect — muddled and slightly
  exploit-adjacent. Likely the resolver over-associating the mana-cost prompt with mana effects; a
  prompt nudge ("costs are not effects; do not add restoreMana unless the spell is about restoring
  magic") would help. (GUI autoplay, 2/2 casts)
- **Resolver invents a terrain target id** `minor` — a cast failed with *"Nothing you can see
  answers to 'shallow_water_20-17'."* The model referenced a tile id as a target; the engine
  correctly rejects it (turn not consumed) but the cast is wasted. Mostly model quality; a gentle
  prompt nudge away from coordinate/tile ids as targets could help. (ep1 s26)

## Dialogue / NPCs

- Dialogue is coherent and lore-grounded so far (frontier voice, reed-memory, Stalnaz reeds).
  NPCs reached via travel answer in-character. No dialogue bug yet.

## World / pacing

- **"Trade places" when moving into a friendly** `minor` — a plain move onto Lio's tile printed
  *"You trade places with Lio of Hollowmere."* Position-swap-with-ally may be intended, but it is
  surprising as the result of a basic move and could strand an NPC. Worth confirming it is
  deliberate. (ep1 s17)

## Bugs found and fixed during playtesting

- **Harness navigation spin (fixed)** — the live playtest driver spun hundreds of no-op travels
  once it needed new NPCs, because spawned residents are not perceived until looked at. Fixed with
  an Inspect-after-travel step + a no-progress breaker (same fix applied to GUI autoplay).
- **`restoreMana` bundled into unrelated spells (prompt fix, to validate)** — added a resolver
  prompt rule: "Costs are not effects: never add a restoreMana/heal effect unless that is the
  spell's actual purpose." Applies on next launch; will confirm it stops the fire-strike-hands-mana
  behavior.
- **Unpayable item cost fizzles the spell (fixed earlier)** — resolver-named items the caster does
  not carry now substitute mana instead of failing the cast (SpellCostRepairTests).
