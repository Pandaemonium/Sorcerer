# Magic Resolver Architecture

Wild magic is the crown jewel of Sorcerer. The resolver must make typed spell ideas feel
surprising, powerful, and mechanically real without letting the model own game truth.

## Core Rule

The model proposes a composition of engine operations. The engine normalizes, validates,
prices where supported, applies transactionally, and records the result.

```text
spell text
  -> cast context view
  -> provider
  -> raw JSON
  -> parse and repair
  -> normalize
  -> materialized magic resolution
  -> re-parse at apply point
  -> validate
  -> apply operations transactionally
  -> action result, deltas, audit
```

`IWildMagicController.ResolveAsync` owns the provider-facing half of this pipeline. It may call a
live or mock model, but it must not mutate `GameState`; its output is a
`MaterializedMagicResolution` containing provider metadata, raw text, accepted/error flags, effect
types, and normalized `resolvedMagicJson`. `ApplyResolved` owns the authoritative half. It rebuilds
the current engine context, re-parses the materialized JSON, validates against the present state,
and only then applies mutations transactionally. `CastAsync` remains the ordinary convenience path:
resolve, then immediately apply.

Pending casts use the same split. `begin_cast` starts `ResolveAsync` in the background and may save
the materialized result before any turn is consumed; `await_cast` is the explicit apply point.
`cancel_cast` cancels the pending provider token instead of merely hiding the pending record.

## Primary Failure Modes

The two biggest resolver failures are:

- bad LLM outputs
- boring magic

The architecture should make both visible. Every live cast should be auditable. Spell
evaluation should measure not only schema validity, but also whether resolutions are
specific, local, consequential, and interesting.

## Capability Cards

Sorcerer should preserve and improve the capability-card approach.

A capability card describes one family of magical expression:

- name
- triggers or routing text
- effect types it unlocks
- required context slices
- prompt block
- examples
- version
- audit tags

The resolver should receive:

- a compact always-on core
- a one-line index of available capabilities
- full detail for selected cards only
- a narrowed schema or operation list when practical
- a `resolverLens` block derived from body/soul stats and magical signature

Routing should be recall-biased. Loading one extra relevant card is much less harmful than
missing the card that would make a spell work.

Current implementation:

- `CapabilityRegistry.Select` ranks cards by trigger-hit count (ties broken by registry order),
  expands one hop via each card's `CommonCombos`, and applies a dynamic cap (5 with any hits else
  3, +1 for a compositional connective like `" and "`, hard ceiling 7) instead of a flat cap.
- Cards load from `content/capabilities/*.json` via `CapabilityCardLoader` (same shape/loader
  pattern as `OperationCardLoader` for `content/operations`), with an in-code fallback set that
  covers the core card families (terrain_shape, summoning, transformation, prophecy, conjure_item,
  memory_edit, faction_charm, delayed_effects, triggers_reactions, persistent_effect,
  behavior_control, environment_flow). Content cards can add newer routed families such as
  `fixture_manifestation`, which unlocks `conjureFixture` for discrete shrines, markers,
  landmarks, hazards, and props.
- `WildMagicController.ResolveAsync` calls `Select` per cast and builds a narrowed `OperationIndex`
  (core operations, per `IOperation.IsCore`, plus the effect types the routed cards unlock) instead
  of always advertising the full registry; `OllamaSpellProvider` assembles the core prompt, the
  always-on one-line capability index, and only the routed cards' detail blocks/examples.
- `WildMagicController.ApplyResolved` re-parses the materialized resolution and validates it against
  the current state. A materialized spell can therefore be applied after a save/load or by replay
  without trusting stale provider context as state authority.
- Narrowing only shapes what is advertised. `OperationRegistry.Resolve`/`Supports` still validate
  against the full registry regardless of what a given cast's prompt/context exposed; Sorcerer does
  not hard-enforce a per-cast schema enum, matching Wild Magic's own choice to defer that.

## Resolver Lens

`MagicContextView.ResolverLens` is the current character-to-magic bridge. It is soft
guidance, not a second spell engine:

- body Vigor frames whether physical costs are plausible
- soul Attunement nudges effect magnitude up/down
- soul Composure frames volatility, backfires, and messy flourishes
- soul magical signature supplies a recurring idiom
- current region identity and affordances provide local voice and reusable environmental hooks

Middle-band stats leave anchors unchanged. The live provider prompt tells the model to use
the lens as soft guidance, and the mock provider reads the same block for deterministic
offline differences. This keeps stats meaningful without making flavor bypass validation:
the model still proposes operations and the engine still validates/applies them.

Region notes are also soft guidance. A reed bed, marble blind spot, or loose-reality border only
matters mechanically when the provider proposes ordinary engine operations such as `addStatus`,
`createTile`, `addTrait`, `summon`, `createTrigger`, or `message`, and those operations validate.

