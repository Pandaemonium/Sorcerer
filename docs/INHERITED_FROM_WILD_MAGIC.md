# Inherited Soul & Canon Docs

These documents were carried over verbatim from the Wild Magic prototype because they are
the durable **heart and soul** of the game, not implementation. They are engine-agnostic:
ignore any references inside them to Python modules (`wildmagic/*.py`) or to the old game
name. Sorcerer is the clean-slate successor; the world, voice, and spell ambition are the
same.

When tone, world fact, or spell ambition is in question, these win.

## Copied here (source of truth for feel and world)

- **AESTHETICS_AND_TONE.md** - the voice/look/feel bible. COLOR vs MARBLE; wild magic as
  ecstatic and feral; the Empire as reasonable, competent, not evil; region-skinned
  registers; backfires that are gorgeous, not merely punitive. This governs the *register*
  of every message and every spell resolution.
- **WORLDBUILDING.md** - the canon: the Grand Empire of Vigovia, the four old kingdoms
  (Stalnaz/Brall/Ryolan/Vint), the client and small realms, the peoples, the traditions as
  cultural dialects of one wild substrate, and the Shadow Purge. This governs *what is
  true* about the world. Note: it already answers several of Sorcerer's own
  OPEN_QUESTIONS (what the Empire/emperor are, the regions, the founding traditions).
- **SPELL_COMPENDIUM.md** - ~400 example spells (the tone and ambition the resolver must
  live up to) plus a taxonomy of ~200 currently-hard spells grouped by the engine
  mechanics they would need. Use the first half as the eval corpus and the tone bar; use
  the taxonomy as a north star for growing the operation grammar. A few taxonomy entries
  reference old UI/LLM internals (e.g. flipping a Pygame screen) and do not apply.

## Where they touch tone vs fact

`AESTHETICS_AND_TONE.md` wins on register and feel; `WORLDBUILDING.md` wins on fact. They
were written as companions and agree.

## Vision wisdom still living in the Wild Magic repo (adapt, do not raw-copy)

These hold real design wisdom but are welded to the Python implementation and its phase
history, so copying them verbatim would mislead a clean-slate build. They should be
distilled into Sorcerer-native docs when their systems are built:

- the responsive/emergent world model (deeds -> legend -> standing -> a deterministic
  simulator -> an LLM narrator; the LLM interprets and narrates, it never simulates) ->
  belongs in WORLD_REACTION_AND_EMPIRE.md
- "semantic by default, mechanical on demand, never on the critical path" - entity
  descriptions/traits are dormant mechanics that the model surfaces when relevant; this is
  why an LLM-driven world feels alive, and it is directly relevant to the resolver and to
  "every object is grammar"
- the always-honor promise model (claimed vs bound space, buildable-archetype binding, the
  false-binding lessons from live runs) -> belongs in PROMISES_AND_PROPHECY.md
- the Vigor / Attunement / Composure stat model and tradition-tied origins; Composure as
  the wildness dial ties directly to CASTING_AND_MINIGAMES.md
