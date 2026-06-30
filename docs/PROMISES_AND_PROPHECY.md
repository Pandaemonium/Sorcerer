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

## Agent And Debug Surface

The agent/debug view should include:

- promise id
- type
- status
- subject
- spatial hints
- trigger hints
- bound location, if known
- realization state
- player-visible text

The normal GUI can be more mysterious, but the backend state must be inspectable.

