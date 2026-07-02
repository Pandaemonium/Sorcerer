# Testing And Evals

Sorcerer should be built with tests and evals because the riskiest parts of the game are
behavioral, not only technical.

The biggest failure modes are:

- bad LLM outputs
- boring magic
- GUI/CLI desync
- hidden state mutation
- provider latency turning play into waiting
- protected items being consumed unexpectedly
- promises or consequences becoming invisible

## Unit Tests

Core tests should cover:

- movement and collision
- turn consumption
- state validation
- transaction rollback
- target/reference binding
- protected inventory
- item cost validation
- promise binding
- body swap control pointer
- renderer-independent views
- command parsing
- action result shape

## Integration Tests

Integration tests should run through `GameSession`.

Important flows:

- move, wait, inspect
- combat
- cast mock spell
- malformed spell response
- rejected spell
- protected item cost
- promise creation
- CLI JSON command
- GUI command path, once available

## Spell Evals

The resolver needs a growing spell corpus.

Current command:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --eval
```

The current harness runs 26 prompts from fresh imperial encounters and checks required
operation families such as `addStatus`, `transformEntity`, `summon`, `createTiles`,
`scheduleEvent`, `changeFaction`, `areaDamage`, `possess` rejection, and intentional
rejection.

Each eval case should include:

- spell text
- scenario
- expected intent tags
- required or forbidden effect families
- whether rejection is acceptable
- notes on what would be interesting

Eval metrics should include:

- valid JSON rate
- validation pass rate
- technical failure rate
- rejection rate
- unsupported effect rate
- target hallucination rate
- latency
- selected capability cards
- cost types
- severity
- rough interestingness notes

The current mock eval is deterministic and contract-focused. Live-provider evals should be
expected to reveal normalization and prompt issues; when the output is semantically useful
but shaped wrong, prefer improving parser normalization over adding prompt-specific
fallbacks.

Interestingness cannot be fully automated, but the harness can surface candidates for
human review.

## Feel Evals: Memorable More Often Than Merely Valid

Status: proposed 2026-07.

The current harness measures correctness; the top failure mode on the vision's list is *boring
magic*, and nothing measures that yet. The feel-eval program closes the gap:

- **An LLM-judge pass over the audit log.** A background/offline job scores each live resolution
  against a fixed rubric:
  - **Specificity:** did the resolution use local anchors from context (named entities,
    materials, regional texture) or could it have happened anywhere?
  - **Consequence:** did it write anything beyond the immediate effect - a trait, promise, deed,
    memory, or durable status - or was it a bare damage/status delta?
  - **Surprise:** did the outcome interpret the spell text, or merely transcribe it?
  - **Tone fit:** does the prose match the region voice and the wonder-with-teeth register
    (flag generic-grimdark and generic-fantasy phrasing for deletion on sight)?
- **Boring-tail mining.** Rank audited casts by rubric score and human-review the worst decile.
  Every recurring boredom pattern becomes either an operation-card improvement, a prompt change,
  or a new eval corpus case - the same loop that already turns parse failures into repairs.
- **Judge discipline.** The judge is an eval tool, never a runtime gate: it runs offline or as a
  throttled background job, its scores land in eval reports rather than game state, and its
  rubric lives in content so it can be tuned without code changes.
- **Latency as a feel metric.** Track command-to-visible-text time per purpose (cast, dialogue)
  in the same reports; a memorable spell that took forty seconds still fails the feel bar.

The judge should also periodically score the *dialogue* audit log with the analogous rubric
(directness, voice, claim quality), per [DIALOGUE_SYSTEM.md](DIALOGUE_SYSTEM.md).

Related, and prerequisite to good tone scores: the region voice + tradition idiom injection that
[AESTHETICS_AND_TONE.md](AESTHETICS_AND_TONE.md) calls the highest-leverage tone change still
needs its C# implementation in the resolver prompt path (the doc's reference to
`wildmagic/prompts.py` is the old prototype). The feel judge is how we will know it landed.

## Agent Playtesting

The JSON CLI supports an initial unattended episode runner:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --episode --episodes 5 --max-turns 40 --episode-log logs\episode_smoke.jsonl
```

Use `--json` for a machine-readable summary. The JSONL log is diagnostic-first and
comprehensive enough to be lightweight replay material where practical. It contains:

- `episode_start` with seed, limits, and the initial debug observation
- `episode_step` with command text, action result summary, magic effect families, player
  HP, entity/promise counts, pending-cast state, invariant issues, and the full debug
  observation after the command
- `episode_final` with the episode summary and final debug observation

The harness should:

- start many runs
- run scripted or agent-chosen commands
- record every command
- record `ActionResult`
- record `AgentObservation`
- check invariants after each step
- write JSONL logs
- write a summary report

Current runner behavior is deterministic and heuristic-driven. It equips/focuses useful
items, picks up nearby items, reads nearby notices, opens the cell when it has the key,
casts through the selected provider, moves toward encounter objectives, and checks
invariants after each command.

Normal scripted runs can also write diagnostic transcripts:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --script content\scripts\background_smoke.txt --transcript logs\cli_transcript_smoke.jsonl
```

These transcripts are command-oriented rather than autonomous-agent episodes. They preserve
the command text, action result, debug observation, and materialized spell JSON where available.
They can be replayed without a model call:

```powershell
dotnet run --project src/Sorcerer.Cli -- --replay logs\cli_transcript_smoke.jsonl --replay-assert-final --json
```

Agents may access perfect debug state. Their job is to find bugs and boring outcomes, not
to imitate a human player's limited perception.

## Invariants

Check:

- no crashes
- state validation passes
- turn counter advances correctly
- technical LLM failure does not consume a turn
- intentional rejection consumes a turn
- protected items are not silently consumed
- actors do not overlap illegally
- entities do not stand in walls unless explicitly allowed
- action results match state changes
- save -> load -> save remains byte-stable for deterministic timestamps
- completed runs write a chronicle
- GUI and CLI reach the same player-facing capability

## Audit Review

Audit logs should be easy to inspect.

Developer tools should eventually support:

- latest calls
- filter by purpose
- filter by failure
- raw response
- normalized response
- validation errors
- selected capability cards
- prompt/context slices
- replay or reproduction command, when available