`MagicContextView.Lore` is the current canon-to-resolver bridge. `LoreCatalog` loads Markdown cards
from `content/lore/*.md` (with built-in fallbacks), and `LoreRouter` selects relevant cards by
region, entity tags, affordances, promises, and recent trigger words. Only access-gated live
sections are injected; draft cards/sections are excluded. The provider may use these cards for
canon and voice, but lore still becomes mechanically true only when the model proposes supported
operations that pass validation.

## Operation Registry

Operations should be first-class registered engine verbs.

Each operation should eventually own or point to:

- canonical name
- aliases
- JSON shape
- validator
- applier
- cost/pricing metadata, once pricing exists
- state delta shape
- prompt guidance
- examples
- required state context

This keeps magic extensible. Adding a new mechanic should mean adding one disciplined verb,
not editing scattered prompt strings, validators, docs, and handlers by memory.

Current implementation:

- C# operation classes own validation and application.
- `OperationRegistry.CreateDefault()` registers the code-owned operation set.
- `OperationCardLoader` searches upward from the current/app base directory for
  `content/operations` and attaches any matching data cards.
- Built-in operation cards remain the fallback when content files are absent.
- Data aliases from cards are exposed to provider context and can supplement resolver
  aliases, but exact operation names stay authoritative.
- Cost parsing has a similar repair lane for common live-model shapes. A cost object with
  `name: "mana"` becomes a mana cost, while a named carried material such as `name: "charcoal wand"`
  becomes an item cost before validation. Unsupported cost families still reject normally.
- `consequence` is the generic operation for already-modeled world-consequence effects.
  It carries `consequenceType`, optional `target` / `targetEntityId`, provenance/visibility fields,
  `timing`, and a typed `consequencePayload`, then submits one `WorldConsequence` through
  `GameEngine.ApplyConsequence`. This is how wild magic reaches social/world handlers such as
  tags, memory, wants, services (`offer_service` to create an affordance, `request_service` to
  perform one), routes, canon, rumors, or messages without growing a spell-only helper.
  The shared consequence applier canonicalizes common spellings like `addTags`,
  `requestService`, and hyphenated ids to snake_case before dispatch, so model
  spelling repair stays at the engine boundary rather than in each source adapter.
  The operation also reads common nested payload aliases (`payload.world_consequence_type`,
  `payload.target_id`, `payload.consequence_timing`, and visibility aliases), matching the
  dialogue consequence path's tolerance for model-shaped JSON.
  The shared world-consequence payload merge helper makes nested payload fields win on conflicts,
  while top-level typed fields fill missing payload values, so
  `{"type":"consequence","consequenceType":"apply_status","targetEntityId":"prisoner_1","status":"blue-marked","consequencePayload":{"operation":"spellMark"}}`
  preserves both the operation metadata and the status fields.
  `immediate` timing applies now; `after_turn`, `world_pump`, and `deferred` schedule the same
  typed consequence through the shared scheduled-event pump. Use `scheduleEvent`, `createTrigger`,
  persistent effects, or another explicit deferred primitive only when the desired shape is a broad
  future event, repeating trigger, aura, ward, or hook rather than one delayed consequence.
  Narrative guardrails inspect the inner `consequenceType` too, so outcome text about binding a
  promise, opening a door, revealing a route, or changing inventory can be backed by
  `create_promise`, `open_or_unlock`, `create_route`, `modify_inventory`, and other typed
  consequences without adding a spell-only operation.
- `createTrigger` is the canonical operation for delayed tactical effects, auras, and
  ward-shaped pulses. It writes `TriggerLedger` records with an anchor, radius, target filter,
  cadence, use count, and one embedded effect. The shorthand embedded effect set remains
  `addStatus`, `damage`, `heal`, and `message`, and the broader path is `effectType:
  consequence` with a `consequenceType` plus typed payload fields. That generic path lets a
  delayed ward submit the same consequence grammar used by dialogue, promises, services, and
  scheduled events.
  Trigger advancement, completion, and expiry now route through the shared `update_trigger`
  consequence. When a trigger fires, its embedded effect fan-out and lifecycle update are staged as
  one transaction; explicit target ids stay binding, so a vanished named target fizzles instead of
  being retargeted to the player. Rejected generic trigger payloads roll back, report the
  underlying rejection, and expire the bad trigger.
- High-use operation families now apply by submitting typed `WorldConsequence` records through
  `GameEngine.ApplyConsequence`: damage/heal/mana, movement, terrain creation/lifecycle, status, summon/item spawn,
  promises, messages, inventory changes, tags/traits, faction allegiance, world flags, scheduled
  event creation/lifecycle, trigger creation/lifecycle, transformations, resistance/weakness, delayed incoming damage, status
  acceleration, memory edits, persistent effect creation/lifecycle, behavior tag creation/lifecycle, tile flows, faction
  standing/resource adjustments, legend tags, and canon records. `IOperation` remains the
  resolver-facing validation layer; the authoritative mutation lifecycle is converging on the
  shared consequence applier.
