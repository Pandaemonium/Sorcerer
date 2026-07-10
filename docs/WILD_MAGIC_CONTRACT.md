# Wild Magic Contract

Wild magic turns player text into validated engine operations.

The LLM proposes. The engine decides.

## Turn Contract

- Technical failures do not consume a turn.
- Intentional rejections consume a turn.
- Successful accepted spells consume a turn.
- Malformed LLM output must not partially mutate state.
- Failed effect application must roll back the whole spell.
- Costs are normally revealed after casting.
- Outcome text, fallback sparks, technical failure notices, and rejection reasons are
  player-visible `message` consequences with wild-magic-specific operations.

Technical failures include:

- provider timeout
- invalid JSON
- unsupported effect type
- invalid target reference
- invalid cost
- unavailable item cost
- schema-shaped but mechanically inert effects
- application exception

Intentional rejections include:

- spell too broad
- spell too overpowered
- spell impossible under active curse limits
- spell rejected by the model or engine as a magical impossibility

Design bias: overpowered spells should usually happen with severe cost rather than be
rejected outright. Literal win buttons, infinite resources, impossible global rewrites, and
contradictions can still reject.

Early balance calibration (owner decision, 2026-07-09): err on the side of permissiveness. When a
spell can be represented by the validated operation/consequence grammar and applied coherently,
allow it even if it is dramatic, durable, or probably unbalanced. Engine authority, protected-item
consent, transactional application, and state invariants remain firm; power ceilings and stricter
balance rules wait for concrete playtest evidence. Durable body, control, identity, relationship,
and world changes are not rejected merely because they would be severe.

## Top-Level Shape

Initial response shape:

```json
{
  "accepted": true,
  "severity": "minor",
  "outcomeText": "The spell answers in a brief flash of blue glass.",
  "effects": [],
  "costs": [],
  "rejectedReason": null
}
```

If `accepted` is false:

```json
{
  "accepted": false,
  "severity": "major",
  "outcomeText": "",
  "effects": [],
  "costs": [],
  "rejectedReason": "The spell is too vast to fit through one body."
}
```

## Initial Effect Surface

Start smaller than Wild Magic, but keep the operation model broad enough to grow.

Recommended first effects:

- `damage`
- `areaDamage`
- `heal`
- `restoreMana`
- `push`
- `pull`
- `teleport`
- `createTile`
- `createTiles`
- `addStatus`
- `removeStatus`
- `summon`
- `createEntity`
- `transformEntity`
- `transformItem`
- `changeFaction`
- `addTrait`
- `createPromise`
- `scheduleEvent`
- `createTrigger`
- `addCurse`
- `message`
- `consequence`

`scheduleEvent` is for broad future world events. `createTrigger` is for tactical turn-pump effects:
delays, aura pulses, and ward-shaped records. It can use shorthand embedded effects (`addStatus`,
`damage`, `heal`, `message`) or `effectType: consequence` with `consequenceType` plus payload
fields when the delayed trigger should deliver another typed world consequence.
For `scheduleEvent`, use `text` or `description` for a delayed message, or provide
`consequenceType` when the due event should deliver a typed consequence. Do not emit a
generic consequence-shaped scheduled payload without `consequenceType`; the engine treats that as
malformed hidden content rather than delayed narration.
For delayed typed consequences from triggers, persistent hooks, or scheduled events, nested payload
fields win on conflicts and top-level typed fields fill missing payload values.

`consequence` is the generic bridge into the shared world-consequence grammar. Use it
when wild magic needs a local effect that is already owned by a typed consequence handler, such as
`add_tags`, `update_want`, `record_memory`, `offer_service`, `request_service`, or `message`, without adding a new
spell-only operation. It accepts `consequenceType`, an optional `target` or `targetEntityId`,
`consequencePayload`, and `timing`. `immediate` is the default; `after_turn`, `world_pump`, and
`deferred` schedule the same
typed consequence through the shared scheduled-event pump. Keep using `scheduleEvent` for broader
future world events and `createTrigger` for repeating, anchored, aura, ward, or pulse-shaped
effects.
When a nested `consequencePayload` exists, it wins on explicit fields, but top-level typed fields
such as `status`, `duration`, `amount`, `resource`, or `tags` fill missing payload values through
the shared world-consequence payload merge helper. This matches generated dialogue's generic
consequence repair lane and keeps provider output tolerant without giving up engine validation.
The engine canonicalizes common source spellings such as `addTags`, `requestService`,
or hyphenated ids to snake_case at the world-consequence applier boundary, but
prompts should still prefer the canonical ids for clarity.

`transformEntity` / `transformItem` are the general prop-and-body alteration lane. They can rename,
rematerialize, add/remove tags, change descriptions, alter `blocksMovement` / `blocksSight`, set
glyph/palette, retag `fixtureType`, and add interactable verbs. A bridge collapsing, a shrine
becoming climbable, or a sign becoming a door should use this general transformation payload or
`consequenceType: transform_entity`, not a one-off spell operation.

