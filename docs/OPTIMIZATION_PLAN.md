# Optimization Plan — Wild Magic Flexibility, Context/Latency, Dialogue, Voice

Status: **partially implemented — see Implementation Status below for the handoff checklist.**
Written 2026-07-05 on branch `wildmagic-import`.
Companion to [MAGIC_RESOLVER_ARCHITECTURE.md](MAGIC_RESOLVER_ARCHITECTURE.md),
[CAPABILITY_ROUTING.md](CAPABILITY_ROUTING.md), [DIALOGUE_CONTEXT_ROUTING.md](DIALOGUE_CONTEXT_ROUTING.md),
[AESTHETICS_AND_TONE.md](AESTHETICS_AND_TONE.md), [FEEL_NOTES.md](FEEL_NOTES.md).

Four workstreams. Each task lists the files to touch, the change, and acceptance criteria.
Tasks are independently landable unless a dependency is called out. WS0 (measurement) should land
first so every other workstream can prove its effect.

## Implementation Status — COMPLETE (2026-07-05, session 3)

**Every workstream task has landed. Build clean, 599/599 tests green.** Changes are in the working
tree on `wildmagic-import` (uncommitted). Sessions 2–3 implemented the plan from scratch (an earlier
"Opus implemented the plan" report did not match the repo — no commits/stashes/worktrees existed).

Session 3 completed the remaining checklist: router-intent prompt + router telemetry (1.6),
recentDialogue continuity (3.4), speech-prompt reorder (3.5), dialogue voice directives + region
voice (3.7), assembler byte budget (3.3), the full voice pass (4.1–4.3 + mock lines), the audit
miner (`scripts/mine_wild_magic_audit.py`, 0.2), and the needs_capability hatch (1.2, with a test).
Doc sync done (this file + CAPABILITY_ROUTING.md + MAGIC_RESOLVER_ARCHITECTURE.md).

**Still worth doing (needs a running Ollama, not codeable here):** the live-playtest telemetry
comparison in item 10 below — confirm `loadMs ≈ 0` across interleaved cast/talk, prompt tokens
roughly halved, and a prefix-cache hit on the second consecutive cast. All code and tooling for it
are in place.

### DONE — landed and test-verified

| task | where |
|---|---|
| **2.1** unified `num_ctx` | new `src/Sorcerer.Llm/OllamaDefaults.cs` (default 8192, `SORCERER_NUM_CTX` override); all 7 hard-coded sites replaced (spell provider ×2, spell router, dialogue provider, dialogue router, claim extractors ×2, background generator) |
| **3.6** speech cap 1024 | `DialogueProviders.cs` — all 4 speech/retry `maxTokens` sites |
| **0.1** Ollama token/timing telemetry | new `Sorcerer.Core/Telemetry/ProviderCallStats.cs` + `Sorcerer.Llm/Diagnostics/OllamaStats.cs`; `LlmTrace` entries carry `Stats`; plumbed through `SpellProviderResult` → `MaterializedMagicResolution.ProviderStats` → `SpellRoutingRecord.ProviderStats` (wild audit) and `DialogueProviderResult.Stats` → `DialogueAuditEntry.ProviderStats` (dialogue audit, `GameSession.RecordDialogueAudit`) |
| **1.1** recall floor | `OperationRegistry.ToRoutedIndex`: zero-card selection now advertises **every** op name (lean cards, core-common full) instead of core-only |
| **2.2** core shrink | `CoreCommon` = { damage, heal, addStatus, removeStatus, message, createTiles }; `setFlag` made routable via the prophecy card |
| **1.3** (first tranche) | six new built-in capability cards in `CapabilityCard.cs`: `motion_kinetics` (push/pull/teleport), `area_burst` (areaDamage/areaStatus), `protection_wards` (addResistance/addWeakness), `restoration` (restoreMana), `curse_mark` (addCurse), `possession` (possess) — every op demoted from CoreCommon has a routed home |
| **2.3** compact ops + stable prefix | new `src/Sorcerer.Llm/SpellPromptBuilder.cs`, used by **both** Ollama and OpenAI spell providers; system prompt = static core rules → consequence line → capability index → per-cast tail (supported list, text-rendered op guidance, loaded capability blocks); wire context omits the operation catalog and all null fields; spell text moved last in the user message |
| **1.4** yes-at-a-price | in `SpellPromptBuilder.CoreRules` (replaces the old blanket "Reject spells that are too broad…" line) |
| **1.5** consequence-type line | `SpellPromptBuilder.ConsequenceTypesLine` (~300 bytes, static) |
| **4.4** outcome register | CoreRules: outcomeText = concrete/sensory, no fate-speak |
| **2.7** slim repair lane | `SpellPromptBuilder.RepairSystem/RepairUser`: core rules + parse error + previous output + supported list + valid-target-id line + spell; no context JSON, no cards |
| **2.4** slim promises | `EngineViewBuilder.ToResolverPromiseCard` (id/kind/status/text/subject/claimedPlace/triggerHint; debug fields stay null and drop off the wire) |
| **2.5** entity trim | nearest-first cap of 14 visible entities (caster always kept); `PerceivedEntity.RelativeX/Y` now nullable and omitted |
| test updates | `SpellRouterTests.RoutedIndexTrimsUnselectedRoutableCardsAndSelectionRestoresThem` rewritten to lock recall-floor semantics |