- The transformation family is broad enough for prop and fixture retuning: `transformEntity`,
  `transformItem`, and generic `consequenceType: transform_entity` can change material, tags,
  description, passability/sight blocking, glyph/palette, fixture type, and interactable verbs.
  This is the general route for bridge-collapse, shrine-repair, sign-becomes-door, and similar
  local object changes.
- `createPersistentEffect` is a separate combat-hook primitive, not a hidden recursive spell
  engine. It stores a deliberately small shorthand effect-kind set (`damage`, `heal`,
  `addStatus`, `message`), a generic `effectType: consequence` payload with `consequenceType`, or
  a resolved sympathetic link. Unsupported embedded effects reject before mutation; legacy
  unsupported records surface an explicit failure delta and then follow the normal use-consumption
  lifecycle instead of disappearing silently. Hook firing collects the full child consequence delta
  set before submitting `update_persistent_effect`, so combat hooks remain compatible with compound
  consequence handlers. Rejected generic payloads roll back the fired child and remove the malformed
  persistent record.
- HP loss should flow through the shared combat damage path. Ordinary attacks, spell damage,
  delayed damage releases (`release_delayed_damage`), terrain/status harm, resistance/weakness,
  and delay buffers should not grow separate arithmetic rules.
- `createFlow` writes zone-local tile-flow fields; expiry/removal routes through `update_flow`.
  They travel with zone snapshots like terrain and terrain expirations, so environmental fields do
  not leak between places or vanish on return.

This means prompt guidance can improve without creating a second source of truth for
mechanics.

## Live Output Repair

Live local models often return semantically useful JSON in a dialect that is not quite the
schema. The parser should repair broad, reusable shape mistakes before validation, while
the engine still decides legality.

Current repairs include:

- operation keys: `type`, `operation`, `op`, `kind`, `effectType`, `effect_type`, and
  legacy `name` when no stronger operation key exists.
- keyed effect objects such as `{ "addStatus": { ... } }` are flattened into ordinary
  `{ "type": "addStatus", ... }` effects when they are otherwise valid JSON.
- target aliases: `targetId` and `target_id` become `target`.
- summon aliases: `entityName` and `entity_name` become `name`.
- compact effect ids such as `status_webbed_target_id:soldier_1...` and
  `message_text:...`.
- nested `details` or `data` fields folded into the effect when they provide names or
  descriptions.
- missing operation type inferred from clear fields: `target + trait(s)` becomes `addTrait`,
  `status` becomes `addStatus`, `text`/`message` becomes `message`, and terrain coordinates
  become `createTile`.
- coordinate shorthand such as `{ "target": { "x": [4], "y": [5] } }` and
  `"target/x/y": [[4,5], ...]`.
- array-valued traits, so `trait: ["friendly"]` does not become `System.Object[]`.
- simple cost strings and numbers such as `"10 mana"` or `10`.

Status ids are canonicalized at the engine boundary. Flavor names that contain known
status aliases, such as `webbed_blue_webbing` or `shadow_pinned`, inherit the registered
mechanics while keeping the model's display phrase for messages. This is a general alias
family, not a spell-specific handler: rooted/binding language such as webbed, bound,
pinned, anchored, tethered, snared, or held suppresses movement/action through
`StatusRegistry`. Concealment language such as hidden, camouflaged, veiled, or
`river_concealed` now canonicalizes to `concealed`; that status uses a registry default duration
when the model omits one and suppresses hostile AI notice beyond close range. Ongoing conditions are
also data-driven: `burning` and `poisoned` deal turn-pump harm, while `regenerating` and aliases such
as `mending` restore HP through the same shared engine cadence.

Terrain can now participate in that cadence. `createTile` / `createTiles` terrain such as
`shallow_water`, `wild_fire`, and `vines` is not just description: `TerrainReactionSystem` can
extinguish burning into `steam_mist`, ignite actors, or apply rooted status when the deterministic
turn pump says the setup applies. The resolver should still express those setups as ordinary terrain
and status operations; it should not invent hidden physics outside the operation list.

`MagicContextView.Reagents` exposes only unprotected carried fuel. Each reagent card includes
quantity, value, material, tags, and `spellBias`; protected/treasured inventory remains visible in
ordinary inventory views but is not listed as available reagent fuel. Providers may use reagent
metadata as soft theming or cost guidance, but the cost validator remains authoritative.

