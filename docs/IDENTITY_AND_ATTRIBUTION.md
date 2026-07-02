# Identity And Attribution

Status: proposed 2026-07. Companion to [EMERGENT_WORLD.md](EMERGENT_WORLD.md),
[WORLD_REACTION_AND_EMPIRE.md](WORLD_REACTION_AND_EMPIRE.md),
[CHARACTER_AND_STATS.md](CHARACTER_AND_STATS.md), and [ENTITY_MODEL.md](ENTITY_MODEL.md).

## The Tension This Resolves

The docs contain a deliberate rule and a latent contradiction:

- **Legend is soul-bound** so body swap cannot launder reputation. Correct, and unchanged here.
- But **witnesses see bodies** - a face, a public name, regalia, a magical signature. The Empire
  cannot file a warrant against a soul.

Rather than paper over this, make it a system: split *what the world deeply senses about you*
from *what the world's institutions have written down about you*.

> Legend stays soul-bound. Attribution attaches to an **identity** - a name + face + signature
> cluster - and identities can be forked, worn, and eventually merged by evidence.

## The Two Records

**Legend (existing, unchanged).** Soul-bound weighted tags (defiant, butcher, merciful, uncanny)
plus the uncanniness-flavored reputation axes. This is the world's *deep* sense of you - it
colors the resolver, dreams, omens, and how the wild substrate itself answers. It follows the
soul through any body precisely because it is not paperwork; it is what you *are*.

**Identity records (new).** An `IdentityLedger` entry is what institutions and strangers can
point at: public name, appearance/body reference, known magical signature fragments, and the set
of deeds attributed to it. Warrants, wanted posters, bounties, and the clerk's memos name an
identity, never a soul. Institutional reputation axes - notoriety, fear, imperial threat as *the
Empire tracks it* - are computed per identity from its attributed deeds.

## How Attribution Flows

- At deed time, the witness model already classifies visibility. Attributed deeds now record
  **which identity was worn**: the current body's face and name, plus signature if magic was
  sensed. `DeedRecord` carries `soulId` (for legend, existing) and `identityId` (for
  attribution, new).
- Suspicious deeds attach to *no* identity until the existing 20-turn line-of-sight rule
  resolves - and then to the identity seen, not the soul.
- Faction heat, warrants, and patrol targeting read the identity ledger. The Empire hunts "the
  Reed-Witch of Hollowmere"; if you wear a customs officer's face, its instruments point at the
  wrong file.

## Forking And Merging

**Forking is play.** A body swap, a disguise, a false name given in dialogue, or magic that
changes the face creates or adopts a different identity. Deeds committed while wearing it
accumulate there. This makes disguise, pseudonymous mischief, and body swap real strategies that
ride the existing witness model rather than a new stealth system.

**Merging is drama.** Identities merge when evidence links them, through deterministic rules:

- a witness sees the change itself (a swap observed, a disguise pierced)
- the same magical signature is sensed on deeds filed under two identities
- a confession, an informant with knowledge of both, or a captured follower
- imperial cross-referencing: at world-turn pumps, the Censorate may *spend a resource* to
  correlate files - merging is an expenditure like any other faction reaction

When identities merge, their attributed deeds pool, and the institutional reputation snaps to the
combined weight - a designed "the mask comes off" beat. The clerk's memos are the natural
narration surface: their slowly mounting suspicion that incident files 7-112 and 9-41 describe
one individual is both mechanics and the best possible use of the weary-careerist voice.

## What Sees Through

- **Personal bonds can know the soul.** An NPC with a deep bond (or who witnessed a swap) keys
  their bond and memories to the person, not the face - loyalty that survives your
  transformation, or a betrayal risk who can testify across identities.
- **The uncanny senses the soul.** Legend-reading systems (the resolver lens, deep-wild beings,
  prophecy, shrines) were never fooled; they read the soul-bound record. In imperial territory
  you are your file; in old or uncanny places you are what you have done. That contrast is the
  COLOR-vs-MARBLE polarity expressed as an information system.

## Guardrails

- **No reputation laundering.** Soul-bound legend is untouched by identity play; forking hides
  you from *institutions*, not from the world's deep memory or from bound promises.
- **Merging is deterministic; the model narrates.** Evidence rules decide when files merge; the
  model writes the memo. Never the reverse.
- **Costs stay real.** Maintaining a clean identity has upkeep (avoid signature-revealing casts
  while wearing it; witnesses to the fork are loose ends), so identity play is a strategy with
  texture, not a free reset button.

## Build Shape

1. `IdentityLedger` + `identityId` on `DeedRecord`; the controlled body's current identity
   resolves at deed time.
2. Warrants, posters, patrol targeting, and empire heat read identity-attributed deeds instead
   of soul-attributed ones. Legend distillation keeps reading the soul. (This is mostly a
   re-pointing of existing reads.)
3. Fork events: body swap creates/adopts an identity; a false name in dialogue can mint a thin
   one.
4. Merge rules at pump points, including the Censorate cross-reference expenditure; merge deltas
   and clerk-memo narration.
5. Bond records gain a soul-keyed flag for NPCs who know the person.

**Tests:** deeds attribute to the worn identity; soul legend unchanged across forks; suspicious
deeds resolve to the seen identity; merge pools deeds and re-computes institutional axes
deterministically; body swap under witness creates a linking evidence record; soul-keyed bonds
survive swap.

## Open Questions

- How cheap should thin identities (a false name alone) be before they need supporting evidence
  (regalia, forged papers) to hold up?
- Should some axes (gratitude, legitimacy) be soul-leaning even institutionally, since they
  spread through rumor rather than filing? Likely yes once the rumor layer exists - rumors can
  carry either the identity or, distorted, hints of the truth.