`addCurse` may be semantic or mechanical. Mechanical curses should name a template (`close`, `far`,
`narrow`, `straight-path`, `anchored`) so the engine can reject later accepted resolutions that break
that limit before mutation.

Resolver context may include one distilled `lore` card only when a selected capability requests
regional/canon context. Lore is voice and canon guidance only. It does not mutate state or create mechanics
unless the resolver emits supported operations that validate normally.

Cost objects are normalized before validation. Canonical shapes are still preferred, e.g.
`{"type":"mana","amount":4}` or `{"type":"item","item":"grave salt","quantity":1}`, but the
parser also repairs common live-model shapes such as `{"name":"mana","amount":4}` and
`{"name":"charcoal wand","quantity":1}` into supported cost types.

Recommended first costs:

- `mana`
- `health`
- `maxHealth`
- `maxMana`
- `item`
- `status`
- `curse`

Later cost families may include reputation exposure, faction attention, promise debt,
memory, and body change. Body changes are extremely severe and should be rare.

Add operations only when they unlock a family of spell prompts.

## References

The model should refer to world things through normalized references.

Examples:

```json
{"kind":"entity","id":"entity_12"}
{"kind":"tile","x":12,"y":8}
{"selector":"player"}
{"selector":"nearest_enemy"}
{"selector":"selected_target"}
{"selector":"all_enemies"}
```

Legacy strings may be accepted by the parser, but the engine should bind references
through one authoritative ref system.

## Parsing And Repair

Local models often return useful but misshaped JSON. The parser should repair common
mistakes when the intent is clear:

- `effect` to `effects`
- `cost` to `costs`
- nested `details` lifted into effect fields
- wrapper objects such as `result` or `output`
- target aliases such as `nearest foe` to `nearest_enemy`
- flavor statuses such as `petrified` to mechanical `frozen` with display name preserved
- cost dictionaries such as `{ "mana": 3 }`
- unsupported effect aliases to canonical names

Repair should not invent major semantics. If repair cannot clearly map output to engine
operations, treat it as technical failure.

## Validation

Validation should check:

- top-level shape
- supported effects and costs
- effect count and cost count limits
- required fields
- target/ref validity
- cost availability
- protected inventory constraints
- status names
- tile names
- summon/entity bounds
- active curse limits
- state consistency after application

Validation is not just schema validation. Schema can prove shape. The engine proves sense.

## Transaction

Spell application should be transactional:

1. validate resolution
2. preflight costs
3. snapshot or open transaction
4. apply effects
5. apply costs
6. collect state deltas
7. advance turn
8. validate state
9. commit

If any application step fails, roll back to the pre-cast state and report a technical
failure without consuming the turn. This includes accepted resolutions whose operation, cost, or
deed children return `worldConsequenceRejected`: the cast keeps hidden rejection diagnostics for
agents/audits, but no earlier effect from the same cast remains committed.

Full engine-side spell-budget pricing is deferred. Early builds should validate costs and
targets, but let the model propose most cost magnitude. Add engine auto-pricing only after
playtesting shows what good magic costs should feel like.

## Operation Deltas

Each meaningful mutation should optionally produce a `StateDelta`:

```json
{
  "op": "damage",
  "target": "entity_12",
  "summary": "glass moth takes 4 fire damage",
  "details": {}
}
```

Deltas are useful for:

- animation
- audit logs
- tests
- agent understanding
- replay diagnostics

The GUI can later use deltas as an animation/event feed without making renderer code
authoritative.

## Prompt Context

The resolver should receive compact state views:

- compact caster resources and statuses
- a bounded roster of important target entities
- terrain only for spatial capabilities
- selected target
- spell anchors/scenery only for object-aware capabilities
- at most four unprotected reagents
- promises only for promise-aware capabilities
- supported operations for this cast

Do not dump all state into every prompt. Recent event history, protected inventory, unrelated lore,
unrelated scenery, and unrelated promises do not belong on the resolver wire.
Player perception is not the resolver's hard knowledge boundary: hidden or debug-only facts may be
included when useful for coherent magic, but they must be labeled as hidden from the player, and
renderers must not reveal them until the engine makes them observable. Wild magic is not limited by
what the player knows; a hidden target is not invalid merely because it is hidden. The engine
remains authoritative about references, range, costs, curse limits, transaction safety, and whether
a proposed operation can actually be applied.

Cast performance never enters the prompt: the minigame plays while the provider thinks,
so the score does not exist when the prompt is sent. The engine applies performance after
the provider returns. See [CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md).

## Mock Provider

Mock provider exists so agents and tests can exercise the engine quickly. It should:

- be deterministic
- cover common spell families
- return valid contract JSON
- intentionally produce a few rejection/technical test cases

It is not the design target. Real play is designed around local LLM interpretation.
