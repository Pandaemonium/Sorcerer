# Handoff

## Latest Sprint: Wild Magic Spell Resolver Port

This section is the most current summary; the rest of this file predates it and covers an
earlier sprint (see docs/JOURNAL.md for the full chronological record, including later sprints
this file was never updated for, such as persistence/factions/dialogue/procedural world).

Ported the parent Wild Magic prototype's resolver mechanics into Sorcerer's C# engine:

- The capability-card system (`CapabilityRegistry`) is now actually wired into
  `WildMagicController`/`OllamaSpellProvider` instead of being dead code, with ranked/combo/capped
  routing and data-loadable cards under `content/capabilities/`.
- ~15 new operations across three cost tiers: `areaStatus`, `modifyInventory`, `addTag`/
  `removeTag`, `accelerateStatus`, `conjureItem`, `conjureCreature`, `addResistance`/
  `addWeakness`, `setFlag` (+ auto Wild Debt), `delayIncoming`, `editMemory`,
  `createPersistentEffect` (+ sympathetic links), `setBehavior`, `createFlow`.
- Cost enforcement ("costs must bite" on negative/missing amounts) and curse stacking.
- `SpellResolutionJson` gained four more live-model repairs (wrapper unwrap, element-damage
  aliasing, cost/effect rescue, junk outcome-text stripping).
- `SpellEvalHarness` grew from 26 to 44 prompts with `common`/`creative`/`exploit` categories and
  exploit-leak/hallucinated-target detection.
- Full detail, file list, and design decisions are in the "Wild Magic Spell Resolver Port" section
  of docs/JOURNAL.md. Test suite: 144 tests (up from 118). Eval: 44/44 (up from 26/26).

## Current Branch

`overnight-cli-magic-slice`

Recent checkpoints:

- `dfc0211` - replaced switch-based magic application with registry-owned operations.
- `3a7e6d0` - added pending casts and hostile actor turns.
- `198226a` - expanded mock resolver coverage and added the spell eval harness.
- Uncommitted checkpoint - implemented the first Godot ASCII GUI over `GameSession`.
- Uncommitted checkpoint - added shared-engine interactables, inventory verbs, readable/examinable fixtures, a locked cell, a prisoner rescue consequence, promise realization, status expiry, and due scheduled-event messages.
- Uncommitted checkpoint - added a deterministic unattended CLI episode runner with JSONL step logs and invariant checks.
- Uncommitted checkpoint - added the first shared-engine `possess` command for actor-agnostic body control.
- Uncommitted checkpoint - added the first low-resource background job lane with `jobs` visibility and deterministic canon application.
- Uncommitted checkpoint - moved action/movement-blocking status behavior into `StatusRegistry` traits.
- Uncommitted checkpoint - added CLI `--script` playback and `--transcript` diagnostic JSONL recording.
- Uncommitted checkpoint - upgraded promises from simple ledger text into target/trigger-aware bindings that can realize through shared verbs.

## What Works

