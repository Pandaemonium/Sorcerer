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
- Expanded the mock resolver into broad reusable spell families: damage, area damage, bindings/statuses, transformations, summons, terrain, promises, delayed events, charm/faction change, push/pull, teleport, healing, mana restoration, curses, and overreach rejection.
- Added `dotnet run --project src/Sorcerer.Cli -- --provider mock --eval`, a 25-prompt spell eval harness that starts each prompt from a fresh imperial encounter and checks expected operation families.
- Made restraint statuses (`bound`, `webbed`, `frozen`, `asleep`, `petrified`, etc.) suppress hostile AI turns while active.
- Improved Ollama requests by sending the compact magic context plus operation cards, disabling model thinking when supported, and setting a bounded generation budget.
- Added normalization for common LLM shapes: effect operation keys such as `name`, `operation`, and `effectType`, plus object targets like `{ "id": "soldier_1" }`.
- Live Ollama spot-check with `qwen3.5:9b-cpu` initially failed by thinking into an empty response, then by returning `name` instead of `type`; after the prompt and normalization repairs, `cast bind the nearest enemy in sticky blue webbing` resolved and applied `webbed` plus minor damage.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln`, `dotnet test C:\Games\Sorcerer\Sorcerer.sln --no-build` (8 tests), mock spell eval (25/25), and the live Ollama spot-check all passed.
- Implemented the first playable Godot ASCII GUI over the same `GameSession` backend as the CLI. It includes clickable map targeting, keyboard movement, directional buttons, wait/inspect, spell entry, quick spells, command entry, provider/model controls, pending cast buttons, inventory/status/promise/entity panels, and the shared audit sink.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln` passed, and the Godot scene loaded headlessly with `C:\Tools\Godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe --headless --path C:\Games\Sorcerer\src\Sorcerer.Godot --quit-after 2`.
- Continued the milestone push by turning existing command stubs into shared engine behavior: pickup, drop, use, equip, unequip, focus, unfocus, read, examine, open, talk, journal, standing, and followers now route through `GameSession`.
- Expanded the imperial encounter into a more Hollowmere-shaped seed: a readable containment notice, loose healing/key items, cell walls, a locked cell door, Lio of Hollowmere as a prisoner, and a visible promise that realizes when the cell opens.
- Added stack components for loose item entities, an initial item catalog for tincture/wand/key, simple equipment and focus handling, status expiry on turn advancement, due scheduled-event log surfacing, promise status updates, and rescue consequences that update deeds, faction standing, bonds, and follower state.
- Added regression coverage for shared inventory/equipment/focus behavior and for reading/examining/opening the cell door to realize a promise and free Lio.
- Deferred question log: no user decision is needed for this checkpoint. The next likely architectural decision is how rich the first unattended agent episode runner should be before saves/replay exist, but the current implementation can proceed with a simple JSONL command runner.
- Added `dotnet run --project src/Sorcerer.Cli -- --provider mock --episode`, a deterministic unattended episode runner that writes optional JSONL step records and fails on state validation issues, technical magic failures, bad turn accounting, or successful commands without messages.
- The first episode run found a real invariant bug: defeated soldiers stopped blocking for movement but still looked blocking to `StateValidator`, allowing a later AI move to create a blocking overlap. Defeated actors now become non-blocking corpses with a defeated tag, and a regression test covers the invariant.
- Verification after this checkpoint: the runner passed 2/2 mock episodes with `--max-turns 30` after the corpse/blocking fix.
- Deferred question log: no user decision is needed for this checkpoint. Later we should decide whether episode transcripts should become the lightweight replay format or stay separate until save/load is implemented.
- User decision: episode transcripts are primarily diagnostic. They should be comprehensive enough that they can act as lightweight replay material where practical, but troubleshooting and postmortem clarity matter more than building full replay machinery right now.
- Implemented the first actor-agnostic body-control seam through `possess [target]`. Alert hostile bodies resist and consume a turn; incapacitated nearby bodies can be possessed; player and displaced souls swap bodies; factions/controllers update; inventory stays with the body; and `ControlledEntityId` follows the new body for views and future commands.
- Added tests for hostile resistance and successful possession of an incapacitated body, including the key invariant that the moon pearl stays with the abandoned body rather than following the player soul.
- Deferred question log: no user decision is needed for this checkpoint. The current possession rule is intentionally conservative; later design work should decide how wild-magic soul swaps differ from ordinary possession.
- Added `possess` as a wild-magic operation and taught the mock resolver the body-swap intent family. The operation uses the same engine rule as the typed command and validates as an in-world rejection when the body is alert rather than reporting a technical failure.
- Expanded the spell eval corpus with an alert-body possession rejection case, bringing the mock eval to 26 prompts.
- Added terrain expiration state and turn ticking. `SetTerrain(..., duration)` now records an expiry turn, temporary terrain fades during `AdvanceTurn`, and temporary blocking terrain restores movement when it expires.
- Added regression coverage for temporary blocking terrain expiry.
- Deferred question log: no user decision is needed for this checkpoint. Rich terrain behavior such as fire damage, slippery movement, water conductivity, and vines restraining actors can be added as reusable mechanics later.
- Updated the episode JSONL transcript format to match the user's diagnostic-first decision. Logs now include `episode_start`, `episode_step`, and `episode_final` records with full debug observations, preserving enough command/state material to help future lightweight replay without making replay machinery the priority.
- Added the first background job lane. `GameState` now owns background settings and a queue; `read`/`examine` can enqueue target-detail jobs; turn advancement pumps at most one job by default; completed output is applied to durable canon at the engine boundary; `jobs` exposes the queue; and debug observations include background job cards for diagnostic transcripts.
- Added regression coverage for examining a fixture, queueing a background job, applying it on the next turn, and seeing it in both `jobs` output and debug observations.
- Deferred question log: no user decision is needed for this checkpoint. Real provider-backed background generation can replace the deterministic placeholder text behind the same queue/apply boundary later.
- Moved action and movement blocking behavior out of `GameEngine` string lists and into `StatusRegistry` traits. Status aliases such as `webbed`, `sticky_webbed`, `bound`, `asleep`, `bewildered`, and `disoriented` now inherit reusable mechanics.
- Added regression coverage that registry traits suppress AI actions and block controlled movement.
- Deferred question log: no user decision is needed for this checkpoint. Later we can decide which additional status traits deserve first-class mechanics, such as damage-over-time, fear, charm, concealment, conductivity, or spellcasting disruption.
- Wired data-loaded operation cards into `OperationRegistry.CreateDefault()`. The loader searches for `content/operations`, attaches matching cards when present, and keeps built-in cards as fallback so mechanics remain code-owned.
- Added regression coverage that the default registry sees content card data for `createTiles`.
- Deferred question log: no user decision is needed for this checkpoint. Operation cards need richer examples later, but the architecture now loads them through the default resolver path.
- Added CLI `--script` playback and `--transcript` diagnostic JSONL recording for normal runs. Script files are newline commands with blank lines and `#` comments ignored; `--command` entries append after script commands. Transcripts write `transcript_start`, `transcript_step`, and `transcript_final` records with full debug observations.
- Added `content/scripts/background_smoke.txt` as a checked-in smoke script.
- Verification for this checkpoint included running the script with `--transcript` and confirming start/step/final record types were written.
- Deferred question log: no user decision is needed for this checkpoint. This follows the existing decision that transcripts are diagnostic first and replay-like only where practical.
- Expanded promises from simple visible notes into structured bindings. Promise records now carry source, subject, claimed place, bound place, bound target, trigger hint, realization kind, and realized-in metadata; target entities can hold multiple promise ids through `PromiseAnchorComponent`.
- `createPromise` and `addCurse` can now pass an explicit target and trigger hint to the engine. The engine can also bind by selected target, nearby anchor inference, or region-level promise kind.
- `read`, `open`, and `talk` now realize matching anchored promises through shared engine verbs and emit `realizePromise` deltas for CLI, GUI, tests, and transcripts.
- Added regression coverage for a magic-created promise binding to the containment notice and realizing when the notice is read.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln`, `dotnet test C:\Games\Sorcerer\Sorcerer.sln` (20 tests), mock spell eval (26/26), two unattended mock episodes, script/transcript smoke, and a headless Godot launch all passed.
- Deferred question log: no user decision is needed for this checkpoint. Later design work should decide which realization archetypes become concrete first: memory, threat, item, site, quest, or faction reaction.
- Made the first promise realization archetypes concrete. Memory promises now write world and entity memory records; threat promises spawn a hostile claimant; item promises create pickupable entities; quest/site/omen outcomes write durable canon records.
- Strengthened the promise regression test to verify the memory archetype actually enters both ledgers after reading the promise-bound notice.
- Deferred question log: no user decision is needed for this checkpoint. The current archetypes are intentionally minimal; later tuning should decide how expensive concrete future-writing should be.
- Live Ollama playtesting with `qwen3.5:9b-cpu` found several useful schema dialects: compact `effectId` strings, `kind` as an operation key, `targetId`, `entityName`, nested `details`, array-valued traits, point shorthand, and simple string/numeric costs.
- Repaired those shapes in `SpellResolutionJson`, made operation text extraction handle nested dictionaries and arrays, taught `addStatus` to accept `trait`/`name` aliases, and canonicalized flavorful status ids through `StatusRegistry` at the engine boundary.
- Added parser and shared-session regression coverage for the live dialect repairs. The final live web spell applied `webbed` and suppressed the soldier's turn.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln`, `dotnet test C:\Games\Sorcerer\Sorcerer.sln` (23 tests), mock spell eval (26/26), two unattended mock episodes, script/transcript smoke, live Ollama web spell, and a headless Godot launch all passed.
- Deferred question log: no user decision is needed for this checkpoint. The next resolver quality work should focus on richer operation cards/examples rather than more ad hoc repairs.
- Fixed the Godot provider wiring after noticing the GUI defaulted to mock on startup and only rebuilt the provider on a new run. The GUI now always creates an Ollama-backed `GameSession`; mock remains available only through CLI tooling.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln` and a headless Godot launch both passed.
- Deferred question log: no user decision is needed for this checkpoint. Later we can add explicit GUI support for non-Ollama live providers, but mock should stay hidden from human play.
- Made Godot readout panels, including the log, selectable and context-menu-enabled so playtest text can be copied out of the GUI.
- Repaired another live-provider JSON dialect: if Ollama returns one valid JSON object followed by stray commentary, `SpellResolutionJson` now extracts and parses the first complete object. Ollama parse failures also preserve the raw content in audit records when repair still fails.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln`, `dotnet test C:\Games\Sorcerer\Sorcerer.sln` (24 tests), and a headless Godot launch all passed.
- Deferred question log: no user decision is needed for this checkpoint. Longer term, the GUI could expose an explicit "copy log" button, but normal text selection is now available.
- Fixed malformed target-object handling after a live result surfaced `System.Collections.Generic.Dictionary...` as an entity id. Target normalization now recognizes `{x,y}` point objects, labels unrecognized target dictionaries as malformed instead of stringifying them, and maps malformed operation shapes to technical failures that do not consume a turn or run actor AI.
- Verification after this checkpoint: `dotnet build C:\Games\Sorcerer\Sorcerer.sln`, `dotnet test C:\Games\Sorcerer\Sorcerer.sln` (25 tests), the focused malformed-target regression, and a mock CLI cast smoke all passed.

