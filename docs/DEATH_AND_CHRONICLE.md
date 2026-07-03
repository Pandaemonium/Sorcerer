# Death And The Chronicle

Status: proposed 2026-07. Companion to [AESTHETICS_AND_TONE.md](AESTHETICS_AND_TONE.md) (locked
decision 6: death is frustrating by design, and that is the engine of the story) and
[MATURITY_ROADMAP.md](MATURITY_ROADMAP.md) Phase 8 (chronicles and memorials exist as a first
slice). This document designs the death-to-new-run arc as an experience rather than a state
transition.

## Death Screens Split By Killer

The tone bible already decides this; make it real:

- **An imperial death is paperwork.** The death screen is the incident file: case number, the
  clerk's voice, the standard schedule, "Incident closed. Remains unrecoverable. Recommend no
  further expenditure." Cold, stamped, bloodless. The player's animosity toward the Empire is
  earned mechanically here, one closed file at a time.
- **A wild death is a transformation.** Narrated in the region's voice, strange and specific -
  what the drowning rite made of you, what the walking stone carried away. Deaths to wild forces
  should read as endings the world found *interesting*.
- Deterministic templates keyed by killer faction/region always exist; the model may enrich them
  when a provider is warm, through the normal narration boundary.

## The Chronicle As The Narrator's Showpiece

The run-closing chronicle is where the whole flywheel is read back to the player. It should be
assembled from the ledgers, not from a template:

- **Deeds as the world tells them** - preferring the rumor-distorted versions over the raw
  records once the rumor layer exists ([SYSTEMS_VISION.md](SYSTEMS_VISION.md)). The chronicle is
  the world's memory of you, not your save file.
- **The futures that never came.** Unrealized bound promises are listed as what the world still
  owed you - the blade that waited north with your name on it, unmet. This is cheap, haunting,
  and teaches the promise system retroactively.
- **Epitaphs from bonds.** One line each from the NPCs whose bonds ran deepest, voiced by
  posture and history: the follower who left, the fence who trusted you, the clerk who finally
  closed the file.
- **The shape of the war.** Faction standing and imperial heat at the end - how close the marble
  came to cracking.

Structure is deterministic (which ledger entries qualify, in what order); prose may be
model-enriched. The chronicle file persists per run and should be pleasant to share.

## Cross-Run Traces Feed The Flywheel

The standing rule is commemorate, never empower. Current memorials are inert props; make them
*narratively* live while staying mechanically inert:

- **Grave markers and chronicles seed the next run's rumor layer.** The new world can carry
  distorted tales of the previous sorcerer - a shrine where they fell, a ballad with the facts
  wrong, a Censorate file referenced in passing. Zero mechanical power flows; the previous run
  becomes texture and dramatic irony instead of a stat bonus.
- Origins or dialogue may occasionally reference the tales the same way they reference any
  rumor. The dead player is just a rumor source with excellent provenance.

## Death-To-New-Run UX

Sorcerer is death-heavy and many-runs; the loop must be fast and appetizing:

- Chronicle -> one keypress -> quick-start into a fresh world roll. Character customization
  stays optional and never blocks (the existing creation rule).
- The first minutes of a new run should surface one fresh-world hook early (the new geopolitics,
  a new region voice) so the reward for death - a wholly new world to read - is felt
  immediately, not discovered an hour in.

## Build Shape

1. Killer-split death screen templates (imperial file / wild transformation), keyed by killing
   faction and region; deterministic first, model-enriched later.
2. Chronicle assembly from ledgers: qualifying deeds, unrealized bound promises, top bonds,
   final faction posture. Deterministic ordering; prose enrichment behind the narration boundary.
3. Memorial records extended to emit rumor seeds into the next run's world roll (blocked on the
   rumor layer; until then, keep inert props).
4. Restart flow polish: chronicle -> quick-start without menu friction, CLI and GUI alike.

**Tests:** death screen template selection by killer; chronicle includes unrealized bound
promises and omits unbound claims; chronicle assembly is deterministic under replay; memorial
rumor seeds carry no mechanical effects; restart reaches a playable fresh run in one command.
