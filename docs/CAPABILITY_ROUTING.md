# Capability Routing — Adding a Routing Call to the Wild-Magic Resolver

Status: **implemented** — the LLM router (unioned with keyword routing on every cast), Lever A
(operation-card gating), and Lever B (capability-gated off-screen entity context) have shipped.
Owner: resolver. Companion to [MAGIC_RESOLVER_ARCHITECTURE.md](MAGIC_RESOLVER_ARCHITECTURE.md) and
[LLM_AND_BACKGROUND_JOBS.md](LLM_AND_BACKGROUND_JOBS.md). Ports and updates the WildMagic
prototype's `docs/CAPABILITY_ROUTING.md` for the current C# engine.

## Implemented (2026-07-04)

- **LLM router on every cast, unioned with keyword routing.** `ISpellRouter`
  ([ISpellRouter.cs](../src/Sorcerer.Magic/Capabilities/ISpellRouter.cs)) with Ollama / OpenAI /
  Null implementations, a `SpellRouterFactory`, and a new `LlmPurpose.Router` config route
  (defaults to the resolver's model, `SORCERER_ROUTER_*` overrides, 30s timeout).
  [`CapabilityRegistry.Select(spell, routerNames)`](../src/Sorcerer.Magic/Capabilities/CapabilityCard.cs)
  ranks keyword hits, adds the router's picks as high-confidence seeds, and never lets the cap drop
  an explicit pick. Any router failure/timeout falls back to keyword-only; a cast is never blocked.
- **Lever A — operation-card gating.**
  [`OperationRegistry.ToRoutedIndex`](../src/Sorcerer.Magic/Operations/OperationRegistry.cs) keeps
  full card detail only for the common core, routed-in capabilities, and operations no capability can
  unlock; everything else becomes a lean name+summary card. Measured: the operations block drops from
  ~10.1 KB to ~6.0 KB (simple cast) / ~7.2 KB (heavy 5-capability spell).
- **Lever B — capability-gated off-screen state.** `MagicContext` only includes perceived entities
  by default; entities the caster cannot perceive are added only when a selected capability's
  `RequiredContext` contains `hidden_entities` (currently `memory_edit`). Replaces the old
  always-include-hidden behavior.

Remaining / deferred: dynamic per-cast JSON schema (Lever C2); a dedicated
small router model; richer `RequiredContext` extractors beyond `hidden_entities`.

## Update (2026-07-05, docs/OPTIMIZATION_PLAN.md)

- **Recall floor.** When routing selects **zero** capabilities, `ToRoutedIndex` now advertises
  **every** registered operation by name (lean cards, full cards for the common core) instead of
  core-only. An unanticipated spell can therefore still reach any mechanic. Routed casts keep the
  narrowing above.
- **Smaller always-full core.** `CoreCommon` shrank to six ops (`damage`, `heal`, `addStatus`,
  `removeStatus`, `message`, `createTiles`). Every demoted op has a routed home on a capability
  card, so a spell that needs it loads its full card: `motion_kinetics` (push/pull/teleport),
  `area_burst` (areaDamage/areaStatus), `protection_wards` (resist/weaken), `restoration`
  (restoreMana), `curse_mark` (addCurse), `possession` (possess), and `setFlag` folded into
  `prophecy`.
- **`needs_capability` shipped.** The resolver may answer `{"needsCapability":"name"}`; the
  controller loads that card and re-resolves **once** (`SpellResolutionJson.TryReadNeedsCapability`,
  `SpellProviderResult.RequestedCapability`, `CapabilityRegistry.Find`). Bounded — a second such
  answer is a technical failure.
- Prompt assembly moved to `SpellPromptBuilder` (static prefix first for KV-cache reuse; operation
  cards rendered as compact text, not context JSON). Per-cast latency stats
  (`ProviderCallStats.PromptTokens`, `LoadMs`, …) now ride the audit; mine them with
  `scripts/mine_wild_magic_audit.py`. Note: `SpellRoutingRecord.ContextPayloadBytes` still measures
  the full context object, not the trimmed wire payload — prefer `ProviderStats.PromptTokens`.

## 1. Why now

Latency is roughly linear in prompt length, and the resolver prompt is dominated by content
that does not change with the spell. Measured from `logs/wild_magic_audit.jsonl` (3,932 casts),
the **user-message context JSON averages ~15.9 KB**, and the single largest slice is the
operation catalog:

| Context field | Chars (sample cast) | Share |
|---|---:|---:|
| **operations** (names + full cards) | **10,555** | **64%** |
| visible entities | 1,807 | 11% |
| lore | 1,313 | 8% |
| resolverLens | 1,010 | 6% |
| knownPromises / recentEvents | ~1,090 | 7% |
| terrain | 490 | 3% |
| caster | 153 | 1% |

Across all casts, `operations` alone averages **~58% of the context**, and it is re-serialized
into the *user* message on every call ([EngineViewBuilder.cs:84-114](../src/Sorcerer.Core/Engine/Systems/EngineViewBuilder.cs#L84)).
Two structural facts make this the place to act:

1. **The operation cards are (nearly) static.** They do not depend on world state; the same
   ~27 cards' `summary` / `promptGuidance` / `fields` are sent every cast, including for the
   many core operations a given spell will never use.
2. **Game-state context is ungated.** `visible` iterates **every** entity with a position —
   not just perceived ones ([EngineViewBuilder.cs:47-71](../src/Sorcerer.Core/Engine/Systems/EngineViewBuilder.cs#L47)) —
   so it includes `hidden_from_player` entities the caster can neither see nor (usually) act on.
   Context grows with the world whether or not the spell needs it to.

As we keep adding *kinds* of magic (off-screen memory editing, weather, time, reputation,
structure manipulation), stuffing every capability and every state slice into every prompt
taxes the 95% of casts that never touch them — and degrades a small model's fidelity on the
5% that do (fewer, relevant options measurably improve decisions).

The goal: **let the resolver address a growing set of capabilities while each cast sees only the
mechanics — and only the game state — that the typed spell actually needs.**

### 1.1 The motivating example: off-screen memory edit

> "Plant a memory in the mind of the baron, three towns north."

This should be *possible* — but the `edit_memory`-of-an-absent-target capability should not be
advertised on every fireball, and the memory/identity context of off-screen NPCs should not be
serialized into every cast. Keyword triggers alone route this poorly (the phrasing that names an
absent target is open-ended), and the state it needs (a specific off-screen NPC's memory summary)
is exactly the kind of slice we do *not* want sent unconditionally. This case motivates both
halves of the plan below: **routing mechanics** and **routing state**.

## 2. What the prototype did (and what it deferred)

The WildMagic design chose a **capability-card architecture** gated by a **cheap, tiered
selector**, plus **dynamic schema tightening** so an un-loaded capability is structurally
un-emittable. Faithfully summarized:

- **Adopted & shipped:** capability cards + **Tier-1 keyword routing** (`select_cards`), with a
  lean always-on core prompt + a one-line-per-card index, and a recall-biased dynamic cap.
- **Designed but deferred / "measured into existence":**
  - **Tier-2 embedding routing** — add only if audit logs show keyword misses on real paraphrases.
  - **Dynamic per-cast JSON-schema enums** — narrow the effect-type enum to core + selected cards
    so unselected capabilities can't be emitted; `needs_capability` stays a *global* enum (escape hatch).
  - **Card-driven state retrieval** via each card's `required_context` — inject only the state
    slices the selected cards ask for.
- **Explicitly rejected for the resolver path:** MCP / agentic multi-turn tool-calling (no
  round-trip needed; the "capabilities" are output schemas the engine already executes).

Crucial nuance on a **generative router**: the prototype measured a *short* generative routing
pass (name the spell + return a JSON array of card names, `think:false`) at **~0.25s warm** on
`qwen3.5:9b` — cheap enough to be an **affordable fallback tier for ambiguous spells**, but
deliberately **never the default** when a free keyword scan suffices.

## 3. Where Sorcerer is today

The C# port carried over the front of that design and left the high-value back of it dormant:

- ✅ **Tier-1 keyword routing is live.** [`CapabilityRegistry.Select`](../src/Sorcerer.Magic/Capabilities/CapabilityCard.cs#L47)
  does trigger-hit ranking + one-hop `CommonCombos` expansion + a recall-biased dynamic cap
  (base 3/5, +1 on connectives, ceiling 7).
- ✅ **Core vs. narrowed operations exist** — [`OperationRegistry.ToNarrowedIndex`](../src/Sorcerer.Magic/Operations/OperationRegistry.cs#L58)
  advertises all core ops plus the selected cards' effect types.
- ⚠️ **But narrowing doesn't reach the cards.** All core operation *cards* (guidance + fields +
  examples) are still serialized every cast — the 58% above — and the operation **names** are sent
  twice (system-prompt `supported` **and** context `operations.names`).
- ⚠️ **`RequiredContext` is a dead field.** [`CapabilityCard.RequiredContext`](../src/Sorcerer.Magic/Capabilities/CapabilityCard.cs#L8)
  exists on the record but is **read nowhere** — state assembly in `MagicContext` is identical for
  every spell.
- ❌ **No dynamic schema.** The provider passes `format:"json"` / free JSON, not a per-cast schema
  ([OllamaSpellProvider.cs:50-71](../src/Sorcerer.Llm/OllamaSpellProvider.cs#L50)); nothing makes an
  unselected effect un-emittable.
- ❌ **No `needs_capability` escape hatch** and **no generative router.**
- ❌ **`visible` is unfiltered** (§1, point 2).

So "are we passing every capability every time?" — the full capability *cards* are already
routed (only selected ones' prompt blocks are sent), but the capability **index** (one line per
card) is broadcast every cast, and — more importantly — the **operation catalog and the game
state are not routed at all.**

## 4. Plan

Three levers, ordered by payoff-to-risk. Levers A and B are non-generative and capture most of
the latency win; Lever C is the "routing call" proper, added as a tier for the cases the cheap
router can't handle (the §1.1 example).

### Lever A — Route the operation cards (biggest, quality-neutral)

The narrowing in `ToNarrowedIndex` already knows which operations this cast can use. Extend it
from **names** to **cards**, and stop paying for the rest:

1. **Send full cards only for the routed set** (selected cards' effect types + a small always-on
   core), and **name + one-line summary** for everything else. The engine still validates and
   applies against the *full* registry regardless of what was advertised, exactly as today — this
   only shapes the prompt (see the `ToNarrowedIndex` contract comment).
2. **Deduplicate names.** Drop `operations.names` from the context JSON (the system prompt's
   `supported` already lists them), or vice-versa. Free.
3. **Drop `aliases` from the model-facing view.** Aliases exist for engine canonicalization
   ([WildMagicController.Normalize](../src/Sorcerer.Magic/WildMagicController.cs#L428)); the model
   should emit the canonical `type` and doesn't benefit from seeing them (~0.85 KB, pure noise).

Expected effect: cut the 58% operation slice to a small fraction on typical casts, with the model
seeing *the same or better* signal (full detail only for relevant ops). No behavior change to
validation/apply.

### Lever B — Route the game state (fixes the off-screen bloat)

Wake up `RequiredContext` and make state assembly card-driven:

1. **Filter `visible` to perceived + caster by default** — mirror `View()`'s
   `perception.VisibleEntityIds` filter. Removes `hidden_from_player` entities from the default
   packet and makes the base context bounded by what the caster can see, not by world size.
2. **Add card-driven state slices.** Each selected card's `RequiredContext` keys map to small
   extractors over live state (e.g. `off_screen_target → named-NPC memory/identity summary`,
   `region → region tags`, `nearby_structures → …`). Only selected cards add their slices. This is
   the symmetric half of routing: retrieve the right *state*, not just the right *mechanics*.
3. **Dynamic target enum (optional, later).** When the dynamic schema (Lever C's schema half)
   lands, source its `target` enum from the same packet's real entity ids so the model can't aim
   at a nonexistent foe.

Expected effect: the default context stops growing with off-screen/hidden entities; specialist
state (like an absent NPC's memory) is present *only* when a card asked for it.

### Lever C — The routing call (generative tier + dynamic schema)

This is the "router call" proper. Two sub-parts, both opt-in and measured:

**C1. A generative router tier for ambiguous / absent-target spells.** Keep Tier-1 keyword routing
as the free default; add a **generative fallback** that fires only when keyword routing is thin or
when a spell shows absent-target / wider-world intent that triggers can't reliably catch (§1.1). It
is a *short* call — system: "You are a wild-magic router; here is the capability index; return a
JSON array of the card names this spell needs"; user: the spell text (no world dump). Per the
prototype's measurement this is ~0.25s warm and returns a handful of tokens, so it is affordable as
a *tier*, not a per-cast tax. Wire it as its own purpose-scoped model route
([LlmConfiguration](../src/Sorcerer.Llm/Configuration/LlmConfiguration.cs) — add an `LlmPurpose.Router`
alongside `Wild`, defaulting to the same local model) so it can be tuned or disabled independently.

**C2. Dynamic per-cast JSON schema.** Feed the routed effect types into a per-cast schema (Ollama
`format` object / OpenAI structured outputs) whose effect-type enum is `core + selected`. Unselected
capabilities become **structurally un-emittable** — routing decides what the model *sees*, the grammar
decides what it can *say*. Keep `needs_capability` as a **global** enum (all card names) so the model
can flag "the tool I needed wasn't loaded" — a precise signal to widen triggers or add a card.

The routing call and the dynamic schema share one selection set, so build them together.

## 5. Non-negotiables (unchanged by any of this)

- **The engine is the source of truth.** Routing and schemas keep the model on rails; they never
  replace the post-generation pipeline. Every field still flows through
  `parse → Normalize → ValidateResolution → transactional apply/clamp`
  ([WildMagicController.ApplyResolved](../src/Sorcerer.Magic/WildMagicController.cs#L96)). A narrowed
  schema that still admits nonsense is fine — the engine clamps/rejects it as it does today.
- **Routing is recall-biased.** Under-selection (missing the one card a spell needs) is far worse
  than over-selection (a few ignored tokens). Bias toward including; keep the always-on core broad
  so a total routing miss still degrades to a plausible generic resolution.
- **Discovery-time cost, not hot-path.** If/when the spellbook lands, a learned spell caches its
  routed cards/effects and recasts skip the resolver *and* the router entirely — so capability
  growth never taxes the recast path.

## 6. Migration (each step independently shippable)

Ordered so observability and safety nets exist before anything narrows:

1. **Lever A + name/alias dedup.** Pure prompt-shaping; guard with a test that core + selected cards
   still cover every effect type a spell needs, and an audit-size assertion. *Biggest win, lowest risk.*
2. **Lever B step 1** (filter `visible` to perceived). Small, self-contained; playtest that resolver
   quality holds without off-screen entities in the default packet.
3. **Lever B step 2** (`RequiredContext` extractors) for the first specialist that needs it — the
   off-screen memory-edit card is the natural pilot.
4. **Log routing decisions.** Record `selected_cards`, `router_tier`, and (later) `needs_capability`
   into `wild_magic_audit.jsonl` next to the prompt, so a routing miss is distinguishable from a
   model error *before* gating tightens.
5. **Lever C2** (dynamic schema enums) in shadow mode against the offline audit before enforcing.
6. **Lever C1** (generative router tier) — only if the logs show keyword routing missing real
   paraphrases / absent-target intent. Add the `LlmPurpose.Router` route then.

## 7. Testing

- **Router unit tests** (extend the WildMagic-style table): `spell → expected card set`, including
  **negatives** (a plain fireball must *not* load `memory_edit` or pull off-screen state).
- **Multi-card composition:** compositional spells select the full set ("a wall of fire that makes
  them forget I was here" → terrain + area-damage core + memory-edit).
- **Assembly test:** routed cards → the context carries full cards for exactly core + their effect
  types, name+summary for the rest, and `operations.names` is not duplicated.
- **State-gating test:** the default packet excludes `hidden_from_player` entities; a card's
  `RequiredContext` slice appears iff that card is selected.
- **Refactor guard:** "all cards loaded / no gating" assembly equals the legacy prompt.
- **Shadow comparison:** routed/narrowed output vs. full-context output on the eval corpus
  ([SpellEvalHarness](../src/Sorcerer.Cli/SpellEvalHarness.cs)) — quantify the fidelity delta before enforcing.
- **Latency metric:** log `tokens_in` / context bytes per cast; the whole point is that this number
  drops for typical spells and stops scaling with world size.

---

### One-line summary

Route **both** the mechanics and the game state: extend the existing keyword selector so it gates
the *operation cards* (not just their names) and — via the dormant `RequiredContext` field — the
*state slices* a cast needs, filter off-screen entities out of the default packet, and add a short
generative **router call** as an opt-in tier (with a dynamic per-cast schema) for ambiguous or
absent-target spells like off-screen memory editing — while every output still flows through the
engine's normalize→validate→clamp pipeline, which remains the source of truth.
