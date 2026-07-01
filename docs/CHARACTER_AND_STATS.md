# Character And Stats

Status: first implementation in place. Origins, body/soul stat ownership, the shared
`character`/`sheet` command, soul-bound mana, and resolver anchor shaping exist. Character
creation is still minimal quick-start plus CLI `--origin`; richer GUI customization and point
spend are later work. Adapted from the Wild Magic prototype. Companion to
[CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md) and [ENTITY_MODEL.md](ENTITY_MODEL.md).

## The three stats

A wild-magic-flavored triad, not the classic six:

| Stat | Owner | Drives |
|---|---|---|
| **Vigor** | body | max HP, melee attack/defense, carry capacity |
| **Attunement** | soul | max mana, spell potency |
| **Composure** | soul | how hard wild magic bites back - surge and backfire volatility |

This split is deliberate. Body swap should let a frail soul inherit a strong body, or a
powerful soul find itself trapped in a weak one. Vigor follows the flesh; Attunement and
Composure follow the person. HP follows the body; mana follows the soul.

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
prompt-shaping, so it works through the normal resolver; the mock provider reads the same
`ResolverLensView` so stats still bite offline.

Implemented thresholds:

- Attunement `<= 2`: effect magnitude delta `-1`.
- Attunement `3-4`: unchanged.
- Attunement `>= 5`: effect magnitude delta `+1`.
- Composure `<= 2`: volatile/wilder framing.
- Composure `3-4`: unchanged.
- Composure `>= 5`: cleaner controlled framing.
- Vigor `<= 2`: steer away from raw HP costs.
- Vigor `3-4`: unchanged.
- Vigor `>= 5`: physical costs are more plausible.

## Origins

An origin is a starting package, not a class: a split stat baseline (body Vigor plus
soul Attunement/Composure), starting items, a tradition tie, and how factions first react.
Origins should tie to the world's traditions and realms (see WORLDBUILDING.md) - a
bone-singer's apprentice, a deserter charter mage, a desert Parn, a merfolk exile. Phase 1
should ship a small real roster rather than placeholder origins, because first-reaction data
feeds the faction spine.

Implemented origins live in `content/origins/initial_origins.json`:

- `fugitive_wild_sorcerer`
- `bone_singers_apprentice`
- `deserter_charter_mage`
- `desert_parn`
- `merfolk_exile`

The CLI quick-start uses `fugitive_wild_sorcerer` unless `--origin <id>` is provided.

## Free-form identity fields

Name, physical appearance, backstory, and magical signature are free text fed into the
resolver and NPC-dialogue context. Keep body-facing and soul-facing fields distinct enough
that body swap stays coherent: public name and appearance normally follow the body; magical
signature and personal backstory normally follow the soul. Two rules:

- **The name is external.** The message log stays second person ("You"). The chosen name
  surfaces only where *others* refer to the player - NPC dialogue, imperial warrants, the
  clerk's memos. After a body swap, NPCs use the inhabited body's name.
- **The signature is a persistent lens**, injected into every resolution as idiom.

## Creation UX

Quick-start (a sensible random character on Enter) plus optional customize (pick or roll an
origin, spend a small point pool, fill the free-form fields). Sorcerer is death-heavy and
many-runs, so creation must never block the flow; the CLI uses a default profile so agents
never stall.

Current implementation is deliberately small: CLI supports `--origin <id>` and the shared
`character`/`sheet` command; Godot starts from quick-start and can read `SORCERER_ORIGIN`
for development runs while showing stats in the side panel.

## Soul and body ownership

Implementation represents soul-owned state with `SoulLedger` / `SoulRecord` keyed by
`SoulComponent.SoulId`, rather than scattering soul facts across whichever body is currently
controlled. Soul-owned state includes Attunement, Composure, current/max mana, magical
signature, personal backstory, origin/tradition, and faction first-reaction seeds. Exploration
memory is also soul-bound in `ExploredBySoulId`. Body-owned state includes Vigor, HP,
inventory, equipment, public name, appearance, and physical condition.

## Body swap

Vigor, appearance, public name, inventory, equipment, and physical condition come from the
**body**; Attunement, Composure, mana, personhood, reputation, exploration memory, and the legend
follow the **soul** (see [ENTITY_MODEL.md](ENTITY_MODEL.md)). The controlled entity carries the
active body profile; soul-facing profile fields move with the soul.

## Reserved lane (what to keep ready now)

Done for the first implementation: body-facing and soul-facing slots route into
`MagicContextView.ResolverLens`. Remaining work is richer character creation UX, point spend,
more origins, and deeper use of faction first-reaction seeds once Phase 3 factions mature.
