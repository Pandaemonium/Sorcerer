# Spell Echoes - A Within-Run Repertoire

Status: proposed experiment 2026-07. Build behind a flag and validate with the probe below
before committing. Companion to [CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md) (recorded
resolutions, replay) and [CHARTER_MAGIC.md](CHARTER_MAGIC.md) (the other latency lever).

## The Idea

Every accepted wild cast already records its resolved JSON for audit and replay. Let the player
**re-cast a past spell as an echo**: the recorded resolution is re-fed through the ordinary
normalize -> validate -> apply pipeline in the *current* context - instantly, with no model call.
Costs still bite. References re-resolve against what is actually here now.

You are literally developing a personal tradition - which is what every folk tradition in the
canon *is*: someone's wild improvisations, repeated until they had names.

## What It Solves

- **Latency:** a second instant option in combat besides charter magic, one that keeps the
  player's own authored magic in their hands.
- **Within-run progression that respects no-meta-progression:** the repertoire is soul-bound
  state and dies with the run. Nothing carries forward.
- **Diegesis:** the bone-singer's rite was once an improvisation too. The player's grimoire is
  tradition-forming made playable.

## Mechanics Sketch

- The grimoire lists named echoes of past accepted casts (player names them, or the resolver's
  outcome text seeds a name).
- An echo replays the recorded effects through full validation: targets re-bind by the normal
  reference rules (selectors and names re-resolve; a dead id fails validation as usual), costs
  re-apply, treasured guards hold, deeds and witnesses record normally.
- **Echoes drift.** Wildness variance is re-rolled per echo from the seeded RNG, and drift can
  reflect region identity, recent magical history, and repetition pressure - in strange places an
  echo may come back changed. The wild substrate is not a vending machine.
- **Repetition fatigue (optional, test it):** each repetition of the same echo climbs the cost
  ladder slightly. The substrate gets bored of you; improvisation stays the best deal.

## The Risk, Stated Plainly

If echoes are too good they erode the improvisation that is the whole game. The failure smell:
players find three strong spells in the first hour and type nothing new after that. Every
guardrail above (drift, fatigue, costs that still bite) exists to keep fresh casting dominant,
and the probe exists to check that they do.

## The Probe (before committing)

Ship behind a flag; run agent and human playtests with it on and off; measure in transcripts:

- fraction of casts that are echoes over run time (alarm if it trends past ~1/3 and keeps rising)
- whether fresh-cast variety (distinct operation families per hour) drops with echoes enabled
- whether combat turn latency improves enough to matter

If echo usage crowds out fresh casting even with drift and fatigue tuned, cut the feature - the
latency problem then belongs entirely to charter magic and true background resolution.

## Open Questions

- Should echoes be castable at reduced mana (they are known ground) or full price? Start full.
- Should a many-times-repeated echo ever *crystallize* into a personal charter-like spell -
  reliable, fixed, weaker - as the endpoint of tradition-forming? Deliciously thematic;
  defer until the probe passes.