- The CLI and Godot GUI share one `GameSession` backend.
- Godot has a playable ASCII GUI with map targeting, movement buttons/keys, spell entry, command entry, Ollama model controls, pending cast controls, and inventory/status/promise panels.
- Mock provider mode is hidden from the GUI and remains CLI-only for deterministic tests, evals, and unattended episodes.
- Wild magic now flows through `OperationRegistry` and `IOperation` classes.
- Provider technical failures do not consume turns.
- Parseable invalid resolutions become in-world rejections and consume the turn.
- Accepted magic applies transactionally with costs and state validation.
- Protected item costs fizzle before mutation unless explicitly allowed.
- Hostile AI acts after consumed player turns and respects restraint statuses.
- Timed statuses now expire on turn advancement.
- Status mechanics use registry traits for action and movement blocking; aliases such as `webbed`, `sticky_webbed`, `asleep`, and `disoriented` inherit reusable mechanics.
- Timed terrain now expires on turn advancement and restores movement for temporary blocking terrain.
- Due scheduled events now surface as log messages and are removed from the scheduled ledger.
- Background detail jobs can be queued by `read`/`examine`, pumped at turn boundaries, applied to `CanonLedger`, and inspected with `jobs` or debug observations.
- `OperationRegistry.CreateDefault()` now loads matching data cards from `content/operations` when available, with built-in cards as fallback.
- CLI scripts can load newline command files and normal runs can write `transcript_start`/`transcript_step`/`transcript_final` JSONL logs with full debug observations.
- Godot readout panels, including the log, are selectable/copyable.
- Promises now preserve source, subject, claimed place, bound place, bound target, trigger hint, realization kind, and realized-in metadata. `createPromise`/`addCurse` can bind to an explicit target, selected target, inferred nearby anchor, or region; `read`, `open`, and `talk` can realize anchored promises. Memory promises write memory records, threat promises spawn hostile claimants, item promises create pickupable entities, and less concrete outcomes become durable canon.
- `begin_cast`, `await_cast`, and `cancel_cast` provide a CLI pending-cast contract.
- `pickup`, `drop`, `use`, `equip`, `unequip`, `focus`, `unfocus`, `read`, `examine`, `open`, `talk`, `journal`, `standing`, and `followers` route through shared engine behavior instead of renderer-only code.
- The imperial encounter now includes a containment notice, loose items, a locked cell door, Lio of Hollowmere as a prisoner, and a visible promise that can be realized by opening the cell.
- Defeated actors become non-blocking corpses so movement and state validation agree.
- `--episode` runs unattended playtest episodes and fails on technical magic failures, turn accounting errors, missing success messages, or state validation issues.
- `possess [target]` and the `possess` wild-magic operation prove the body-swap control seam: hostile alert bodies resist and consume a turn, incapacitated bodies can be controlled, souls swap, inventory remains with bodies, and views/commands/resolver effects follow `ControlledEntityId`.
- Mock resolver covers broad operation families rather than only damage/promise.
- `--eval` runs a 26-prompt spell corpus and currently passes 26/26 with `mock`.
- Live Ollama spot-checks with `qwen3.5:9b-cpu` exercised web/status, summon, terrain, promise, and heal spells. Early failures exposed useful model dialects; parser and operation repairs now handle compact `effectId`, `kind`, `targetId`, `entityName`, nested `details`, array traits, point shorthand, and simple string/numeric costs. The final web spell applied `webbed` and suppressed the soldier turn.
- The parser also handles a valid first JSON object followed by stray model commentary, which avoids failures like `'c' is invalid after a value` when the JSON itself is usable.

## Verification Commands

```powershell
dotnet build C:\Games\Sorcerer\Sorcerer.sln
dotnet test C:\Games\Sorcerer\Sorcerer.sln
dotnet run --project src/Sorcerer.Cli -- --provider mock --eval
dotnet run --project src/Sorcerer.Cli -- --provider mock --command "cast bind the nearest enemy in sticky blue webbing" --command "wait"
dotnet run --project src/Sorcerer.Cli -- --provider mock --command "move east" --command "pickup red tincture" --command "use red tincture" --command "equip charcoal wand" --command "focus charcoal wand" --command "journal"
dotnet run --project src/Sorcerer.Cli -- --provider mock --episode --episodes 2 --max-turns 30 --episode-log logs\episode_smoke.jsonl
dotnet run --project src/Sorcerer.Cli -- --provider mock --script content\scripts\background_smoke.txt --transcript logs\cli_transcript_smoke.jsonl
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu --command "cast bind the nearest enemy in sticky blue webbing"
& "C:\Tools\Godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe" --headless --path C:\Games\Sorcerer\src\Sorcerer.Godot --quit-after 2
```

Optional live check if Ollama is running:

```powershell
dotnet run --project src/Sorcerer.Cli -- --provider ollama --model qwen3.5:9b-cpu --command "cast bind the nearest enemy in sticky blue webbing"
```

## Important Files

