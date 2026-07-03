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
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu --json --debug-state `
  --transcript logs\cli_ollama_playtest.jsonl `
  --command "inspect" `
  --command "character" `
  --command "talk Lio" `
  --command "wait" `
  --command "journal" `
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
- `logs\dialogue_audit.jsonl` for generated dialogue requests, raw replies, parsed proposals,
  validation issues, and resulting delta operation names

Report the provider, model, command sequence, transcript path, and audit symptoms when a
playtest finds a problem.

For a focused social-system smoke, use the named quickstart:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --quickstart social `
  --command "talk Lio" `
  --command "give grave salt to Lio" `
  --command "talk Lio, what waits outside?" `
  --command "wait" `
  --command "travel east" `
  --command "inspect" `
  --command "bonds Lio"
```

The `wait` step gives queued dialogue claim extraction a turn boundary to apply. The travel step
should realize the confided site promise as a generated place when the destination zone is new.
Debug observations also expose visible claim cards and claim ids/counts for agent diagnostics.

For a focused travel/world-feel smoke, use:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu `
  --transcript logs\phase5_travel_feel.jsonl `
  --command "travel east" `
  --command "atlas" `
  --command "cast ask the reed shrine to hide me under river-color" `
  --command "inspect"
```

This checks shared travel, region affordances, resolver region context, schema repair, and whether
the generated place gives free-form magic something lush and mechanically real to bite into.

For a focused lore/texture/background smoke, use mock mode so the deterministic content layer is
easy to inspect:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --json --debug-state `
  --transcript logs\phase7_lore_texture_mock.jsonl `
  --command "travel east" `
  --command "atlas" `
  --command "inspect" `
  --command "examine reed" `
  --command "wait" `
  --command "examine reed" `
  --command "cast ask the Hollowmere reeds to hide my outline in water-memory"
```

The atlas should surface local lore, generated fixtures should have concrete region-colored names
and subjects, the second examine should show applied known detail, and the spell context should
include routed lore without requiring the player-visible view to reveal hidden debug state.

For a focused narrator/legend smoke, create reputation, then travel:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --json --debug-state `
  --transcript logs\phase7_narrator_mock.jsonl `
  --command "cast a plain blue fire" `
  --command "travel east" `
  --command "standing"
```

The cast should mint a durable rumor in debug/view state, the travel result may include a
`worldTurn` delta with `kind=rumor_spread` as that record reaches the next region, and
`zone_entry_rumor` may still add legibility text based on legend/faction pressure without creating
new standing by itself. Player-facing messages should show the concrete rumor or lead once; the
`worldTurn` wrapper remains in deltas/debug as audit-only structure. The `journal` command should
show the mixed lead/claim/pressure overview, while the dedicated `rumors` command should show only
player-heard high-salience rumor records, including status and recent retelling history; debug
observations expose full `debug.rumors` cards with carriers, status, hops, and distortion history.
Debug ledger summaries should expose bounded world-turn counts and recent `debug.worldTurns` cards
with kind, source, summary, and details.

For a focused persistence/replay/win smoke, record a short run, replay it, and then exercise the
capital:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --json --debug-state `
  --transcript logs\phase8_save_replay_mock.jsonl `
  --command "examine tincture" `
  --command "save runs\quicksave.json" `
  --command "load runs\quicksave.json" `
  --command "cast freeze the floor under the nearest enemy"

dotnet run --project src/Sorcerer.Cli -- --replay logs\phase8_save_replay_mock.jsonl --replay-assert-final --json

dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu --json --debug-state `
  --disable-background `
  --transcript logs\phase8_capital_ollama_feel.jsonl `
  --command "travel east" `
  --command "travel east" `
  --command "cast make the emperor's marble shadow kneel and crack" `
  --command "standing"
```

The save/load commands should preserve state and pending casts, replay should consume materialized
spell JSON and background text instead of calling the model, and the capital smoke should show
whether the emperor and Empire reaction feel like ordinary systems rather than a scripted finale.
Internally, wild magic resolution is now split from application: a provider result can be
materialized without changing state, then applied later through the same authoritative validation
path.

Background enrichment can be throttled for resource-sensitive runs:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu `
  --disable-background `
  --command "inspect" `
  --command "cast hide me in the marble blind spot"
```

For deterministic background-lane testing, keep mock spell resolution but alter the queue limits:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider mock --json --debug-state `
  --max-background-jobs 2 `
  --background-jobs-per-turn 1 `
  --command "examine brazier" `
  --command "wait" `
  --command "jobs"