## Wild Magic Spell Resolver Port

- Ran three parallel deep-dive research passes into the parent Python prototype
  (`C:\Games\WildMagic`) to plan a full port of its resolver mechanics: prompt/schema/parsing
  pipeline, the 17-card capability routing system, and effect application plus eval scoring.
  Cross-referenced against Sorcerer's current C# resolver (22 operations, a 4-card
  `CapabilityRegistry` that was never wired into the live pipeline, `SpellResolutionJson`'s repair
  lane, `SpellCostApplier`, `MechanicalCurseValidator`). User chose the full scope, including the
  two subsystem-heavy items (`setBehavior` AI hooks, `createFlow` tile movement).
- Phase A: made the capability-card system real. `CapabilityRegistry.Select` now ranks by trigger
  hit count, expands one hop via `CommonCombos`, and applies a recall-biased dynamic cap
  (3-7, +1 for a compositional connective) instead of a flat `Take(7)`. Cards load from
  `content/capabilities/*.json` via a new `CapabilityCardLoader` (mirrors `OperationCardLoader`)
  with an 11-card in-code fallback. `WildMagicController.CastAsync` now calls `Select` and builds a
  narrowed `OperationIndex` (core ops plus routed cards' effect types) instead of always exposing
  every registered operation; `OllamaSpellProvider` assembles core prompt + always-on capability
  index + only the routed cards' detail blocks. Narrowing only shapes what's advertised;
  `OperationRegistry.Resolve` still validates against the full registry, matching upstream's own
  choice not to hard-enforce a per-cast schema.
- Phase C: `SpellCostApplier` now floors a negative/unparseable cost amount to its absolute value
  (minimum 1) so a specified cost always bites, while an explicit `0` still stays free per an
  existing tested Sorcerer contract (`ZeroNumericMagicCostsDoNotEmitPlayerFacingCostLines`) that
  predates this port. Curses now stack: `PromiseLedger.FindActive`/`Stack` let `AddCurseOperation`
  and `SpellCostApplier.AddCurseCost` increment an existing matching curse's `Stacks` instead of
  duplicating a promise.
- Phase D (parser): `SpellResolutionJson` now unwraps a common resolution envelope key
  (`resolution`/`result`/`response`/...), aliases bare element names used as an effect `type`
  (fire/frost/poison/etc.) into `damage` + `damageType`, rescues a cost entry whose type is
  actually a registered operation name into `effects`, and strips one-word/placeholder outcome
  text instead of showing it to the player.
- Phase B: ported ~15 new operations across three cost tiers, all registered in
  `OperationRegistry.CreateDefault()`:
  - Group 1 (cheap): `areaStatus`, `modifyInventory`, `addTag`/`removeTag`, `accelerateStatus`.
  - Group 2 (one new component/ledger each): `conjureItem`/`conjureCreature` (template tables +
    a new `GameEngine.SpawnItem`), `addResistance`/`addWeakness` (new `ResistanceComponent` read by
    `CombatSystem.DamageEntity` before the flat Defense reduction), `setFlag` (new
    `GameState.WorldFlags` plus an auto Wild-Debt mechanic that stacks a curse and schedules a
    debt-collector event when the flag reads as debt-shaped), `delayIncoming` (new
    `DelayedDamageComponent`; `CombatSystem` buffers instead of applying damage, released by a new
    `TurnSystem.ReleaseDueDelayedDamage` tick), `editMemory` (wired into the existing
    `MemoryLedger`/`MemoryComponent`; removing the caster from a hostile NPC's memory also calms it
    via a `BondLedger` loyalty bump).
  - Group 3 (subsystem-level): `createPersistentEffect` including sympathetic links (a new
    `PersistentEffectLedger`/`PersistentEffectSystem` fires anchored effects on `on_hit`/`on_strike`
    combat hooks rather than turn cadence; `GameEngine.AttackEntity` now returns
    `IReadOnlyList<StateDelta>` to carry the fired effects, and a sympathetic link mirrors the real
    combat damage dealt rather than inventing a fresh effect); `setBehavior` (new
    `BehaviorTagsComponent`; `AiSystem.RunActorTurns` gained `coward`/`dance`/`freeze_dread`/`mimic`
    branches — `duel`/`lowest_hp` were dropped from the real implementation since the current AI
    loop is single-target and has nothing for them to select between); `createFlow` (new
    `GameState.TileFlows` plus a `TurnSystem.ApplyTileFlows` tick that translates whoever stands on
    a flow tile each turn).
  - All new components/ledgers/state fields are wired into `GameSaveService` (component
    save/load switches, new `GameStateSave` fields appended at the end of the positional record to
    avoid disrupting existing field order).
- Phase D (eval): `SpellEvalHarness` now tags every prompt `common`/`creative`/`exploit`
  (26 -> 44 prompts, with a new prompt per Phase B operation), and adds exploit-leak detection
  (accepted with zero cost, or an effect amount >= 100) and hallucinated-target detection (an
  effect names a literal entity id that never existed in a fresh encounter). The exploit-leak
  check immediately caught a real gap: `MockSpellProvider`'s `Accepted` helper never attached a
  cost to any of its 44 bucket responses; added a sibling `AcceptedWithCosts` helper and two
  exploit-probe-specific buckets (placed ahead of the shared "blast"/"curse" buckets they'd
  otherwise fall into) that price the response instead of giving it away.
- Live smoke-testing surfaced a real grammar bug in the new operations' messages: `target.Name`
  used directly with a hardcoded third-person verb produced "You resists"/"You carries" and a
  "You's wounds" possessive when the target was the player. Added `Subject`/`Verb`/`Possessive`
  helpers to `OperationHelpers` (matching the existing `CombatSystem`/`EffectSystem` second-person
  convention) and fixed all 13 call sites across the three new operation files.
- Verification: `dotnet build Sorcerer.sln`, `dotnet test Sorcerer.sln` (144 tests, up from 118),
  `--eval` (44/44, up from 26/26), two unattended mock episodes, script/transcript smoke, a
  dedicated save/load round-trip test exercising every new component/ledger, several direct CLI
  casts across the new operations, and a headless Godot launch all passed.
- Deferred question log: no user decision is needed for this checkpoint. `setBehavior`'s
  `duel`/`lowest_hp` tags and dynamic per-cast schema enum tightening remain explicitly out of
  scope, matching the plan's stated non-goals; a future sprint could revisit them if multi-target
  AI selection becomes a priority.