Mechanical curses are promise-backed validation constraints. `addCurse` should include a
`template` / `curseType` when it intends a known mechanical limit (`close`, `far`, `narrow`,
`straight-path`, `anchored`). `MechanicalCurseValidator` then rejects future accepted resolutions
that violate the active curse before any operation mutates state. Curses without a known template
remain semantic promise/debt text. Curse costs use the same `create_promise` consequence lane with
operation `cost:curse`, including stacking metadata, so a debt paid by magic is still an ordinary
promise/debt record for later payoff systems.

Live-provider feel tests should check that prose and mechanics agree. If the model says an
enemy is pinned, asleep, frozen, charmed, burning, or otherwise altered, the applied engine
operations should make that state matter immediately or the audit should reveal why repair
or operation guidance failed. The Ollama prompt now explicitly tells the provider that
`outcomeText` and message effects must describe only what listed operations make true, and
that second person is reserved for the controlled player/caster. Non-player targets should be
named rather than addressed as "you" or "your." It also requires target-taking effects to name a
real target, requires flat effect objects with a `type` field, and tells the provider to target an
owning entity when a spell names appearance, clothing, rope, hair, voice, shadow, or body detail
rather than selecting an unrelated object-like item.

The Ollama provider asks for a larger local context window and makes one repair call when the model
returns non-JSON or malformed resolution content. The repair call is still a provider/schema step:
it may convert prose or keyed/nested effect dialects into the required operation JSON, but it does
not invent engine effects without validation. If the repair also fails, the cast remains a
technical failure and does not consume a turn.

`transformItem` rejects no-op transforms that provide no name, material, or tag change. This keeps
live output from producing misleading "X changes into X" messages.
`modifyInventory` remove-style effects (`consume`, `remove`, `subtract`) also validate available
quantity before application; the shared `modify_inventory` consequence rejects insufficient
consumes at apply time for magic, costs, services, and future systems.

Malformed provider shapes are technical failures, not in-world spell rejections. For
example, an unrecognized target object must not be stringified into a .NET type name and
treated as a missing entity; it should fail visibly, preserve audit evidence, and leave the
turn clock untouched. By contrast, a well-formed but impossible or overreaching spell may
still be rejected in-world and consume the cast turn.

## Engine Pricing

Full spell-budget auto-pricing is deferred.

Early Sorcerer should let the model propose costs and let the engine enforce:

- supported cost types
- target validity
- protected/treasured inventory rules
- impossible references
- hard safety clamps
- transaction rollback
- technical failure turn rules

Later, the engine may score spell power and paid costs. That should wait until the game has
enough feel to know what pricing should mean.

## Strong Spells

Overpowered spells should usually happen with severe costs rather than be rejected outright.

Examples of severe costs:

- major health loss
- max health loss
- max mana loss
- curse
- reputation exposure
- dangerous promise
- item sacrifice
- body change, only as an extremely severe outcome

Literal win buttons, infinite resources, and global rewrites can still be rejected, but the
default instinct should be "yes, at a price."

## Wildness

Wild magic should be unpredictable. Stronger requested spells can be more monkey-paw.

Backfires should generally become most likely when the player tries a powerful spell with
poor casting performance. The resolver contributes only the severity report; the engine
applies performance and decides mishap escalation after the provider returns. See
[CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md).

## Post-Cast Costs

Costs are normally revealed after casting. The resolver and engine should support the
feeling that the player reaches for power before knowing exactly what it will take.

The player may still hint in the spell text that they want to spend a specific resource.
That should remain organic.

## Provider Neutrality

Do not bake the resolver around one provider.

The provider interface should support:

- local model providers
- Ollama
- API-key providers
- mock providers
- future provider experiments

Provider-specific behavior belongs behind the provider interface. Engine rules should not
care where the JSON came from.

## Audits

Every live resolver call should record:

- provider and model
- request purpose
- spell text
- selected capability cards
- context slices
- prompt or compact prompt representation
- raw response
- repaired/normalized JSON
- validation errors
- final action result
- state deltas
- technical failure flag

Audits are not optional polish. They are how the team improves the most important system.

When a spell produces a normalized accepted or intentional-rejection resolution, the action result's
`MagicResolutionRecord` should preserve `resolvedMagicJson` where practical. `ResolveAsync` also
exposes the same materialized JSON before mutation. Transcripts use that materialized JSON to replay
command sequences with `ReplaySpellProvider` instead of re-calling the model. This keeps replay
deterministic without making the model a source of authoritative state.

Player-facing resolver narration is also a consequence. Accepted `outcomeText`, fallback sparks,
intentional rejection reasons, and technical failure notices enter the log through typed
`message` consequences, so audits and transcripts can distinguish `wildMagicOutcome`,
`wildMagicRejected`, and `wildMagicTechnicalFailure` from mechanical effect deltas.
