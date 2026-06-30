# CLI And Agent Playtesting

The Sorcerer CLI is a first-class frontend. It exists so AI agents can fully play and
playtest the same game humans play in the Godot GUI.

The CLI must call the same `GameSession` backend as the GUI. It must not reimplement game
rules.

The CLI is JSON-first. Human-readable terminal play can exist, but machine-readable agent
play is the primary design target.

## Goals

- Let agents play the whole game without visual screen scraping.
- Let developers reproduce bugs with short command scripts.
- Make live-provider playtesting the normal way to judge game feel and resolver quality.
- Support fast mock-provider runs for focused troubleshooting and regression checks.
- Expose structured observations and action results.
- Expose optional perfect debug state for agents.
- Keep text commands convenient for humans where it is cheap.

## Default Agent Guidance

When playtesting Sorcerer, agents should usually use the live Ollama resolver. The point of
playtesting is to see the real spell pipeline under realistic model behavior: latency,
schema dialects, boring resolutions, surprising costs, target mistakes, and all. Mock mode
is valuable, but it does not test the heart of the game.

Use mock mode when:

- isolating a specific engine, CLI, or turn-accounting issue
- reproducing a bug from a known command sequence
- checking deterministic invariant failures quickly
- running the fixed spell eval corpus
- working while Ollama or the selected live model is unavailable

Use live Ollama when:

- evaluating whether magic outcomes feel vivid, specific, and consequential
- testing new operation cards, prompt changes, or schema repairs
- checking whether the GUI/CLI flow survives real provider latency and weird output
- gathering audit logs for resolver improvement
- doing general exploratory playtests

The CLI currently defaults to `mock` for quick local execution, so agents should pass
`--provider ollama` explicitly for ordinary playtesting.

## Quick Start: Live Ollama Playtest

Start Ollama and make sure the target model is available, then run:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b --json --debug-state `
  --transcript logs\cli_ollama_playtest.jsonl `
  --command "inspect" `
  --command "cast bind the nearest soldier in sticky blue glass" `
  --command "cast promise that the notice will remember my name when read" `
  --command "read notice" `
  --command "jobs"
```

Useful live-provider outputs to inspect after a run:

- terminal JSON envelopes for command results and observations
- `logs\cli_ollama_playtest.jsonl` for step-by-step diagnostic transcript
- `logs\wild_magic_audit.jsonl` for prompt, raw model output, repaired resolution, and
  validation errors

Report the provider, model, command sequence, transcript path, and audit symptoms when a
playtest finds a problem.

## Recommended Modes

### JSON Mode

Agent-friendly commands and results are the primary contract:

```json
{"type":"cast","text":"bind the nearest enemy in blue glass"}
```

Agents can also split casting into submit/resolve steps:

```json
{"type":"cast","text":"bind the nearest enemy in blue glass","await":false}
{"type":"await_cast"}
```

While a cast is pending, turn-consuming commands are blocked until the agent sends
`await_cast` or `cancel_cast`. The pending cast appears in `observation.pendingCast`.

Every command should return an action result and a fresh observation:

```json
{
  "result": {
    "success": true,
    "action": "cast",
    "consumedTurn": true,
    "turnBefore": 4,
    "turnAfter": 5,
    "technicalFailure": false,
    "messages": ["Blue glass snaps around the foe."],
    "deltas": [],
    "magic": {
      "provider": "mock",
      "accepted": true,
      "effectTypes": ["addStatus"],
      "error": null
    }
  },
  "observation": {
    "turn": 5,
    "player": {},
    "map": {},
    "visibleEntities": [],
    "inventory": {},
    "promises": []
  }
}
```

The exact schema can evolve, but fields must stay stable once agents depend on them.

### Text Mode

Human-friendly commands:

```text
inspect
map
move east
wait
cast bind the nearest enemy in blue glass
target 12 8
pickup
equip iron wand
focus weapon
journal
quit
```

Scripted shared-engine smoke:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b `
  --command "inspect" `
  --command "move east" `
  --command "pickup red tincture" `
  --command "use red tincture" `
  --command "equip charcoal wand" `
  --command "focus charcoal wand" `
  --command "journal"
```

Scripted diagnostic run with transcript:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --script content\scripts\background_smoke.txt --transcript logs\cli_transcript_smoke.jsonl
```

Blank lines and `#` comments in script files are ignored. Any `--command` entries are
appended after script commands. The example above intentionally uses `mock` because it is
a deterministic troubleshooting smoke; exploratory playtest scripts should usually use
`--provider ollama`.

## Agent Observation

Agents need a compact structured view after each command.

Recommended top-level fields:

