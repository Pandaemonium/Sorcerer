# Promises And Prophecy

Promises and prophecy should be foundational in Sorcerer.

A promise is an engine-owned future obligation. It may begin as a prophecy, rumor, quest,
threat, debt, bargain, omen, or claim. The model can help phrase and interpret it, but the
engine decides whether it binds, where it can realize, and when it becomes visible.

## Why This Matters

The promise system is one of the clearest ways Sorcerer fulfills its central pitch:

> A game where you can do anything, but it actually matters what you do.

A spell can say:

```text
cast somewhere north, a blade waits with my name on it
cast in three turns a debt collector arrives because I stole tomorrow
cast let this shrine remember my mercy
```

The result should not be only flavor. The world should gain a structured future hook.

## Promise Sources

Promises can come from:

- wild spells
- NPC dialogue
- quests
- reading and examining
- world generation
- faction consequences
- background generation
- player deeds

All promise sources should flow into one ledger.

## Binding

The engine may refuse to bind a promise that is too vague, too broad, contradictory, or
mechanically unusable.

Examples:

- "something interesting should happen someday" can remain flavor.
- "a silver door opens east of here after I betray a friend" can bind.
- "all empires everywhere fall instantly" should reject or become a severe local omen.

Binding should consider:

- promise kind
- subject
- spatial hint
- trigger condition
- reward or threat
- salience
- confidence
- whether the world has room to realize it

Current implementation records both what was claimed and what the engine committed to:
`source`, `subject`, `claimedPlace`, `boundPlace`, `boundTargetId`, `triggerHint`,
`realizationKind`, and `realizedIn`. The `createPromise` and `addCurse` operations may
provide an explicit `target` plus `trigger`/`triggerHint`; otherwise the engine can bind
to the selected target, infer an anchor from nearby entity names/tags, or bind some
promise kinds to the current region.

Entities can carry `PromiseAnchorComponent`, which stores all promise ids attached to
that entity. This keeps promise realization object-owned without making promises a
renderer concern.

## Always-Honor (Yes-And)

The promise economy is pure yes-and: **not everything an NPC or spell says becomes a promise,
but every promise the world makes, it keeps.** The quality gate lives *upstream*, at binding -
once something is a bound promise, the world is obligated to deliver it. This is what makes the
world feel coherent rather than random: the player learns that a bound omen always means
something.

**Keep what was said separate from what the engine chose.** Record the claimed space and shape
(what was said) apart from the bound space and shape (what the engine committed to), so when
they differ it is narratable, never a silent rewrite. If the named place is already taken or
explored, **relocate rather than break** - "the chapel is further north than they said" - with
the relocation visible in the record.

**Honor what you can build; the rest stays talk.** A claim binds only if it maps to a buildable
realization archetype (a site, an NPC, an item, a threat, an event). No archetype match means
it stays flavor lore - it still colors dialogue, it just never realizes. This is how
always-honor stays sane: the world only promises what it can actually deliver.

**Binding guardrails (lessons from live runs).** Match the claimed thing against whole words in
the subject and tags, never against free prose (or a temple keeper's stray philosophy mints
phantom shrines). A non-quest claim should bind only if it names a buildable thing or carries a
real spatial hint; vague or low-confidence chatter stays flavor. Player-asserted claims are
never silently captured as world truth.

**Cost scales with binding strength.** Vague color promises are cheap; a guaranteed item, ally,
or threat is major magic. A prophecy spell that writes a concrete future obligation should
carry an engine cost floor on top of the resolution's own costs - writing the world a debt is
powerful, and powerful magic is paid for.

**Quests and debts are promise-kinds, not separate systems.** A quest is a promise with an
objective and reward; a debt is the world committing to a future collection. One ledger, one
lifecycle. Quest reservations are never the ones dropped when space is tight - an objective
that never realizes is an impossible quest.

## Visibility

Promises should feel mysterious to ordinary players.

The player may see:

- a journal omen
- a rumor
- a partial phrase
- a marked map hint
- an NPC warning
- a dream

Developer tools and agent debug observations may expose perfect structured promise state.
This is acceptable because agents are expected to use debug state for playtesting.

## Promise Types

Initial types:

- prophecy
- rumor
- quest
- threat
- debt
- bargain
- omen

Types are not hard genre boxes. They are engine routing hints for how the promise can bind,
realize, and display.

## Realization

A promise may realize as:

- an entity
- an item
- a site
- a room feature
- an NPC memory
- a faction reaction
- a delayed event
- a quest objective
- a hazard
- a reward
- a message or dream

Realization should happen at explicit engine-controlled apply points, never from arbitrary
background worker mutation.

Current apply points include:

- `read`: realizes matching promises anchored to the readable entity.
- `open`: realizes matching promises anchored to the opened door, then applies normal
  door consequences such as the first prisoner rescue.
- `talk`: realizes matching promises anchored to the spoken-to actor.

Current concrete realization archetypes are deliberately small but real:

- `memory`: writes a shareable memory to the world memory ledger and to the anchored entity.
- `threat`: spawns a hostile promised claimant near the anchor when space allows.
- `item`: creates a tangible promised item near the anchor.
- `quest`, `site`, and other omens: write durable canon records until richer handlers exist.

Trigger hints are intentionally simple. Empty hints can realize at the first matching
anchor interaction, while hints such as `read`, `open`, `door`, `talk`, or `name` route
the promise to an ordinary verb. This is not a full quest system yet; it is the first
engine-owned bridge from wild narrative claim to durable world consequence.

## Agent And Debug Surface

The agent/debug view should include:

- promise id
- type
- status
- subject
- spatial hints
- trigger hints
- bound location, if known
- bound target id, if known
- realization state
- realization kind
- player-visible text

The normal GUI can be more mysterious, but the backend state must be inspectable.
