# Casting And Minigames

Sorcerer uses quick casting minigames while the model resolves a wild spell. The minigame is
an input to the engine's apply step - never a separate rules path, and never a resolver
input. This doc records the settled design. The first minigame, rune tracing, is implemented
(see "First Minigame: Rune Trace" below).

## Design Goal

Provider resolution takes seconds. Instead of a spinner, the GUI offers a short skill game -
the first is tracing a magic rune quickly and accurately - that plays during the wait and
tilts the cast:

- better performance makes the spell more powerful or controlled
- worse performance makes the spell wilder and more unpredictable
- skipping the minigame produces a neutral unmodified cast

Minigames are first a latency mask so players are not simply waiting; they should also be
genuinely enjoyable. They must never become mandatory: skipping is a first-class way to
play.

The CLI uses the neutral unmodified cast by default. Agents should not need to play GUI
minigames to test the real engine.

## Timing Contract

The minigame starts the moment the provider call starts, so the score does not exist when
the prompt is sent. That forces the core rule:

**The resolver never sees casting performance. The engine applies it after the provider
returns.**

Consequences:

- Minigame parameters must be computable locally, with no model call. The spell does not
  shape the minigame; the GUI pulls a random game from the repertoire.
- Target duration is about 10 seconds, but the true deadline is unknown, so games should
  support an indefinite runtime (chained runes, sustained traces) rather than a fixed
  course.
- The game may run one or two seconds past the provider's return to finish cleanly.
- If the provider returns before a minimum scoring window has elapsed, the attempt is
  discarded and treated as neutral. A fast cast never punishes the player for an
  unfinished game.

## Scoring

Score is rate-based - points per second - so a 5-second wait and a 25-second wait are
equally fair, and long latency is never an advantage.

Playing is a fair gamble centered on neutral: an average performance lands at roughly the
neutral cast, good performance above it, poor performance below. On average, playing and
skipping produce the same power level, so skipping is never a punishment and playing well
is never obligatory.

Games may report up to two axes when the mapping stays legible - for rune tracing, speed
feeds power and accuracy feeds control - but many games will report a single axis from
Wild to Strong. `CastPerformance` supports both; a single-axis game maps its one score
across the modifiers.

## Cast Performance

Minigame output is the small engine-facing value object already plumbed through
`Sorcerer.Core` commands:

```csharp
public sealed record CastPerformance(
    bool Played,
    float PowerModifier,
    float ControlModifier,
    float WildnessModifier,
    string Source);
```

Example sources:

- `neutral`
- `rune_trace`
- `debug_fixed`
- `agent_default`

The engine receives a compact performance summary, never renderer-specific gesture data.

## Engine Use

The provider resolves the spell normally and reports severity as it already does. At the
apply boundary the engine combines reported severity with the final performance:

- good performance scales power and control up deterministically
- poor performance raises wildness and unpredictability
- high reported severity plus poor performance can convert the intended outcome into a
  mishap: the engine substitutes or adds a complication instead of applying the spell
  cleanly
- performance and any mishap decision are annotated in the audit record

Because the resolver is not consulted again, mishaps come from engine-side complication
tables. The narrator may flavor a mishap after the fact; narration never blocks the apply.

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

## Input

Minigames assume mouse input for now. Gamepad and touch are out of scope until a real
need appears.

## First Minigame: Rune Trace (implemented)

Wild runes burn into the screen stroke by stroke; the player holds the left mouse button on
the bright start node and traces the searing path. Sealed runes chain indefinitely - a new
sigil burns in until the provider returns - so unknown latency is the game's natural clock.
A rune in hand gets up to two seconds of grace to finish after the provider settles.

The moving parts, and where the boundaries sit:

- `Sorcerer.Godot/Scripts/Minigames/RuneTraceMinigame.cs` owns presentation and raw gesture
  math: procedural runes (`RuneShape`, seeded from the spell text so a cast always burns the
  same sigils), the burn-in/trace/seal/finale loop, embers and glow, the skip button, and the
  live power/control meters. It reduces the whole session to `RuneTraceMetrics`.
- `Sorcerer.Core/Magic/RuneTraceScoring.cs` owns calibration: metrics to `CastPerformance`.
  Speed feeds power, accuracy feeds control, their combination damps or feeds wildness. Par
  speed plus mid-band accuracy maps to exactly 1.0/1.0/1.0, pinning the EV-neutral rule, and
  `MinimumScoringWindowSeconds` (2.75s) implements the too-fast-to-count discard. Unit tests
  pin the center, the bands, and monotonicity.
- The GUI submits `BeginCastCommand`, plays while the pending cast resolves, and attaches the
  score to `AwaitCastCommand`; `GameSession` stamps it onto the materialized resolution at
  the apply boundary. Spell audit entries record the performance a cast carried.

Passive skips score neutral too: a player who never touches a rune was watching, not
playing, and is treated exactly like one who pressed the skip button.

Engine-side mechanical use of the performance (severity scaling, mishap escalation) is the
next step and stays behind this seam; see "Engine Use" above.

## UX Rule

The player must be able to skip the minigame. Skipping produces a neutral result, not a
punishment. This keeps accessibility, CLI parity, and agent playtesting intact.

The CLI defaults to neutral performance; debug and agent testers may inject a fixed
`CastPerformance` (source `debug_fixed`) to exercise the power-vs-wildness dimension
without a GUI.