### DONE — session 3 (landed, test-verified)

| task | where |
|---|---|
| **1.6** router-intent prompt + examples | `SpellRouterPrompt.System` (route by accomplished effect, 3 inline examples in the cacheable prefix); `OllamaSpellRouter` now parses `OllamaStats` into its trace |
| **3.4** recentDialogue continuity | per-speaker ring buffer in `GameSession` (session-scoped, not persisted); `DialogueRequest.RecentDialogue`; appended after each successful turn, trimmed to 6 lines × 240 chars |
| **3.5** speech-prompt reorder | `OllamaDialogueProvider.UserPrompt` now stable-first (speaker/listener/scene/cards/memories), volatile last (recentDialogue, turn, retryNote, playerText) |
| **3.7** dialogue voice + region voice | grounded-voice block in `OllamaDialogueProvider.SystemPrompt`; `GameEngine.CurrentRegionVoice` → `DialogueSceneCard.RegionVoice`, filled by the assembler |
| **3.3** assembler byte budget | `DialogueContextAssembly.FitContextCardsToBudget` (4 KB card budget, 320-char line cap, router order, marks Truncated) |
| **WS4** voice pass | spec in AESTHETICS_AND_TONE.md ("Narration Voice: Grounded and Specific"); canned strings rewritten in `WorldReactionSystem`, `RumorSystem`, `InteractionSystem`, `GameSession`, mock `DialogueProviders`; background prompt in `BackgroundTextGenerators`; 4 characterization tests updated to the new phrasings |
| **0.2** audit miner | `scripts/mine_wild_magic_audit.py` — rejection/tech-failure/zero-route/boring buckets + providerStats latency table; `--dialogue` mode |
| **1.2** needs_capability hatch | `SpellResolutionJson.TryReadNeedsCapability`; `SpellProviderResult.RequestedCapability`; detected in both live providers; `WildMagicController` loads the card and re-resolves once (`CapabilityRegistry.Find`); prompt line in `SpellPromptBuilder.CoreRules`; test `NeedsCapabilityAnswerLoadsCardAndReResolvesOnce` |
| doc sync | this file, CAPABILITY_ROUTING.md, MAGIC_RESOLVER_ARCHITECTURE.md |

### REMAINING — needs a live Ollama (not codeable in this environment)

- **Live telemetry check:** run `--live-playtest`, then `python scripts/mine_wild_magic_audit.py`.
  Expect `loadMs ≈ 0` across interleaved cast/talk (num_ctx unified), prompt tokens on casts roughly
  halved vs. the ~5,700 baseline, and prompt tokens dropping on the second consecutive cast
  (prefix-cache hit). Run `--reparse-audit` to confirm the slimmer repair lane still fixes the
  historical corpus. Add a dated FEEL_NOTES section after the run.

### Findings / gotchas discovered while implementing

- **3.1 is a non-issue and needs no work**: `OllamaDialogueProvider.UserPrompt` never serialized
  `capabilityCards` into the speech prompt — the ~1.9 KB only appears in the *audit record* of the
  request object. The plan's baseline table overstated the speech payload accordingly.
- `MagicContextView.Operations` is non-nullable for the engine/audits/mock/replay;
  `SpellPromptBuilder.WireContextJson` sets it to `null!` **only** on the provider wire copy so the
  null-omitting serializer drops it. Don't "fix" that null.
- `ResolverLensView`'s prose fields (`Magnitude`/`Volatility`/`CostFraming`) are
  characterization-locked by `GameSessionCharacterizationTests` — leave the record shape alone.
- `SpellRoutingRecord.ContextPayloadBytes` still measures the full context **object** (including
  op cards), not the wire payload; real prompt size now comes from `ProviderStats.PromptTokens`.
  Don't tune against ContextPayloadBytes.
