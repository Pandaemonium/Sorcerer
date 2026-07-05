# Open Questions

These questions are still genuinely open. Many earlier ones have since been settled - the
Godot project shape, the first scenario, and the world/content canon are now answered (see
[DESIGN_DECISIONS.md](DESIGN_DECISIONS.md), [WORLDBUILDING.md](WORLDBUILDING.md), and the built
first-playable slice) and have been removed from this list.

## Magic Resolver

- How should "interesting magic" be scored in evals, beyond human review?
- Should capability-card routing stay keyword-based, or add embeddings as the operation set
  grows?

## Casting Minigames

The application model is settled: performance is engine-applied after the provider returns,
never a resolver input (see [CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md) and
[DESIGN_DECISIONS.md](DESIGN_DECISIONS.md)).

- Which games belong in the repertoire beyond rune tracing: rhythm, path drawing, symbol
  matching, something else?
- Is the initial minimum scoring window (`RuneTraceScoring.MinimumScoringWindowSeconds`,
  2.75s) right once real provider latencies are measured?
- Do the initial calibration constants in `RuneTraceScoring` actually make average play land
  at neutral for real players (playing must stay EV-neutral against skipping)? Needs
  playtest data from the audit log's performance annotations.
- What belongs in the engine-side mishap/complication tables, and how does mishap severity
  scale with reported spell severity?
- If the provider call fails and the cast is retried, does a played score carry over?
- How should accessibility options present neutral casting?

## Promises

- How much should the player journal reveal versus keep mysterious as realization grows richer?
- Which realization archetypes are worth building next, beyond the current read/open/talk
  anchors?

## Persistence

The high-level persistence direction is now settled in BUILD_PLAN.md: full mid-run
quicksave/resume, `System.Text.Json` with a versioned envelope and migration hook, and replays
under `runs/<id>/`.

- What migration tests and compatibility guarantees should we keep once real saves exist in the
  wild?
- How much of the audit/log bundle should ship with ordinary player saves versus developer builds?

## Collaboration

- What branch and PR workflow should the new repo use?
- Which docs must be updated in every architecture-changing PR?
- How should agent-authored playtest reports be stored?