```

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

While a cast is pending, turn-consuming and state-changing commands are blocked until the agent
sends `await_cast` or `cancel_cast`. Read-only state and ledger commands remain available:
`inspect`, `map`, `atlas`, `journal`, `rumors`, `character`, `reagents`, `wares`, `services`, `bonds`,
`standing`, `followers`, `jobs`, `save`, `load`, `help`, and `quit`. The pending cast appears in
`observation.pendingCast` with state (`resolving`, `ready`, `failed`, or legacy `waiting`),
provider, accepted/technical-failure flags, effect types, and error metadata when materialized.
`begin_cast` starts the non-mutating provider resolution immediately; `await_cast` applies the
materialized result through the ordinary validation path. `cancel_cast` signals the provider
cancellation token and consumes no turn. Saving while pending waits for resolution and persists
the materialized spell, so a loaded pending cast can be applied without another model call.

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

`ActionResult.Messages` should include both the direct command outcome and diegetic turn-boundary
messages produced by the same consumed turn, such as temporary terrain fading or scheduled magic
coming due. Background jobs expose queue/apply state through `jobs` and debug observations; their
durable content should surface later through ordinary world views such as `examine`, not as raw
queue narration.
Promise payoff action results may include hidden `promiseRealizationPlan` deltas before
`realizePromise` and the concrete payoff consequence. These are agent/debug breadcrumbs with the
normalized handler, score, selection reasons, trigger, target, and `realizedIn`; they should not be
rendered as player-facing prose.

Player-facing observations are perception-bound. `view.tiles` includes every map
coordinate, but unexplored tiles use `terrain: "unknown"` with `visible: false` and
`explored: false`; explored-but-not-currently-visible tiles are marked `explored: true` and
`visible: false`. `view.entities` includes only visible entities plus the controlled body.
When `--debug-state` or transcripts are enabled, debug observations may include perfect
diagnostic state such as all entity ids and ledger counts. Agents should use debug state for
testing and reproduction, not as a model of what the human player knows.
Debug observations also include raw faction standing/resources, hostile roles, raw bond values,
active NPC wants, and ledger counts such as scheduled events and triggers. Those exact resource,
motivation, and relationship counts are intentionally debug-only; player-facing
`standing` reports role, axes, pressure, mood, and rank, `bonds` reports qualitative posture, and
`journal` shows pending patrol/warrant pressure when it is player-legible.

### Text Mode

Human-friendly commands:

```text
inspect
map
map 5
atlas
travel east
move east
wait
cast bind the nearest enemy in blue glass
target 12 8
pickup
equip iron wand
focus weapon
journal
give grave salt to Lio
recruit Lio
bonds
quit
```

`map [radius]` returns a compact coordinate-stamped, perception-bound text map centered on the
controlled body. It is for quick human/agent orientation; JSON agents should still prefer
`view.tiles` for full structured tile state.

Scripted shared-engine smoke:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu `
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

CLI text commands and the Godot command line both parse through `TextCommandParser`; CLI JSON
commands should map the same verbs and common aliases onto the same typed `GameCommand`s.
The Godot Journal scene reads `GameView.Journal`, which is built by the same helper used by the
CLI `journal` command, so agent-visible leads/claims/rumors and the human GUI journal stay in
lockstep.

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
- character sheet fields in `view.character`: body id, soul id, origin, Vigor,
  Attunement, Composure, public name, appearance, magical signature
- debug-only NPC wants in `debug.wants`: entity id/name, want id, text, salience, status, stakes,
  and tags
- promise cards in `view.promises`: id, status, kind, trigger hint, realization kind, bound
  place/target, salience, source claim/speaker/listener/confidence when present, and the last
  eligibility failure/context/turn when a payoff check has recently found the promise waiting
  rather than realizable
- debug rumor cards in `debug.rumors`: id, source, current/origin region, carrier ids, status,
  hops, salience, text, original text, and distortion history
- recent bounded world-turn audit cards in `debug.worldTurns`: id, turn, reason, kind, source,
  summary, and detail fields. Current kinds include `rumor_spread`, `promise_stir`,
  `want_stir`, `faction_pressure`, `faction_pressure_blocked`, and `faction_recovery`;
  `want_stir` is private context and should not appear as player-facing journal truth by itself.
- reagent cards in `view.reagents`: unprotected carried fuel with quantity, value, material, tags,
  and `spellBias`
- local coordinate map
- adjacent tile affordances
- visible enemies, NPCs, props, and items
- world card fields in `view.world`: current zone, region, realm, tradition, imperial
  presence, wildness, and affordances
- floor items and carried items
- selected target
- active curses
- scheduled events and triggers, if player-visible
- recent log
- provider/debug status
- faction pressure, standing, and pending warrant/patrol state
- perfect hidden state when debug mode is enabled

Agents should not need to infer coordinates from rendered glyphs alone.

## Commands Implemented Early

Core shared commands currently route through `GameSession`:

- `inspect`
- `map [radius]`
- `travel <direction>`
- `atlas`
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
- `character`
- `sheet`
- `read [target]`
- `examine [target]`
- `talk [target-or-message]`
- `give <item> to <target>`
- `recruit [target]`
- `bonds [target]`
- `possess [target]`
- `standing`
- `followers`
- `jobs`
- `save [path]`
- `load [path]`
- `help`
- `quit`

Cardinal movement across a tactical map edge also travels to the neighboring zone. The edge tiles
themselves are playable floor, not wall terrain; travel happens when the controlled body steps past
the boundary.

