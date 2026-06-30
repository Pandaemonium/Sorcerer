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
failure without consuming the turn.

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

- caster profile and controlled body
- visible entities
- selected target
- nearby terrain
- spell anchors
- inventory and reagents
- protected inventory
- active curses
- promises and relevant world notes
- supported operations for this cast
- optional cast performance, once GUI minigames exist

Do not dump all state into every prompt. Use routed context once the operation surface grows.

The CLI should pass neutral cast performance. See
[CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md).

## Mock Provider

Mock provider exists so agents and tests can exercise the engine quickly. It should:

- be deterministic
- cover common spell families
- return valid contract JSON
- intentionally produce a few rejection/technical test cases

It is not the design target. Real play is designed around local LLM interpretation.
