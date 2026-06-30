# Character And Stats

Status: vision-for-later. The first playable can ship a single default profile; what matters
now is reserving the profile slot on the controlled entity and knowing that stats feed the
resolver. Adapted from the Wild Magic prototype. Companion to
[CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md) and [ENTITY_MODEL.md](ENTITY_MODEL.md).

## The three stats

A wild-magic-flavored triad, not the classic six:

| Stat | Drives |
|---|---|
| **Vigor** | max HP, melee attack/defense, carry capacity |
| **Attunement** | max mana, spell potency |
| **Composure** | how hard wild magic bites back - surge and backfire volatility |

**Composure is the wildness dial.** It governs how feral the magic is, and it ties directly
to casting performance: low Composure (or a poor casting-minigame score) means wild magic
answers more chaotically - backfires and costs more frequent, gorgeous-but-costly, more
surprising; high Composure means it answers cleanly, backfires rarer and gentler. The
minigame and Composure feed the same power-vs-wildness shaping; see
[CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md).

## The core mechanism: stats modulate the resolver's anchors

Stats are not just combat math. Their most important job is to **shift the anchors the
resolver sends the model** - numbers and tone both:

- **Attunement -> magnitude.** High Attunement pushes suggested effect magnitudes toward the
  top of each severity band; low keeps them at the floor.
- **Composure -> volatility.** Low Composure tells the model wild magic answers more
  chaotically; high tells it to answer cleanly.
- **Vigor -> cost framing.** High Vigor lets the model lean on health/physical costs the
  caster can shoulder; low steers costs away from raw HP.
- **Signature -> flavor lens.** The player's magical signature tints every cast, used
  sparingly so it tints rather than dominates.

A middling caster changes nothing - the anchors are unchanged. The mechanism is
prompt-shaping, so it works through the normal resolver; a deterministic fallback can read the
same profile so stats still bite offline.

## Origins

An origin is a starting package, not a class: a stat baseline, starting items, a tradition
tie, and how factions first react. Origins should tie to the world's traditions and realms
(see WORLDBUILDING.md) - a bone-singer's apprentice, a deserter charter mage, a desert Parn, a
merfolk exile. The specific roster is deferred; the shape is fixed.

## Free-form identity fields

Name, physical appearance, backstory, and magical signature are free text fed into the
resolver and NPC-dialogue context. Two rules:

- **The name is external.** The message log stays second person ("You"). The chosen name
  surfaces only where *others* refer to the player - NPC dialogue, imperial warrants, the
  clerk's memos. After a body swap, NPCs use the inhabited body's name.
- **The signature is a persistent lens**, injected into every resolution as idiom.

## Creation UX

Quick-start (a sensible random character on Enter) plus optional customize (pick or roll an
origin, spend a small point pool, fill the free-form fields). Sorcerer is death-heavy and
many-runs, so creation must never block the flow; the CLI uses a default profile so agents
never stall.

## Body swap

Stats and appearance come from the **body**; personhood, reputation, and the legend follow the
**soul** (see [ENTITY_MODEL.md](ENTITY_MODEL.md)). The controlled entity carries the active
profile; a vacated body keeps its own.

## Reserved lane (what to keep ready now)

Reserve a character-profile slot on the controlled entity and route it into the resolver
context. The first playable can run with one default profile; the triad and origins flesh out
when character creation is built.