- Capability trigger lists are recall-biased on purpose; the six new cards' triggers were written
  conservatively — the 0.2 miner should tune them against real casts.

## Measured baseline (from `logs/wild_magic_audit.jsonl`, last 200 casts, and `logs/dialogue_audit.jsonl`, last 60 turns)

**Wild magic context JSON (user message), avg ~18.0 KB, max ~23 KB:**

| slice | avg bytes | notes |
|---|---:|---|
| operations | 9,731 | 27 op names + 27 cards; **19 still ship full promptGuidance/fields even on `"heal my wounds"`** |
| knownPromises | 2,320 | full `PromiseCard` incl. eligibility-debug fields the resolver never uses |
| visible | 2,016 | |
| lore | 1,514 | |
| resolverLens | 822 | |
| recentEvents / terrain / reagents / caster | ~1,400 | |

Plus a ~5.5 KB static system prompt + capability index + routed card blocks. Per cast: 1 router
call (num_ctx 8192) + 1 resolve call (num_ctx 8192) + optional repair call.

**Dialogue request JSON, avg ~7.8 KB, max ~14.3 KB:**

| slice | avg bytes | notes |
|---|---:|---|
| contextCards | 5,565 | routed cards (good design, payloads still heavy) |
| capabilityCards | 1,904 | **static proposal-schema text sent to the speech call, which is explicitly forbidden from proposing mechanics** |
| scene + speaker + listener | 2,490 | |
| recentMemories/rumors/claims | ~1,200 | |

Per talk: 1 context-router call (num_ctx 2048) + 1 speech call (**num_ctx 4096 — the max observed
request is ~4.3 K tokens, so worst-case prompts already truncate**) + background parser-router
(2048) + parser detail (2048).

**Hidden latency hazard:** five different `num_ctx` values (8192 / 4096 / 2048) are used against
the same Ollama model. Ollama restarts the model runner when options like `num_ctx` change, so
interleaved cast → talk → router calls can silently force runner reloads and full prompt
re-processing. Also, every call re-sends a differently-shaped prompt, so the KV prefix cache almost
never hits.

**Output-size measurement (from the same logs, approx tokens = chars/4):**

| call | prompt tokens (p50 / p95) | output tokens (p50 / p95 / max) |
|---|---|---|
| wild magic resolve | ~5,700 / ~7,100 (ctx ~4,300 + system ~1,400) | **112 / 206 / 347** |
| dialogue speech | ~1,540 / ~3,500 (req ~1,240 + system ~300) | **195 / 315 / 522** |

This is the load-bearing insight for the <15 s target:

- **Wild magic is prompt-eval-bound, not generation-bound.** The model emits only ~112 tokens but
  must first process ~5,700 prompt tokens *uncached* on every cast. At a typical local-9B prompt-eval
  rate this is the dominant cost. → The primary latency levers are the context cuts (2.2–2.5) and
  prefix caching (2.3), **not** trimming `num_predict` (the model already stops early — leave it).
- **Dialogue is already token-light.** ~1,540 in / ~195 out. Its main latency risk is **reload
  thrash** (num_ctx changing between a preceding cast at 8192 and the speech call at 4096) plus the
  extra serial router call — not prompt size. → 2.1 is the biggest dialogue win.
- Output caps (`num_predict` / `maxTokens`) are **tail protection**, not p50 levers, because
  generation halts at end-of-JSON well before the cap.

---

## Decisions & targets (2026-07-05, from the user)

1. **Hard latency target: < 15 s per action** for both wild magic and dialogue (visible result;
   background parser calls excluded). The measurements above say this is comfortably reachable once
   wild-magic context is cut ~2× and prefix-cached, and once reload thrash is eliminated.
2. **One model for all calls for now** — routers included, on the same model as the main call.
   Prior tests put warm router calls under ~1 s, and with uniform options (2.1) they add no reload.
   Therefore the router **stays always-on by default**; the keyword skip-lanes (2.6, 3.2) drop to
   *optional, only if a session is measured over budget*. A very small dedicated router model is an
   **experiment to try and measure** (see 2.6), not the baseline.
3. **Include the mock/canned lines in the voice pass** (4.2) so they don't linger as bad examples.

---

## WS0 — Measurement first (small, do before everything)

### 0.1 Capture Ollama timing/token stats in every trace and audit
- Files: `src/Sorcerer.Llm/Diagnostics/LlmTrace.cs`, all Ollama providers/routers
  (`OllamaSpellProvider.cs`, `OllamaSpellRouter.cs`, `DialogueProviders.cs`, `DialogueRouters.cs`,
  `DialogueClaimExtractors.cs`, `BackgroundTextGenerators.cs`), `src/Sorcerer.Magic/Auditing/*`.
