# World Reaction And The Empire

Sorcerer is about a colorful world under cold imperial order.

The Empire is marble. The rest of the world is color.

## The Long Arc

The long-term win condition is to kill the emperor.
The emperor should exist as a killable character so the run can be technically won even before
the full late-game access, court, and final-encounter systems are elaborate.

The path to the emperor should eventually involve:

- personal power
- weakening imperial defenses
- trickery
- diplomacy
- reputation
- prophecy
- faction pressure
- alliances
- infiltration
- surviving imperial attention

The Empire should not be cleanly reformable. The more the player fights it with wild
magic, the more the Empire believes wild magic must be destroyed. The main alternatives
are destruction, evasion, hiding, or changing the world enough that the Empire can no
longer contain it.

## Moral Texture

The conflict should be morally ambiguous.

The player wants freedom. Other people want safety, order, revenge, tradition, profit, or
quiet survival. Some people will want freedom too. Others will want to restore old
traditions the Empire deemed threatening. The Empire may be oppressive and doomed by the
story's arc, but its supporters should not all be fools or monsters.

## Player-Driven Emergence

Sorcerer should not become a detailed life simulator.

The world does not need NPC hunger schedules, bread economies, or background lives modeled
for their own sake. It needs to respond organically to player action.

Preferred state lanes:

- deed ledger
- promise ledger
- faction ledger
- memory ledger
- canon ledger
- semantic notes for prompt context

These lanes should answer:

- what did the player do?
- who saw it?
- who merely saw the effect and became suspicious?
- who cares?
- what did it cost?
- what might answer later?
- how does the world show the consequence?

First implemented slice:

- accepted wild magic, attacks/kills, prisoner rescue, and body swap emit `DeedRecord`s
- deeds are classified as secret, suspicious, witnessed, public, or mythic through
  shared line-of-sight witness checks
- secret deeds do not enter public legend or standing
- suspicious deeds can raise imperial suspicion without magically attributing the caster
- witnessed/public/mythic deeds can become soul-bound legend tags and faction standing
  changes at the turn pump
- faction definitions carry roles, hostile roles, standing, and finite debug-only resources
- empire-bloc heat spends patrol/warrant/defense resources into scheduled pressure; a patrol can
  enter as a real hostile entity
- patrol pressure is bounded by active/pending patrol checks and response cooldown so escalation
  does not become a constant spawn ratchet
- quiet turn pumps regenerate pressure resources and let heat ebb
- `standing`, `journal`, and debug ledger/faction summaries expose the result to the CLI and tests

This is deliberately not the full empire simulation yet. Richer regional faction rosters,
wanted-poster artifacts, memos, rumors, and model-narrated reactions should spend and display
this lane rather than replacing it.

## Combat And Attention

Combat should be meaningful, not constant.

It should be common enough to invite wild spells, but not so common that provider latency
turns play into waiting. Combat can also threaten:

- cover
- attribution
- reputation
- faction standing
- promises
- access to towns or allies
- imperial attention

Avoiding combat through diplomacy, stealth, trickery, body swap, or magic should be
supported.

## Regions And Traditions

Regions should have distinctive traditions and voices.

Important traditions include:

- blood
- bone
- sound
- crystal
- water
- dreams
- names
- prophecy
- debt
- law
- others discovered later

Do not privilege this list so strongly that the world feels closed. Each region can lean
into a tradition mechanically by emphasizing engine systems that already exist: water
terrain, sound-based confusion, name curses, dream promises, bone summons, crystal
divination, and so on.

## Humor

Humor is allowed. It should emerge from character, situation, bureaucratic dryness, wild
misfires, and specific local cultures. Do not force jokes into the tone.

## No Meta-Progression

Runs do not carry power forward.

Allowed cross-run traces:

- grave markers
- chronicles
- memorial records
- optional player-facing history

These should commemorate, not empower.
