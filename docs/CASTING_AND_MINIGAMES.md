# Casting And Minigames

Sorcerer can eventually use quick casting minigames while the model resolves a wild spell.
This should be designed as an input to the resolver, not as a separate rules path.

## Design Goal

While the provider is thinking, the GUI may ask the player to perform a short action, such
as tracing a rune quickly and accurately.

The result should influence the cast:

- better performance makes the spell more powerful or controlled
- worse performance makes the spell wilder and more unpredictable
- skipping the minigame produces a neutral unmodified cast

The CLI should use the neutral unmodified cast by default. Agents should not need to play
GUI minigames to test the real engine.

## Cast Performance

Represent minigame output as a small engine-facing value object.

Suggested shape:

```csharp
public sealed record CastPerformance(
    bool Played,
    float PowerModifier,
    float ControlModifier,
    float WildnessModifier,
    string Source
);
```

Example sources:

- `neutral`
- `rune_trace`
- `debug_fixed`
- `agent_default`

The exact fields can change. The important rule is that the engine and resolver receive a
compact performance summary, not renderer-specific gesture data.

## Resolver Use

The resolver prompt can receive performance as part of the cast context:

```json
{
  "castPerformance": {
    "played": true,
    "power": 1.2,
    "control": 0.7,
    "wildness": 1.4
  }
}
```

The model can use this to decide severity, outcome text, cost, and backfire flavor. The
engine still validates and applies.

## Engine Use

Early builds may leave performance entirely to the prompt.

Later, the engine can use performance to:

- clamp maximum safe severity
- adjust wildness risk
- apply deterministic bonuses
- decide when extra complications are allowed
- annotate audit records

Avoid making this complex before the core resolver is fun.

## Backfires

Backfires should not be random punishment. They should usually happen when:

- the player asked for a very powerful spell
- casting performance was poor
- active curses or local conditions make the spell unstable
- the spell text itself invites risk

Backfires can be:

- tactical complications
- curses
- item costs
- world promises
- reputation exposure
- delayed consequences
- strange local terrain or entity changes

Body changes are extremely severe and should be rare.

## UX Rule

The player must be able to skip the minigame. Skipping should produce a neutral result, not
a punishment. This keeps accessibility, CLI parity, and agent playtesting intact.