- Change: Ollama's `/api/chat` response includes `prompt_eval_count`, `eval_count`,
  `prompt_eval_duration`, `eval_duration`, `total_duration`, and `load_duration`. Parse them and
  record them in `LlmTrace` entries and in the spell/dialogue audit records (extend
  `SpellRoutingRecord`, which already carries `contextPayloadBytes`). `load_duration` spikes are the
  smoking gun for the num_ctx-thrash problem above.
- Accept: a live playtest produces per-call rows of {purpose, promptTokens, genTokens, promptMs,
  genMs, loadMs}; a tiny script or harness flag summarizes p50/p95 per purpose.

### 0.2 Rejection/miss mining script for the audit log
- Files: new `scripts/` or a CLI harness mode alongside `SpellEvalHarness.cs`.
- Change: scan `logs/wild_magic_audit.jsonl` (~4,000 casts) and bucket: intentional rejections by
  `rejectedReason`, technical failures by validation code (`unsupported_effect`,
  `operation_shape`, `promise_effect_missing`, …), casts where zero capabilities routed, and casts
  whose spell text got only `message` effects (the "boring magic" smell). Output the top-N spell
  texts per bucket.
- Accept: one command prints the ranked list; WS1.2 and WS1.3 consume it.

---

## WS1 — Wild magic: "ask for anything, the game delivers"

### 1.1 Un-routed spells must still see the whole magic surface (recall floor)
- Files: `src/Sorcerer.Magic/WildMagicController.cs` (`BuildSpellRequest`),
  `src/Sorcerer.Magic/Operations/OperationRegistry.cs` (`ToRoutedIndex`).
- Problem: when neither keywords nor the LLM router select a card, the prompt advertises **core
  operations only**. A spell like "make the fresco weep real tears" that routes nowhere cannot see
  `conjureFixture`, `editMemory`, `createFlow`, etc. — the model can't propose what it never saw.
  (Validation would accept them, but the model has no way to know.)
- Change: when zero cards route, advertise **all** effect types as lean (name + one-line summary)
  cards instead of omitting them. Routed casts keep today's narrowing.
- Accept: audit shows unrouted casts with `advertisedOperationCount` == full registry; an eval
  spell that names an exotic effect with no trigger words succeeds.

### 1.2 `needs_capability` escape hatch (one bounded re-route)
- Files: `OllamaSpellProvider.cs` core prompt, `SpellResolutionJson.cs`, `WildMagicController.ResolveAsync`.
- Change (was designed-but-deferred in CAPABILITY_ROUTING.md): allow the model to answer
  `{"needsCapability":"<index name>"}` instead of a resolution when the loaded mechanics don't fit.
  The controller loads that card (validated against the registry) and re-resolves **once**; a second
  request is a technical failure. The capability index line is already in every prompt, so the
  model always knows the menu even when detail isn't loaded.
- Accept: a spell whose only matching card has no trigger overlap (e.g. paraphrased memory-edit)
  round-trips through the hatch and applies; audits record the extra hop; loop is bounded at 1.
- Dependency: 1.1 makes this rarer; still worth having for detail-heavy cards.

### 1.3 Close capability gaps found in the data
- Files: `content/capabilities/initial_capabilities.json` (preferred over built-ins),
  possibly new ops/consequences only where truly missing.
- Change: run 0.2 and mint cards for the top misses. Candidate families visible from the current
  card list (verify against data before adding): **illusion/sensory** (light, darkness, sound,
  disguise → addStatus/concealed, message, conjureFixture), **motion & tempo** (fly, levitate,
  haste, slow → status registry entries + addStatus), **dispel/counter** (removeStatus,
  update_trigger, update_persistent_effect via consequence), **scrying/knowledge** (reveal routes,
  read intentions → revealed status, create_route, canon/memory reads), **weather/ambience**
  (createFlow + createTiles + message). Each card needs triggers, index line, prompt block, and at
  least one example. Where a family needs a status to be mechanically real (flight, haste), add the
  status to `StatusRegistry` with real engine meaning — don't ship prose-only cards.
- Accept: re-run the miner; the targeted buckets shrink; live feel test shows the new families
  produce mechanically-backed results (per the prose/mechanics-agree rule in
  MAGIC_RESOLVER_ARCHITECTURE.md).

