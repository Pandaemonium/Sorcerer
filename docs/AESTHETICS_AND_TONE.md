# Wild Magic — Aesthetics & Tone Bible

Decisions made 2026-06. This document is the source of truth for the game's voice,
look, and feel. The flavor content currently in the repo (generic grimdark dungeon
texture) predates this document and should be brought into line with it.

## The North Star

A vibrant, eclectic, peculiar world — jewel-toned bazaars, saturated archipelagos,
folk-art psychedelia — policed by a handsome, orderly, *genuinely-not-evil* Empire
that exterminates wild mages with the bored competence of pest control.

You are the pest.

The game's joy is improvising ecstatic, feral wild magic to outwit by-the-book
imperial squadrons — and, across many runs, bringing the whole marble edifice down.

**The core polarity: COLOR vs. MARBLE.**

| | Wild | Empire |
|---|---|---|
| Magic feels | Ecstatic, joyful, alluring, feral | Genuinely beautiful, cold, repeatable |
| Fights like | A jazz musician | A textbook |
| Speaks in | Earthy folk compounds (the Glasswild, Bone-Singers, Saltmarket) | Latinate officialese (the Censorate, Provincial Edict 44, Thaumic Containment) |
| Kills you | Transformatively, strangely | Procedurally, like closing a file |
| Visual register | Jewel tones, riotous folk pattern, bioluminescence | Marble, brass, clean geometry, seals and ledgers |

The Empire is never gothic, never cackling, never skull-motifed. Its menace is that
it is *reasonable*, competent, and slightly bored by you. The world around it is
never grimdark. **Grimdark is the placeholder aesthetic we are deleting.**

## Locked Decisions

1. **Narrative voice: eclectic by region.** The message log, descriptions, and the
   LLM spell-resolution style guide all shift with locale — lush in the bazaar,
   clipped in imperial territory, sing-song among the birdfolk. Each region gets a
   short voice spec that is injected into the wild-magic prompts.

2. **Wild magic feels ecstatic AND alluring-feral.** Casting is raw creative joy —
   like dancing, like laughing too loud — but it is also seductive and does not
   entirely love you back. Backfires should often be *gorgeous*. Surges can be
   wondrous or absurd, never merely punitive.

3. **Charter magic is genuinely beautiful, cold.** Precise geometric light, perfect
   repeatability, master-calligrapher elegance. The player should understand why
   people choose it. The contrast with wild magic is bloodlessness, not ugliness.

4. **The name "charter magic" stays.** (The Garth Nix overlap is accepted; the
   real-world sense of "chartered = licensed" is doing the work we want.)

5. **Stakes: real menace amid color.** The world is mostly wonder; when the Empire
   arrives, the temperature genuinely drops. The contrast IS the aesthetic.

6. **Death is frustrating by design — and that's the engine of the story.** Imperial
   squadrons squash you the way an exterminator squashes a bug: no malice, no
   ceremony, just procedure. The player's animosity toward the Empire is *earned
   mechanically* through repeated cold, competent deaths. The counterweight: it must
   feel invigorating to finally outwit them — manipulating wild magic and the
   environment to beat squads that play strictly by the book. Deaths to wild forces
   may be strange and transformative; deaths to the Empire are paperwork.

7. **Dungeons vary by run/region.** A desert necropolis one run, a merfolk trench
   the next, a drowned sound-temple after that. Each region carries its own palette,
   props, bestiary, ambient messages, and voice. (Replaces the current generic
   torture-rack dungeon texture.)

8. **Protagonist: player-defined origin.** At run start you pick or roll a
   background — bone-singer's apprentice, deserter charter mage, merfolk exile,
   desert nomad — which seeds starting items, spell instincts, and faction reactions.

9. **The Empire's face: faceless squadrons + one clerk.** Field units are uniform,
   numbered, interchangeable. But one recurring mid-level official signs every
   warrant, posts every bounty, files every incident report you find — **the weary
   careerist**: you are a paperwork problem that will not close, and their memos
   grow visibly more exasperated as you keep surviving. Dry comedy that humanizes
   the machine without softening it.

10. **Weirdness rules.** Three principles combined:
    - *Exotic but grounded*: strange peoples take themselves seriously; humor comes
      from culture and character, never from the world winking at the player.
    - *Peculiar and dreamlike*: logic may bend — doors that open only for songs,
      markets that exist on Thursdays, a mountain slowly walking somewhere.
    - *Weirdness has causes*: imperial provinces feel surveyed and sensible;
      old, remote, wounded, or tradition-rich places can go dreamlike. Strangeness
      should come from region identity, customs, history, promises, and visible
      consequences, not from a hidden regional meter.

11. **Look & UI: region-skinned.** The ASCII presentation's palette and ornament
    shift with the region — sun-baked ochre and turquoise in the desert,
    bioluminescent glow in the trenches, marble-and-brass severity in imperial
    zones. In deep wild zones the interface itself is allowed to get a little
    strange.

12. **Naming: split by allegiance.** Wild/folk things get earthy English compounds;
    the Empire gets cold Latinate officialese. The naming system itself dramatizes
    the conflict. (Avoid heavy conlang invention; keep names readable.)

