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
- rumor ledger
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
- non-secret witnessed deeds write hidden witness memories through `record_memory`, preserving
  what local NPCs saw as ordinary dialogue context instead of a separate witness-knowledge lane
- non-secret deeds mint durable rumor records through the shared `record_rumor` consequence, and
  high-salience visible dialogue claims can become rumors through the same lane
- each pending deed reaction is staged transactionally: rumor records, witness memories, legend
  tags, faction shifts, heat/resource changes, visible narration, and the final `deedApplied`
  marker commit together, or a rejected child consequence rolls the plan back and emits only a
  hidden audit-only `worldReactionSkipped` delta
- rumors propagate deterministically at bounded turn/travel pump points into region and NPC
  carriers, spend budget only when a new carrier is reached, decay after early hops, become `stale`
  below the active threshold, and stage each rumor update with its heard-memory child writes so
  rejection rolls the whole hop back; successful rumors then appear in journal/debug views and
  generated dialogue context
- `WorldTurnSystem` audits bounded world initiative moves through `record_world_turn` consequences
  for faction pressure, rumor spreading, and high-salience promise stirring, so the world can move a
  little without becoming a full life sim
- background rumor distortion jobs also audit their queue and apply moments as hidden
  `record_world_turn` moves, so the rumor flywheel remains traceable through the same world-turn
  ledger even when text retelling is handled by background generation
- faction definitions carry roles, hostile roles, standing, and finite debug-only resources
- empire-bloc heat spends patrol/warrant/defense resources through budgeted `faction_pressure`
  world-turn moves into scheduled pressure; the resource, schedule, canon, and message changes
  return typed consequence deltas beside the wrapper move; a patrol can enter as a real hostile entity
- patrol pressure is bounded by active/pending patrol checks and response cooldown so escalation
  does not become a constant spawn ratchet
- quiet deed-free turn pumps run `WorldTurnSystem` recovery before budgeted pressure moves,
  regenerating pressure resources and letting heat ebb through typed resource consequences; actual
  recovery changes also leave a hidden `faction_recovery` world-turn audit
- world-turn moves are transactional apply-point packets: faction pressure, recovery, rumor
  spread, promise stirring, and want-stir memories restore staged state if any child consequence
  or final `record_world_turn` audit rejects, leaving only hidden rejection diagnostics
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