```json
{
  "turn": 12,
  "player": {},
  "map": {},
  "nearby": {},
  "visibleEntities": [],
  "inventory": {},
  "equipment": {},
  "reagents": [],
  "messages": [],
  "promises": [],
  "standing": {},
  "availableCommands": [],
  "debug": {}
}
```

Important observation details:

- player position, HP, mana, statuses
- local coordinate map
- adjacent tile affordances
- visible enemies, NPCs, props, and items
- floor items and carried items
- selected target
- active curses
- scheduled events and triggers, if player-visible
- recent log
- provider/debug status
- perfect hidden state when debug mode is enabled

Agents should not need to infer coordinates from rendered glyphs alone.

## Commands Implemented Early

Core shared commands currently route through `GameSession`:

- `inspect`
- `map [radius]`
- `move <direction>`
- direction aliases: `north`, `south`, `east`, `west`, etc.
- `wait`
- `open [target]`
- `pickup [target]`
- `drop <item>`
- `use <item>`
- `equip <item>`
- `unequip <slot-or-item>`
- `focus <item-or-slot>`
- `unfocus`
- `target <x> <y>`
- `untarget`
- `cast <spell text>`
- `begin_cast <spell text>`
- `await_cast`
- `cancel_cast`
- `journal`
- `read [target]`
- `examine [target]`
- `talk [target-or-message]`
- `possess [target]`
- `standing`
- `followers`
- `jobs`
- `help`
- `quit`

Later:

- `wares`

Inventory protection and reagent inspection are also implemented:

- `protect <item>`
- `unprotect <item>`
- `reagents`

## Provider Flags

Recommended CLI flags:

```text
--provider ollama|local|mock|api|openai-compatible
--host <url>
--model <name>
--seed <int>
--command <text>
--script <path>
--json
--debug-state
--eval
--episode
--episodes <n>
--max-turns <n>
--transcript <path>
--episode-log <path>
--record <path>
```

`ollama` and `local` currently select the live Ollama provider. `mock` is deterministic and
fast enough for frequent focused checks, but it should not be treated as the ordinary
playtest resolver.

Live providers exercise the real resolver and write audit logs. They are the right choice
when judging wild magic quality.

Current implemented flags are `--provider`, `--host`, `--model`, `--json`,
`--debug-state`, `--command`, `--script`, `--transcript`, `--eval`, `--episode`,
`--episodes`, `--max-turns`, `--seed`, and `--episode-log`/`--record`.

Unknown provider names fall back to mock today, so strict live evaluation should use an
explicit known provider name and check the result's `magic.provider` field.

## Replay

Replay is useful but should stay lightweight.

Recommended replay record:

```json
{
  "version": 1,
  "seed": 7,
  "scenario": "test_chamber",
  "commands": [],
  "resolvedLlm": [],
  "finalSummary": {}
}
```

For live LLM runs, record the validated resolution JSON at the action where it applied.
Replay should not call the model again.

If replay becomes expensive, prefer preserving:

- command scripts
- seeds
- audit records
- final summaries

over building a complicated rewind system early.

The current lightweight path is diagnostic transcripts: `--transcript` writes JSONL
records for `transcript_start`, each `transcript_step`, and `transcript_final`. Each record
includes command text, action results, and debug observations. These logs are meant for
troubleshooting first and replay-like reproduction second.

## Agent QA Harness

The first spell eval harness is available:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --eval
```

It runs a fixed spell corpus from fresh imperial encounters and checks that the mock
resolver selects the expected operation families. This is not a substitute for free-play
agents or live Ollama testing, but it catches engine and registry drift quickly.

The first unattended episode runner is also available:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --episode --episodes 5 --max-turns 40 --episode-log logs\episode_smoke.jsonl
```

With `mock`, it uses a deterministic heuristic agent over the real `GameSession`, writes
diagnostic JSONL records when `--episode-log` is provided, and fails the process if hard
invariants fail. Logs include `episode_start`, `episode_step`, and `episode_final` records
with full debug observations, so they can support troubleshooting first and lightweight
replay material where practical. This is intentionally simple, but it is already useful
for catching engine regressions.

For a slower but more realistic unattended playtest, use Ollama:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b --episode --episodes 1 --max-turns 20 --episode-log logs\episode_ollama.jsonl --json
```

Eventually, deepen the unattended harness so it:

- starts many episodes
- chooses commands through a mock/random/live-provider agent
- records step JSONL
- records command scripts
- records replay where enabled
- checks invariants after each command
- writes a report

Hard invariant checks should matter more than agent opinions:

- no crashes
- state validation passes
- technical failure does not consume a turn
- rejected spell consumes a turn
- turn counter does not jump unexpectedly
- blocking actors do not overlap or stand in walls
- player-facing action had messages when appropriate

Agent free-form notes can be useful leads, but should not be treated as confirmed bugs.