13. **The old traditions are everywhere.** Blood, bone, crystal, sound magic and
    more appear in all four roles:
    - *Player origins* (your background ties to a tradition),
    - *Living cultures* (the desert nomads ARE sound-magic folk; merfolk keep their
      own rites — traditions are geography, not just history),
    - *Buried strata* (ruins, relics, and readable fragments of traditions that
      didn't survive — the dungeon as archaeology),
    - *Spell-flavor vocabulary* (the LLM resolver receives the traditions as an
      idiom palette, so improvised casts can sound bone-sung or blood-warm or
      crystal-keyed).

14. **The long arc: destroy the Empire and end the prohibition.** This is winnable,
    by multiple paths — solo (e.g., kill the emperor) or geopolitical (aid other
    nations, win them to your cause, build a coalition). The game is a revolution,
    not an elegy.

## The Moral Texture

The Empire is not evil, and the player can still destroy it. Sit in that tension —
it's the most interesting thing in the game:

- Imperial peace is real. Roads are safe, trade flows, the charter system has
  genuinely prevented magical catastrophes. NPCs can credibly prefer it.
- Most of the known world *chooses* appeasement: three conquered kingdoms, one
  nominally independent client, small states keeping their heads down. Only one
  rival nation still openly allows wild magic.
- Individual imperials are people following tradition and procedure, not sadists.
- Therefore: coalition-building should involve real persuasion, and victory should
  raise real questions about what replaces the marble. The game never answers these
  with a sneer in either direction.

## Writing Style Guide

### Wild / folk register
Sensory, exuberant, present-tense, second person. Color and music words. Specific
over generic. Joyful even in danger.

> The spell leaves your hands like a startled flock. Where it lands, the flagstones
> remember being riverbed, and the moss blooms copper and singing-green.

> The bone flute's note hangs in the air a moment too long, listening back.

### Imperial register
Passive voice, file numbers, courteous menace. Never angry. Never colorful.

> NOTICE IS GIVEN that the individual styling themselves a sorcerer is subject to
> Thaumic Containment Order 7-112. Citizens rendering assistance will be
> compensated at the standard schedule.

> Incident closed. Remains unrecoverable. Recommend no further expenditure.
> — filed, Provincial Office of the Censorate

### The clerk's voice (found documents, escalating across runs)
> Third incident this quarter attributed to the same individual. Requisitioning
> a second squadron, against my own advice, since my first advice is apparently
> being ignored by reality.

### Ambient/dungeon messages
Per-region, replacing the current generic set ("Something moves in the dark," etc.).
Wonder-forward with an edge, not dread-forward:

> Somewhere above, a market bell is ringing on the wrong day.

> The crystal pillars hold yesterday's light. They are still deciding about you.

### Things to delete on sight
Torture racks, dissection tables, "the dungeon breathes," generic shadow-and-dread
ambience, skulls-as-decor, any prose whose only flavor is menace. If a dark element
earns its place (an imperial containment site, a tradition's ossuary), it must be
*specific* and culturally situated, never generic gloom.

## Narration Voice: Grounded and Specific

The world is vivid, but the *narration layer* — message-log lines, NPC speech, rumor text,
spell outcomes — should sound organic and concrete, like people with their own lives reacting
to what you did, not like an oracle intoning about your destiny. This is the register every
prompt and every canned string should hold to.

The rules:

- **Name people and places; never "the world," "the story," or "word."** Not "word of your
  magic takes on a sharper color" but "the dockhands who saw the fire are still arguing about
  it in the taproom." A consequence has a carrier — a person, with a face and a reason to care.
- **Attribute rumors to a teller with a mundane motive.** Gossip spreads because a drover wants
  to seem important, a clerk is bored, a debtor is frightened — not because the world remembers.
- **Reactions flow from wants, not cosmic significance.** An NPC helps or refuses because of a
  sick mule, a census due, a grudge, a price — concrete stakes the character actually holds.
- **Mystique is a character trait, not the default register.** Some people talk in omens and
  portents; most do not. Let the seer be strange and the ferryman be plain.
- **Concrete and sensory over portentous.** Say what is seen, heard, smelled, or owed. Wild
  magic may be genuinely strange — that strangeness is vivid imagery ("glass grows over the
  wound, warm to the touch"), never vague fate-speak ("the world remembers the shape of you").
- **The Empire stays procedural.** Imperial text is dry, civic, and bored — that register
  already works and is deliberate; this section does not soften it.

This spec is the shared source of truth for three prompt sites — NPC dialogue
(`OllamaDialogueProvider.SystemPrompt`), wild-magic `outcomeText` (`SpellPromptBuilder.CoreRules`),
and background enrichment (`BackgroundTextPrompt.SystemPrompt`) — and for the engine's canned
message tables (`WorldReactionSystem`, `RumorSystem`, `InteractionSystem`).

## Where This Touches the Code

- `wildmagic/prompts.py` — inject region voice + tradition idiom palette into the
  wild-magic resolution prompts. This is the highest-leverage tone change in the
  repo: it styles every spell the player ever casts.
- `wildmagic/engine.py` (~L1051) — replace the generic ambient message table with
  per-region tables.
- `wildmagic/game_data.py` — rework monster/prop/trap flavor to regional palettes;
  retire grimdark texture.
- `wildmagic/generation.py` — replace torture-rack prop pairs with region props;
  region-varied level generation.
- UI layer — region-skinned palettes; wild-zone strangeness effects.
- New content systems implied: found imperial documents (the clerk), player
  origins, region/voice specs, death screens split by killer (imperial incident
  report vs. wild transformation).

## Open Questions (for future sessions)

- Proper names: the Empire itself, the rival nation, the three conquered kingdoms,
  the client kingdom, the clerk, the emperor.
- Geography sketch: how regions, runs, and the overworld relate.
- The full roster of origins and which tradition each ties to. (v1 shipped: four —
  bone-singer/bone, deserter charter mage/charter, desert nomad/sound, merfolk
  exile/water. See docs/CHARACTER_CREATION.md. More origins/traditions still open.)
- How the geopolitical victory path works mechanically.
- Music/audio direction (sound magic and musical desert nomads suggest audio
  should eventually be a first-class aesthetic citizen).
- Full region voice specs (one short paragraph each, written for prompt injection).
