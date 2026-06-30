# Journal

## Overnight CLI Magic Sprint

- Started on branch `overnight-cli-magic-slice`.
- Baseline before deeper refactor: `dotnet build C:\Games\Sorcerer\Sorcerer.sln` passed; `dotnet test C:\Games\Sorcerer\Sorcerer.sln --no-build` passed with 5 tests.
- Decision: use `docs/CORE_EXECUTION_MODEL.md` as the sprint design record because the attached objective says to follow it for this run.
- Priority: make the headless CLI magic/combat slice feel vivid and consequential before broadening systems.
- Constraint: do not spend effort on Godot UI tonight beyond keeping the project compiling.
- Replaced the switch-shaped magic application path with an operation registry skeleton backed by real operation classes.
- Added deterministic RNG state to `GameState`, the transaction snapshot, and the imperial encounter seed.
- Added richer magic context views so providers can see caster, visible entities, terrain notes, known promises, and operation cards.
- Changed validation failures from technical failures into in-world spell rejections that consume the cast turn. Provider/network/JSON failures still do not consume a turn.
- Protected item costs now fizzle before mutation and consume the turn unless the resolver explicitly marks the protected item as allowed.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln`, `dotnet test C:\Games\Sorcerer\Sorcerer.sln`, and a scripted mock CLI cast/promise run all passed.
- Added simple hostile actor turns after consumed player turns. Hostile AI now steps toward the player or attacks when adjacent, and the session appends those deltas to the same command result.
- Added a CLI/backend pending-cast seam: `begin_cast` records a cast without mutating state, `await_cast` resolves it through the same magic controller, and `cancel_cast` clears it for free. JSON agents can also send `{"type":"cast","text":"...","await":false}`.
- Added tests for pending casts and hostile actor turns, bringing the suite to 7 passing tests.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln`, `dotnet test C:\Games\Sorcerer\Sorcerer.sln`, a text CLI wait/cast/wait run, and a JSON CLI submit/inspect/await run all passed.
