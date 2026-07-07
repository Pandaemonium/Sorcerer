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

## Second Minigame: Thread & Knot (implemented)

The push-your-luck entry: the spell spools out of the caster as a living thread of light.
Hold (mouse or Space) to draw it — the longer the pull, the faster power banks and the
faster the thread frays. Release to whip it into a knot and bank the draw onto a glowing
necklace; pull past the fray and the thread snaps, scattering the unbanked power as embers
and staining the chain violet. Threads chain indefinitely, each knot tied raising the fray
rate of the next, so unknown latency is again the natural clock. Unlike rune tracing this
game demands no aiming at all — it is pure risk judgment, which also makes it the
low-dexterity entry in the rotation.

- `Sorcerer.Godot/Scripts/Minigames/ThreadKnotMinigame.cs` owns presentation and gesture
  reduction: a small verlet rope that sags in a catenary when slack and hums with a rising
  standing wave under strain, seeded gust schedules (`RuneShape.SeedFor`, so recasting the
  same phrase meets the same temperament of thread), fray fibers and a strain arc as the
  legible warning ladder, tie/snap bursts, and the banked-knot necklace whose bead sizes
  read out pull greed. It reduces the session to `ThreadKnotMetrics`.
- `Sorcerer.Core/Magic/ThreadKnotScoring.cs` owns calibration: banked draw per second
  against par feeds power, the clean-tie fraction (knots vs. snaps, mid-band at 7-of-10)
  feeds control, and their combination damps or feeds wildness. Par banking with a par
  share of snaps maps to exactly 1.0/1.0/1.0, pinning the EV-neutral rule; the shared
  minimum-window discard applies, and a session that never resolved a thread (no tie, no
  snap) is also neutral. Unit tests pin the center, the bands, monotonicity, and the
  timid-weaver corner (small clean knots trade power for control — legitimate, not
  degenerate).
- If the provider settles mid-pull, the pull in hand gets the standard grace and then ties
  itself off — a hold at the buzzer banks rather than punishes.

## Third Minigame: True-Sigil (implemented)

The recognition-memory entry: the wild shows the spell's true shape once — a sigil burns
into the air inside the memory ring, holds, and crumbles to falling ash — then ghost
sigils rise from the ashes and ask to be chosen. One is true; the rest are lies built from
the truth itself: mirrors, rotations, whole strangers, and (as rounds chain) near-perfect
forgeries whose single moved vertex gets subtler each tier. Flash time shortens and a
fourth candidate appears as tiers climb, so an 80-second wait has an arc. Pure clicks, no
dexterity and no hold-timing: this is the accessibility anchor of the suite.

- `Sorcerer.Godot/Scripts/Minigames/TrueSigilMinigame.cs` owns presentation and gesture
  reduction: seeded truths and liars (`RuneShape.SeedFor`, so recasting the same phrase
  tests memory rather than luck, and a symmetric sigil's mirror is detected and replaced so
  a lie is always a real lie), the burn-in/hold/ash-dissolve flash, rising ghosts on
  pedestal rings, an honest flash-countdown arc and a soft response-timer arc (slow answers
  are safe answers, just weaker), gold ignition on a true pick, violet shatter plus a
  teaching glimmer of the missed truth on a false one, and a bead-row of the run's verdicts.
  It reduces the session to `TrueSigilMetrics`.
- `Sorcerer.Core/Magic/TrueSigilScoring.cs` owns calibration: answer speed against par
  feeds power, accuracy feeds control (floor 0.50 ≈ chance, mid-band 0.75), and their
  combination damps or feeds wildness. Par speed plus three-of-four accuracy maps to
  exactly 1.0/1.0/1.0, pinning the EV-neutral rule; the shared minimum-window discard
  applies, and a session with zero resolved rounds is also neutral. Unit tests pin the
  center, the bands, monotonicity, and both trade corners (slow-but-sure, fast-but-guessing).
- If the provider settles mid-round, the choice in hand gets the standard grace; an
  unanswered round is abandoned uncounted — never scored, never punished.

## Fourth Minigame: Bone-Song (implemented)

The rhythm entry, and Sorcerer's first audio: Bralli scrimshaw drumming (see
`content/lore/bone.md` / `brall.md` — boasting is hospitality). The spell's pulse runs a
carved whalebone drum-rim like a lit fuse; strike as the ember crosses each carving. Plain
notches feed control; tight gold accent knots feed power, and *declining* one is safe — it
only costs power (the boast is optional); hollow rests, tier-gated in from the third bar,
must not be struck at all. Bars chain indefinitely, re-carving faster and more syncopated,
and every completed bar etches one more stroke of a scrimshaw whale-hunt into the drumhead —
a long provider wait literally becomes art accumulating. Dropped beats splinter hairline
cracks into the rim, the run's wear worn openly.

- **The visual clock is the source of truth and audio is decoration on top, never the
  reverse.** The beat is geometry (an ember approaching a notch), so the game is fully
  playable with sound off — deaf accessibility is structural — and there is no latency
  calibration screen, because players react to position rather than anticipating audio.
- `Sorcerer.Godot/Scripts/Minigames/BoneSongVoice.cs` synthesizes all percussion in code
  through an `AudioStreamGenerator` (bone tok, accent doom, rest yelp, miss crack, carving
  scrape) over a streak drone — hummed bone-singer fifths that swell as clean hits chain and
  drop out on a miss, so the mix itself is the feedback ladder. No sound assets ship.
- `Sorcerer.Godot/Scripts/Minigames/BoneSongMinigame.cs` owns presentation and gesture
  reduction: seeded bars (`RuneShape.SeedFor`, so the same phrase always sings the same
  song — learnable, never arbitrary), tempo and syncopation escalation, hit windows
  (±120ms plain, ±60ms accent, ±140ms rest), the scrimshaw scene, and the crack ledger. It
  reduces the session to `BoneSongMetrics`.
- `Sorcerer.Core/Magic/BoneSongScoring.cs` owns calibration: accuracy over resolved notches
  feeds control (band 0.45-0.95, mid 0.70), the fraction of offered accent knots taken
  cleanly feeds power (par = half), and their combination damps or feeds wildness. Mid-band
  accuracy plus half the knots maps to exactly 1.0/1.0/1.0, pinning the EV-neutral rule; a
  bar offering no knots judges power as neutral; the shared minimum-window discard applies,
  and zero resolved notches is also neutral. Unit tests pin the center, the bands,
  monotonicity, and both drummers (timid and boastful).
- Swings at empty air break the streak and make a sound, but are not ledgered — enthusiasm
  is not a crime. If the provider settles mid-bar, the standard grace applies and the bar
  in hand ends unjudged.

## The Repertoire Draw

With more than one game in the suite, the GUI pulls a random game per cast and never plays
the same game twice in a row, so every game stays in circulation (`Main.PlayCastMinigameAsync`).
The suite now spans four distinct skills — precision pursuit (rune trace), risk judgment
(thread & knot), recognition memory (true-sigil), and timing (bone-song) — so every player
has at least one comfortable way to play rather than skip. A later settings toggle to
exclude individual games is the planned accessibility answer.

## UX Rule

The player must be able to skip the minigame. Skipping produces a neutral result, not a
punishment. This keeps accessibility, CLI parity, and agent playtesting intact.

The CLI defaults to neutral performance; debug and agent testers may inject a fixed
`CastPerformance` (source `debug_fixed`) to exercise the power-vs-wildness dimension
without a GUI.
