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

The current harness runs 25 prompts from fresh imperial encounters and checks required
operation families such as `addStatus`, `transformEntity`, `summon`, `createTiles`,
`scheduleEvent`, `changeFaction`, `areaDamage`, and intentional rejection.

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

## Agent Playtesting

The JSON CLI should support unattended episodes.

The harness should:

- start many runs
- run scripted or agent-chosen commands
- record every command
- record `ActionResult`
- record `AgentObservation`
- check invariants after each step
- write JSONL logs
- write a summary report

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