Inventory protection and reagent inspection are also implemented:

- `protect <item>`
- `unprotect <item>`
- `reagents` (text output includes material, total value, tags, and spell-bias hints)
- `wares` / `browse`
- `buy <item> [from <merchant>]`
- `sell <item> [to <merchant>]`
- `services [target]`
- `request <service> [from <provider>]`

## Provider Flags

Recommended CLI flags:

```text
--provider ollama|local|mock|api|openai-compatible
--host <url>
--model <name>
--seed <int>
--origin <id>
--quickstart
--quickstart social
--command <text>
--script <path>
--json
--debug-state
--eval
--episode
--episodes <n>
--max-turns <n>
--transcript <path>
--replay <path>
--replay-assert-final
--episode-log <path>
--record <path>
--disable-background
--max-background-jobs <n>
--background-jobs-per-turn <n>
```

`ollama` and `local` select the live Ollama provider. `api`, `openai`, and
`openai-compatible` select the OpenAI-compatible `/v1/chat/completions` adapter for wild magic,
generated dialogue, and dialogue-claim extraction; use `--host` for the base URL and `--model` for
the model id. Hosted endpoints can read credentials from `SORCERER_OPENAI_API_KEY`,
`SORCERER_API_KEY`, or `OPENAI_API_KEY`; purpose-specific `SORCERER_WILD_API_KEY` and
`SORCERER_DIALOGUE_API_KEY` override the shared key for their lanes. `mock` is deterministic and
fast enough for frequent focused checks, but it should not be treated as the ordinary playtest
resolver.

Live providers exercise the real resolver and write audit logs. They are the right choice
when judging wild magic quality.

Current implemented flags are `--provider`, `--host`, `--model`, `--json`,
`--debug-state`, `--command`, `--script`, `--transcript`, `--replay`,
`--replay-assert-final`, `--eval`, `--episode`, `--episodes`, `--max-turns`,
`--seed`, `--origin`, `--quickstart`, `--episode-log`/`--record`,
`--background-provider`, `--background-host`, `--background-model`, `--enable-background`,
`--disable-background`/`--no-background`, `--max-background-jobs`,
`--background-jobs-per-turn`, and `--background-concurrency`.

Unknown provider names fall back to mock today, so strict live evaluation should use an
explicit known provider name and check the result's `magic.provider` field.

`--seed` now affects ordinary CLI runs as well as episode runs. Use it when comparing atlas/world
roll output: the same seed should produce the same realm status/ruler/effective imperial grip, while
different seeds should give coherent variation.

## Replay

Replay stays lightweight and command-oriented. `--transcript` writes JSONL records for
`transcript_start`, each `transcript_step`, and `transcript_final`. `--episode-log` records use
the parallel `episode_start`, `episode_step`, and `episode_final` shape and can also be fed to
`--replay`. The start record captures seed, origin when present, background settings when present,
and quickstart scene metadata. Each step includes command text, `ActionResult`, debug observation,
and materialized model outputs where available:
`magic.resolvedMagicJson` for validated or pending-materialized spell resolution, `dialogue` for
the normalized generated dialogue response, `dialogueClaimExtractions` for completed post-dialogue
claim extraction, and background job `resultText` for provider-backed background prose. Faulted or
canceled claim-extraction tasks are recorded as technical-failure extraction records, so replay keeps
the attempted extraction visible even when it produced no claims.

`--replay <path>` reads one of those transcripts or episode logs, creates a fresh `GameSession`,
reapplies recorded quickstart setup, and uses replay providers to feed recorded spell, dialogue,
claim-extraction, and background-text outputs back through the normal engine pipeline. It does not
call the model.
Spell replay still re-enters the ordinary apply path: materialized spell JSON is parsed, repaired,
validated against the current replay state, and only then applied.
For split casts, transcript replay feeds materialized spell JSON back into the pending-cast
resolution lane; a replayed `await_cast` should apply the saved materialized result rather than
calling the provider a second time.
`--replay-assert-final` compares a compact final observation summary when the transcript has one,
including the main social flywheel counts: promises, claims, rumors, world turns, bonds, and
memories, plus a background job result-text fingerprint.

This is not a historical rewind system. Prefer preserving command scripts, seeds, transcripts,
audit records, and compact final summaries over building heavier time-travel machinery until a
specific mechanic needs it.

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
with full debug observations and step `ActionResult`s, so generated spell/dialogue/claim outputs
are materialized for replay in the same way as ordinary transcripts. This is intentionally simple,
but it is already useful for catching engine regressions.

At episode end, the runner also checks long-run invariants across the newer lanes: state
validation, save -> load -> save byte stability, loaded-state validation, duplicate promise ids,
bounded faction standing, resource caps, bounded legend weights, and chronicle presence for
completed runs.

For a slower but more realistic unattended playtest, use Ollama:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu --episode --episodes 1 --max-turns 20 --episode-log logs\episode_ollama.jsonl --json
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
