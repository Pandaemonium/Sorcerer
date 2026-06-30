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
- Support fast mock-provider playtests.
- Support live provider resolver evaluation.
- Expose structured observations and action results.
- Expose optional perfect debug state for agents.
- Keep text commands convenient for humans where it is cheap.

## Recommended Modes

### JSON Mode

Agent-friendly commands and results are the primary contract:

```json
{"type":"cast","text":"bind the nearest enemy in blue glass"}
```

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

## Commands To Support Early

Core:

- `inspect`
- `map [radius]`
- `move <direction>`
- direction aliases: `north`, `south`, `east`, `west`, etc.
- `wait`
- `open`
- `pickup`
- `drop <item>`
- `use <item>`
- `equip <item>`
- `unequip <slot-or-item>`
- `focus <item-or-slot>`
- `unfocus`
- `target <x> <y>`
- `untarget`
- `cast <spell text>`
- `journal`
- `help`
- `quit`

Later:

- `talk <message>`
- `read <target>`
- `examine [target]`
- `protect <item>`
- `unprotect <item>`
- `reagents`
- `possess [target]`
- `standing`
- `followers`
- `wares`

## Provider Flags

Recommended CLI flags:

```text
--provider mock|local|api|auto
--seed <int>
--scenario <id>
--command <text>
--script <path>
--json
--no-render
--debug-state
--record <path>
--max-turns <n>
```

`mock` should be fast and deterministic enough for frequent agent checks.

Live providers should exercise the real resolver and write audit logs.

`auto` can be useful for casual play, but strict LLM evaluation should prefer an explicit
live provider so provider failures are visible.

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

## Agent QA Harness

Eventually, add an unattended playtest harness that:

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
