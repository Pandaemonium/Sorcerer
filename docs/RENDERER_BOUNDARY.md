# Renderer Boundary

Sorcerer starts with ASCII rendering, but the architecture must allow other renderers.

The renderer is a consumer of state, not the owner of state.

## Rule

Renderers read `GameView` and emit player intent as commands.

They do not:

- mutate world state directly
- apply spell effects
- resolve targeting rules
- decide turn costs
- inspect hidden state
- call providers directly for gameplay

Normal renderer views are perception-bound. `GameView.Entities` contains visible entities
plus the controlled body, and `GameView.Tiles` marks each coordinate as visible, explored,
or unknown. Renderers may display explored-but-not-visible memory dimly, but they should
not reveal hidden entity facts from debug observations. CLI debug state and transcripts can
carry perfect diagnostic information for agents and regression tests; that is not a player
knowledge contract.

## Renderer Inputs

Renderers should receive views such as:

- map tiles
- visible/explored flags
- visible entities
- glyph/color/sprite hints
- selected target
- player stats
- inventory/equipment summaries
- current world/region card and affordances
- message log
- pending foreground job label
- developer/debug views

Views should be stable and serializable where practical.

## Renderer Outputs

Renderer input handling should produce `GameCommand` values:

- move
- travel
- wait
- cast
- target
- use item
- open
- inspect
- menu actions that map to commands

Some menus may maintain local cursor state, but committing an in-game action should route
through `GameSession.Execute`.

Mouse targeting should exist early in the Godot renderer. It should still emit target or
inspect commands rather than mutating selection rules directly.

## ASCII First

The first renderer should be ASCII because:

- it matches the prototype
- it is fast to build
- it keeps state legible
- it aligns with CLI testing

Do not bake ASCII assumptions into rules. Store glyphs as render hints, not mechanics.

Animations and visual feedback should prefer engine `StateDelta` records where possible.
The renderer may animate deltas, but it should not infer hidden rule outcomes from them.

## Future Renderers

Future renderers might include:

- tile sprites
- animated ASCII
- zoomed tactical view
- map-only debug view
- accessibility/high-contrast renderer

They should be replaceable without changing engine operations.