### 1.4 "Yes, at a price" before rejection (partial fulfillment)
- Files: `OllamaSpellProvider.cs` core prompt (one sentence), no engine change required.
- Problem: the prompt says "Reject spells that are too broad, too remote, or impossible," which
  contradicts the Strong Spells doctrine (default instinct: yes, at a price). Audit shows
  rejections for spells that had a reasonable local downscale.
- Change: instruct — "Before rejecting an overreaching spell, deliver the largest local version the
  operations support, at a severe cost, and let outcomeText admit the magic answered smaller than
  asked. Reject only literal win buttons, infinite resources, or global rewrites." Keep the
  existing rejection lane for those.
- Accept: eval set of 10 over-ask spells ("kill every soldier in the empire", "flood the valley"):
  ≥7 resolve to scaled local effects with severe costs, ≤3 clean rejections, 0 free wins.

### 1.5 Advertise the consequence grammar cheaply
- Files: `OllamaSpellProvider.cs` (`BuildCorePrompt`), `Operations` card for `consequence`.
- Problem: `consequence` is the universal bridge to social/world systems (tags, memory, wants,
  services, routes, canon, rumors, promises) but ships as a lean name-only card on most casts.
- Change: add one compact line to the always-on core prompt: the list of supported
  `consequenceType`s with 3-4-word glosses. That's ~300 bytes for a map of everything the world
  engine can do.
- Accept: casts that need social/world effects (audited via 0.2) start emitting typed consequences
  without the full consequence card loaded.

### 1.6 Router prompt: route by intent, not just vocabulary
- Files: `src/Sorcerer.Llm/SpellRouterPrompt.cs`.
- Change: keep the tiny call, but tell the router to pick cards for what the spell is **trying to
  accomplish** (its effects on the world), not for word overlap, and to prefer 2 cards for a spell
  with two clauses. Add the two or three highest-value routing examples inline (they're static, so
  they stay in the cacheable prefix).
- Accept: router-eval fixture (paraphrased spells with no trigger words) routes correctly ≥80%.

---

## WS2 — Resolver context & latency

Target: **cast visible result < 15 s** (p95), via **user-message context ≤ 8 KB avg** (from 18 KB)
and a warm prefix cache, with no loss on the spell eval suite. Because wild magic is prompt-eval-
bound (~5,700 prompt tokens today), context reduction and caching *are* the latency plan — leave
`num_predict` at 1200 (the model stops at ~112 tokens). Land 2.1–2.3 first; they're the tonnage.

### 2.1 Standardize `num_ctx` (and options) per model  ← biggest single latency win
- Files: all Ollama callers listed in 0.1.
- Change: one shared `num_ctx` (8192) and identical option shape for every call against the same
  model, so Ollama never rebuilds the runner between purposes. Make it configurable via the
  existing `LlmConfiguration`. Since everything is on one model (decision 2), a router call after a
  cast then reuses the already-loaded 8192 runner with zero `load_duration` — a short router prompt
  stays ~1 s because prompt-eval is proportional to actual tokens, not to `num_ctx`.
- Accept: 0.1 telemetry shows `load_duration` ≈ 0 across an interleaved cast/talk/router session.
- Note: this **fixes the dialogue truncation bug** (max observed dialogue prompt ~4.3 K tokens vs
  num_ctx 4096) as a side effect.

### 2.2 Shrink the always-full core operation set
- Files: `OperationRegistry.cs` (`CoreCommon`, `IsCore` flags on ops), capability card content.
- Problem: 19 operations ship full promptGuidance/fields/examples on every cast, including a bare
  heal. That's most of the 9.7 KB operations slice.
- Change: keep full cards always-on only for a truly common tier (~6: `damage`, `heal`,
  `addStatus`, `removeStatus`, `message`, `createTiles`). Everything else (areaDamage, areaStatus,
  addResistance/addWeakness, push/pull/teleport, possess, addCurse, setFlag, restoreMana,
  createTile, addTrait) becomes routable: give each a home on an existing or new capability card
  (e.g. `motion` for push/pull/teleport, `protection` for resist/weaken, `area_shaping` for the
  area variants) and let `ToRoutedIndex` demote them to lean cards otherwise. **Depends on 1.1**
  so lean names remain visible when unrouted.