- `src/Sorcerer.Core/GameSession.cs`: command routing, pending casts, actor-turn stitching.
- `src/Sorcerer.Core/Engine/GameEngine.cs`: movement, combat, terrain, statuses, items, equipment/focus, reading, examining, opening doors, possession/body control, prisoner rescue consequences, spawning, actor turns, views.
- `src/Sorcerer.Core/World/PromiseLedger.cs`: promise records, binding metadata, and realization state.
- `src/Sorcerer.Core/Runtime/BackgroundJobs.cs`: low-resource background queue records, settings, and queue helpers.
- `src/Sorcerer.Core/Status/StatusRegistry.cs`: status definitions, aliases, default durations, and mechanical traits.
- `src/Sorcerer.Core/Scenarios/TestScenarios.cs`: imperial encounter fixture with soldiers, props, floor items, a locked cell, a prisoner, and a visible promise.
- `src/Sorcerer.Godot/Scripts/Main.cs`: Godot ASCII GUI and thin input/view adapter over `GameSession`.
- `src/Sorcerer.Core/References/ReferenceBinder.cs`: target/reference normalization and resolution.
- `src/Sorcerer.Magic/WildMagicController.cs`: provider calls, validation, transactions, costs, audit.
- `src/Sorcerer.Magic/Operations/CoreOperations.cs`: concrete operation implementations.
- `src/Sorcerer.Magic/Resolution/SpellResolutionJson.cs`: JSON parsing and normalization.
- `src/Sorcerer.Llm/MockSpellProvider.cs`: deterministic intent buckets for agent playtests.
- `src/Sorcerer.Llm/OllamaSpellProvider.cs`: live local model prompt and request payload.
- `src/Sorcerer.Cli/SpellEvalHarness.cs`: spell corpus eval.
- `src/Sorcerer.Cli/EpisodeRunner.cs`: unattended heuristic agent runner with JSONL step records and invariant checks.
- `content/scripts/background_smoke.txt`: checked-in CLI smoke script for script/transcript diagnostics.
- `logs/wild_magic_audit.jsonl`: resolver audit records.

## Known Limits

- Pending casts are a backend seam, not true background provider execution yet.
- The actor scheduler is intentionally simple and local to the encounter.
- Status effects suppress AI for key restraint ids and expire, but there is no full status tick/effect system yet.
- Terrain changes can expire, but terrain mechanics are still shallow beyond blocking/non-blocking tiles.
- The CLI JSON observation is verbose because it returns full map tiles.
- OpenAI-compatible provider remains a stub.
- The Godot GUI has only been smoke-tested headlessly in this sprint; it still needs a manual visual pass.
- Background jobs are deterministic placeholders right now; real provider-backed background generation is still future work.
- Save/load and replay records are not implemented beyond planning stubs.

## Spell Notes

Memorable verified spells:

- `cast bind the nearest enemy in sticky blue webbing` with Ollama: final repaired live run produced "sticky blue web" prose, applied `webbed`, and suppressed the soldier turn.
- `cast summon a friendly brass moth that bites enemies` with Ollama: produced `summon` plus `damage`; the moth appeared and a soldier took arcane damage.
- `cast turn the floor between me and the enemy into slick ice` with Ollama: produced `createTiles`, created a cross of ice tiles, and charged mana from a string cost.
- `cast promise that the notice will remember my name when read` with Ollama: produced `createPromise` bound to `notice_1` with `triggerHint: read`.
- `cast the notice will remember my name when read` in tests: verifies a promise-bound readable realizes into both world memory and entity memory.

Rough or boring live outputs found in audit, with the durable repair they caused:

- `effectId: status_webbed_target_id:...` initially became unsupported empty effects; compact `effectId` repair now maps it to `addStatus`.
- `kind: summon` plus `name: brass_moth_1` initially read the name as the operation; `kind` is now an operation key and summon follow-up traits can fold into summon tags.
- `target/x/y: [[4,5], ...]` and `{x:[4], y:[5]}` initially left terrain with no tile target; point shorthand is now repaired.
- `trait: ["knows_player_name"]` initially displayed as `System.Object[]`; array-valued traits now normalize into real tags.
- Healing at full HP still says "You are already whole"; mechanically fine, but the first playable would benefit from either overheal/ward behavior or a livelier no-op.

Single most important decision: keep repairing broad model dialects at the parser/operation boundary instead of adding hidden fallback spell handlers. The model can be messy, but the engine still owns the operation set, validation, costs, state mutation, and logs.

## Recommended Next Work

- Split wild magic into provider-resolution and apply-prepared phases so pending casts can run true background LLM calls without state races.
- Add compact agent observation mode for long unattended CLI playtests.
- Expand status traits beyond action/movement blocking, such as damage-over-time, visibility, fear, and charm hooks.
- Add terrain mechanics such as slippery ice, fire, water, and vines.
- Expand actor AI beyond direct chase/attack.
- Deepen the JSONL episode runner with richer policies, compact observations, and replay-ready command transcripts.
- Replace deterministic background text with provider-backed generation behind the same queue/apply boundary.
- Enrich operation cards with stronger prompt guidance, fields, and examples for live providers.
- Expand concrete promise archetypes beyond the first memory, threat, item, quest/site canon handlers.
