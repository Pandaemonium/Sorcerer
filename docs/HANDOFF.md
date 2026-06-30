# Handoff

## Current Branch

`overnight-cli-magic-slice`

Recent checkpoints:

- `dfc0211` - replaced switch-based magic application with registry-owned operations.
- `3a7e6d0` - added pending casts and hostile actor turns.
- `198226a` - expanded mock resolver coverage and added the spell eval harness.

## What Works

- The CLI and future GUI share one `GameSession` backend.
- Wild magic now flows through `OperationRegistry` and `IOperation` classes.
- Provider technical failures do not consume turns.
- Parseable invalid resolutions become in-world rejections and consume the turn.
- Accepted magic applies transactionally with costs and state validation.
- Protected item costs fizzle before mutation unless explicitly allowed.
- Hostile AI acts after consumed player turns and respects restraint statuses.
- `begin_cast`, `await_cast`, and `cancel_cast` provide a CLI pending-cast contract.
- Mock resolver covers broad operation families rather than only damage/promise.
- `--eval` runs a 25-prompt spell corpus and currently passes 25/25 with `mock`.
- Ollama spot-check with `qwen3.5:9b-cpu` applied a webbed status after prompt and normalization repairs.

## Verification Commands

```powershell
dotnet build C:\Games\Sorcerer\Sorcerer.sln
dotnet test C:\Games\Sorcerer\Sorcerer.sln --no-build
dotnet run --project src/Sorcerer.Cli -- --provider mock --eval
dotnet run --project src/Sorcerer.Cli -- --provider mock --command "cast bind the nearest enemy in sticky blue webbing" --command "wait"
```

Optional live check if Ollama is running:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu --command "cast bind the nearest enemy in sticky blue webbing"
```

## Important Files

- `src/Sorcerer.Core/GameSession.cs`: command routing, pending casts, actor-turn stitching.
- `src/Sorcerer.Core/Engine/GameEngine.cs`: movement, combat, terrain, statuses, spawning, actor turns, views.
- `src/Sorcerer.Core/References/ReferenceBinder.cs`: target/reference normalization and resolution.
- `src/Sorcerer.Magic/WildMagicController.cs`: provider calls, validation, transactions, costs, audit.
- `src/Sorcerer.Magic/Operations/CoreOperations.cs`: concrete operation implementations.
- `src/Sorcerer.Magic/Resolution/SpellResolutionJson.cs`: JSON parsing and normalization.
- `src/Sorcerer.Llm/MockSpellProvider.cs`: deterministic intent buckets for agent playtests.
- `src/Sorcerer.Llm/OllamaSpellProvider.cs`: live local model prompt and request payload.
- `src/Sorcerer.Cli/SpellEvalHarness.cs`: spell corpus eval.
- `logs/wild_magic_audit.jsonl`: resolver audit records.

## Known Limits

- Pending casts are a backend seam, not true background provider execution yet.
- The actor scheduler is intentionally simple and local to the encounter.
- Status effects suppress AI for key restraint ids, but there is no full status tick/effect system yet.
- Terrain changes are durable but durations are not ticked down yet.
- The CLI JSON observation is verbose because it returns full map tiles.
- OpenAI-compatible provider remains a stub.
- Godot GUI is kept compiling but has not been developed in this sprint.
- Save/load and replay records are not implemented beyond planning stubs.

## Recommended Next Work

- Split wild magic into provider-resolution and apply-prepared phases so pending casts can run true background LLM calls without state races.
- Add compact agent observation mode for long unattended CLI playtests.
- Add status definitions with mechanical traits instead of hard-coded restraint ids.
- Add terrain duration ticking and terrain mechanics such as slippery ice, fire, and vines.
- Expand actor AI beyond direct chase/attack.
- Add a JSONL episode runner for unattended playtesting with invariant checks after every command.
- Promote operation cards into data-loaded content and include richer examples for live providers.