- Accept: operations slice ≤ 4 KB on a simple cast; spell eval suite (incl. new cards' triggers)
  shows no regression; audits confirm routed casts still get full detail.

### 2.3 Move static prompt content out of the per-cast user message, order for KV-cache reuse
- Files: `OllamaSpellProvider.cs`, `EngineViewBuilder.MagicContext`, `MagicContextView`.
- Change: (a) render operation cards as compact text lines (name — summary — fields: a,b,c —
  example) instead of pretty JSON records; JSON key overhead is roughly half the slice. (b) Put all
  static/slow-changing content in one stable-order prefix: core rules → consequence-type line →
  capability index → core op guidance. Per-cast material (routed card details, context JSON, spell
  text) goes after it, spell text last. Consecutive casts then share a multi-KB cached prefix.
- Accept: telemetry shows `prompt_eval_count` on the second consecutive cast drops well below the
  first (prefix cache hit); parse/repair rates unchanged.

### 2.4 Slim `knownPromises` for the resolver
- Files: `EngineViewBuilder.MagicContext` (use a resolver-specific projection, not `ToPromiseCard`).
- Change: resolver needs {id, kind, status, text, subject, triggerHint}. Drop the eligibility-debug
  and provenance fields (LastEligibilityFailure/Context/Turn, SourceClaimId, SourceListenerSoulId,
  …) from the magic context only — GameView keeps them.
- Accept: promises slice ≤ 700 bytes avg; promise-related evals (prophecy card, promise ledger
  validation) still pass.

### 2.5 Compact entity/terrain/lens encodings
- Files: `EngineViewBuilder.MagicContext`.
- Change: drop `RelativeX/RelativeY` (derivable; prompt already gets caster position), drop
  `Glyph` for the resolver, emit tags only when non-empty, cap visible entities at nearest ~12 with
  a `"+N more"` line. ResolverLens: the `Notes` list duplicates the structured fields — send only
  the notes (they're what the prompt tells the model to read) and the three stat ints.
- Accept: visible+terrain+lens ≤ 2.5 KB on a busy zone; targeting evals still pass.

### 2.6 (Optional) Skip / shrink the router only if measured over budget
- Files: `WildMagicController.RouteCapabilitiesAsync`, `CapabilityRegistry`, `SpellRouterFactory`.
- Decision 2 keeps the LLM router always-on by default (warm ~1 s, no reload after 2.1). Treat this
  task as **latency insurance**, applied only if 0.1 telemetry shows a session over the 15 s budget:
  - **Skip-lane:** run keyword selection first; call the LLM router only when keyword routing is
    ambiguous (zero hits, or a compositional connective with fewer than 2 hits).
  - **Tiny-router experiment:** wire a small model (e.g. a 1–2 B) behind the existing
    `SORCERER_ROUTER_*` config and A/B its warm latency and routing accuracy vs. the main model on
    the same box. Note the tradeoff: a second resident model costs VRAM and *reintroduces* a
    model-load cost unless it stays warm — measure before adopting.
- Accept: if applied, telemetry shows router overhead is not the thing pushing any action past 15 s;
  router-eval from 1.6 shows no routing-quality regression.

### 2.7 Cap repair-lane spend
- Files: `OllamaSpellProvider.RetryAfterInvalidJsonAsync`.
- Change: the repair call re-sends the full system prompt + full context. Send instead: core rules
  + the parse error + the previous invalid output + the spell text + a **one-line** target/entity
  id list (repair fixes shape, not world reasoning).
- Accept: repair success rate unchanged on the audit replay corpus (`AuditReparseHarness`), repair
  prompt ≤ 3 KB.

---

## WS3 — Dialogue: fantastic results at low latency

Target: **visible reply < 15 s** (p95), better continuity and voice. Dialogue is already token-light
(~1,540 in / ~195 out), so the latency work here is mostly **2.1 (kill reload thrash)** plus keeping
the prompt cacheable (3.5); the payload cuts (3.1, 3.3) and continuity (3.4) are primarily
quality/robustness, not raw speed.

### 3.1 Stop sending proposal-schema `capabilityCards` to the speech call
- Files: `DialogueContextAssembler.BuildDialogueRequest` / `DialogueCapabilityCards()`,
  `OllamaDialogueProvider.UserPrompt`.
- Problem: ~1.9 KB of claim/action schema text goes to a call whose system prompt says "Do not
  output proposals… A separate parser will inspect the reply." It costs tokens and invites schema
  leakage into speech.
- Change: route those lines to the parser requests only (`DialogueClaimExtractors` path). The
  speech call keeps zero mechanics text.
- Accept: dialogue request avg ≤ 6 KB; parser proposal quality unchanged on
  `dialogue_promise_eval` fixtures.

### 3.2 (Optional) Keyword-first context routing — only if measured over budget
- Files: `GameSession.RouteDialogueContextAsync`, `DialogueRouters.cs`.
- Per decision 2 the context-router stays always-on by default. Apply this only if 0.1 telemetry
  shows the serial router call pushing dialogue past 15 s: promote the deterministic keyword table in
  `MockDialogueRouter` to a shared `KeywordDialogueRouter` in front of the LLM router — a clean ≥1
  keyword hit uses the deterministic bundle and skips the LLM call; long/multi-topic lines fall
  through to the LLM router; union results as wild magic does.
- Accept: if applied, LLM context-router runs on a minority of turns with no routed-card relevance
  loss on a 20-turn spot-check.

### 3.3 Enforce a byte budget in the assembler
- Files: `DialogueContextAssembler` (it already has `DialoguePayloadSizer` and per-card line
  limits).
- Change: total request budget (~6 KB): rank selected cards by router order, trim card lines
  oldest-first to fit, mark `Truncated`. Hard-cap `recentMemories` (5 → keep) but cap each memory
  line length; cap rumor/claim line lengths.
- Accept: max request over a long session ≤ 8 KB (was 14.3 KB); no truncated-JSON parse failures.

### 3.4 Add conversation continuity: a `recentDialogue` slice
- Files: `DialogueContextAssembler`, `DialogueRequest`, `OllamaDialogueProvider.UserPrompt`.
- Problem: the speech call sees recent memories and 2 recent event messages, but not necessarily
  its own previous lines in this conversation — continuity leans on lossy memory records.
- Change: keep the last N (4–6) player/NPC exchanges **with this speaker** (from the message log or
  a small per-speaker ring buffer) as compact `"player: …" / "Brel: …"` lines. This is the
  single biggest "fantastic results" lever: the model can call back, stay consistent, and not
  re-introduce itself.
- Accept: multi-turn live transcript shows callbacks/consistency (no re-greeting, remembers what it
  just offered); slice costs ≤ 800 bytes.

### 3.5 Order the speech prompt for prefix-cache reuse across a conversation
- Files: `OllamaDialogueProvider.UserPrompt`.
- Change: serialize stable parts first (speaker, listener, scene, contextCards), volatile parts
  last (recentDialogue, retryNote, playerText). Today `retryNote`/`turn`/`playerText` come first,
  so consecutive turns share almost no prefix.
- Accept: `prompt_eval_count` drops on turn 2+ of a same-NPC conversation.

### 3.6 Speech generation cap as a runaway backstop only (do not truncate long replies)
- Files: `DialogueProviders.cs` (`maxTokens: 1200` for speech), keep parser caps as-is.
- Change: set the cap to **1024** — comfortably above the longest measured legitimate reply
  (p50 195 / p95 315 / max 522), so no real reply is ever truncated. This is purely a guard against a
  runaway generation, **not** a latency lever: the model stops at end-of-JSON (~195 tokens typical),
  so the cap almost never binds. If replies feel long, shorten them by tightening the length
  instruction in the system prompt (it currently invites "two compact paragraphs"), never by lowering
  the cap.
- Accept: no mid-sentence cutoffs in 50 sampled replies (including the longest); cap is only hit by
  genuine runaways.

### 3.7 Voice directives in the dialogue system prompt (see WS4 for the shared spec)
- Files: `OllamaDialogueProvider.SystemPrompt`, `DialogueContextAssembler` (region voice line into
  the scene card).
- Change: add ~4 sentences: *"Speak as a particular person with your own concerns, not as an
  oracle. Ground answers in concrete local detail — names, prices, roads, weather, work. Let your
  want and your bond with the listener steer what you volunteer or hold back. Avoid portent and
  mystique unless this character genuinely trades in it; never speak on behalf of 'the world'."*
  Inject the region's `VoiceSummary` (already available to the resolver lens) as one line.
- Accept: A/B 20 replies on the same fixture; graders (or the user) prefer the grounded set;
  AESTHETICS_AND_TONE regional-voice requirement is now actually wired for dialogue.

---

## WS4 — Voice pass: organic people, not ambient mysticism

The user's direction, refining AESTHETICS_AND_TONE.md: keep COLOR vs. MARBLE, but the *narration
layer* should sound organic and concrete — people with their own goals reacting to your actions —
not vague portent ("rumors spread about the color of your magic").

### 4.1 Write the shared voice spec (one paragraph, one place)
- Files: `docs/AESTHETICS_AND_TONE.md` (new "Narration voice: grounded and specific" subsection),
  referenced by the three prompt sites (dialogue 3.7, resolver outcomeText, background text 4.3).
- Content: name people and places instead of "the world/word/story"; attribute rumors to a carrier
  with a mundane motive; reactions flow from wants (fear of the census, a debt, a sick mule), not
  from cosmic significance; mystique is a *character trait* some NPCs have, not the default
  register; empire text stays procedural (that part already works).
- Accept: doc merged; each prompt site cites it in a code comment.

### 4.2 Rewrite the canned engine-string tables (including mock/test lines)
- Files: `src/Sorcerer.Core/World/WorldReactionSystem.cs` ("Word of your wild magic takes on a
  sharper color", "The killing will travel farther than the body", "The story of stolen eyes starts
  looking for listeners"), `RumorSystem.cs` deed templates ("stole a body's certainty"),
  `InteractionSystem.cs` (~lines 1930–1940: "Hollowmere will remember the color of your magic",
  "I have heard the shape of you"), `WildMagicController.cs` fallbacks ("The spell refuses to
  become real", "small blue spark" — the spark can stay, it's concrete).
- Also the **mock provider canned lines** (per decision 3, so they don't seed bad examples):
  `DialogueProviders.cs` `MockDialogueProvider` ("It remembers favors longer than laws", the
  Hollowmere/Nannerl whispers) and `DialogueRouters.cs`/mock dialogue strings. These are test-path
  only, but they read as the reference voice — bring them in line with the spec.
- Change: replace with observational, specific strings, parameterized by who saw it and where:
  e.g. "Someone saw the magic, but not the hand that loosed it" → keep (it's already concrete);
  "Word of your wild magic takes on a sharper color" → "The dockhands who saw the fire are telling
  it in the taproom tonight." Where a witness entity exists, use its name; where none does, use the
  place. Several strings already take `deed`/witness parameters — use them.
- Accept: grep for the old strings returns nothing; a live playtest transcript reads grounded; the
  FEEL_NOTES reviewer pass signs off.

### 4.3 Background text generator: same spec, distortion with a carrier
- Files: `BackgroundTextGenerators.cs` (`BackgroundTextPrompt.SystemPrompt`).
- Change: add the voice spec line; for `rumor_distortion`, require the retelling to sound like a
  specific kind of teller (drover, clerk, ferry passenger) rather than omniscient narration.
- Accept: sampled distortions read like overheard talk, not prophecy.

### 4.4 Resolver `outcomeText` register
- Files: `OllamaSpellProvider.BuildCorePrompt`.
- Change: one sentence — outcomeText describes what is physically seen/heard/smelled in the room,
  in the region's voice; no fate-speak, no "the world remembers." (Wild magic itself may be
  ecstatic and strange — that's imagery, not vagueness.)
- Accept: 20 sampled outcomeTexts contain concrete sensory detail and zero
  world-remembers/fate-whispers constructions.

---

## Sequencing

1. **WS0** (both tasks) — cheap, unlocks proof for everything else.
2. **2.1** (num_ctx unification) — one-line-per-site fix, kills the truncation bug and runner
   thrash immediately.
3. **1.1 → 2.2 → 2.3** — the flexibility floor, then the two big context cuts (2.2 depends on 1.1).
4. **3.1 → 3.3 → 3.4 → 3.5** — dialogue payload cuts + continuity.
5. **1.4, 1.5, 1.6, 2.4–2.7, 3.2, 3.6, 3.7** — independent, any order.
6. **1.2, 1.3** — after the miner (0.2) has data.
7. **WS4** — independent; 4.1 first, then 4.2–4.4 in parallel.

## Verification

- `SpellEvalHarness` + `AuditReparseHarness` must stay green after every WS1/WS2 task (schema
  validity, repair rates, targeting).
- A scripted live playtest (`--live-playtest`) before/after WS2+WS3 lands, comparing 0.1 telemetry:
  context bytes per slice, prompt_eval tokens, load_duration, p50/p95 per purpose.
- FEEL_NOTES gets a new dated section after the voice pass and after the flexibility work
  (~20 spells / ~12 dialogue rounds per the existing protocol).

## Resolved decisions (2026-07-05)

1. **Latency budget: < 15 s per action** (visible result), both systems. Reachable per the
   measurements above — wild magic needs the ~2× context cut + prefix cache; dialogue mainly needs
   the reload-thrash fix (2.1). Trimming in 2.2/3.3 is calibrated to hit ≤ 8 KB / ≤ 6 KB, not to
   race to a smaller number at the cost of recall.
2. **One model for all calls, routers always-on.** Uniform options (2.1) keep warm router calls
   ~1 s with no reload. The keyword skip-lanes (2.6, 3.2) and a tiny dedicated router model are
   *experiments to measure only if a session runs over budget*, not baseline work.
3. **Mock/canned lines are in scope for the voice pass** (folded into 4.2).
