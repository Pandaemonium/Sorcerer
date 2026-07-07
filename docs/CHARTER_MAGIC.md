# Charter Magic

Status: **first slice implemented 2026-07-07.** Bundles load from `content/charter/*.json`
(`CharterSpellbook`), the repertoire lives on the soul (`SoulRecord.KnownCharterSpells`,
save/load-safe, soul-bound), `charter` lists and `charter <id>` casts instantly through
`ApplyResolved` with zero provider calls, exact-name `cast` text also routes to the charter
lane (GUI parity via the shared parser), origins grant starting forms
(`startingCharterSpells` — the deserter charter mage starts with three), the opening
containment notice teaches Binding Writ I via `read` (`teaches_charter:<id>` tag), and
witnessed charter casts record a `charter_magic` deed that reads as plausibly licensed
(no uncanny legend/heat; +1 suspicion) in `WorldReactionSystem`. Unlicensed-context
escalation and squadron-mage casting are future layers. Tests: `CharterAndEchoTests.cs`.

Companion to [GRAND_VISION.md](GRAND_VISION.md),
[AESTHETICS_AND_TONE.md](AESTHETICS_AND_TONE.md), [WORLDBUILDING.md](WORLDBUILDING.md), and
[CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md). Every vision doc names charter magic as the
reliable counterpoint to wild magic; this document is its design.

## The Three Jobs

Charter magic earns its place by doing three jobs at once:

1. **The tone counterpoint made playable.** COLOR vs MARBLE is the game's core polarity, and the
   player should feel it in their own hands, not just in the scenery. Wild magic is jazz; charter
   magic is the textbook - genuinely beautiful, cold, repeatable.
2. **The latency answer.** The named failure mode "combat becomes repetitive waiting on the model"
   has a structural fix: charter spells resolve **deterministically, with zero model calls,
   instantly**. The player's moment-to-moment choice becomes *instant, weak, and precise* versus
   *slow, wild, and dangerous* - which turns provider latency from a defect into a diegetic cost
   of wildness.
3. **Tools between wild casts.** Per GRAND_VISION: reliable, repeatable, weaker, far less open.
   The strange, powerful, world-changing things must always require wild magic.

## Mechanical Identity

> A charter spell is an authored operation bundle: fixed effects, fixed price, fixed targeting
> rules, resolved through the ordinary pipeline with the provider stage removed.

- Charter spells run the same validate -> transact -> apply -> audit spine as everything else
  ([CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md), section 1, minus the provider and minigame
  stages). No new mutation path exists.
- Effects compose from the existing `IOperation` set. A charter spell that needs an operation the
  registry does not have is a design smell - either the operation is worth adding for wild magic
  too, or the spell is overreaching charter's role.
- Costs are **fixed and known before casting** - the exact inversion of wild magic's post-cast
  cost reveal. Repeatability and predictability *are* the fantasy being contrasted.
- Power stays capped below wild magic's ceiling. Charter magic never surges, never overreaches,
  never binds promises, never mints traits. It does exactly what it says, every time.

## Data Shape

Charter spells are content, not code: `content/charter/*.json`, loaded like operation cards.

```jsonc
{
  "id": "lumen_edict_3",
  "name": "Lumen Edict III",              // Latinate officialese, per the naming split
  "summary": "A licensed light. Steady, white, unromantic.",
  "effects": [ { "type": "createTiles", "tile": "light", "radius": 2 } ],
  "cost": { "mana": 2 },
  "targeting": "self|entity|tile",
  "licenseTier": 1                          // how advanced / how tightly controlled
}
```

The engine validates bundles at load (every effect type must resolve in the registry) and rejects
unknown operations as content warnings, the same rule as operation cards.

## Voice

One deliberate tone mechanic: **wild magic prose is regional; charter prose is uniform.** Wild
casts are narrated in the local voice per the tone bible; a charter spell reads identically in
the Saltmarket and the capital - the same cold, precise line everywhere, because that is what the
Empire built. Fixed strings per spell, no model call, and the uniformity itself dramatizes the
marble.

## Acquisition

The player is, by definition, unlicensed. Charter spells are learned, not granted:

- **Origins.** The deserter charter mage starts with a small licensed repertoire; other origins
  start with none or one folk-adjacent form.
- **Study and theft.** Charter paraphernalia are entities: a squadron field manual, a confiscated
  lens, a charter lattice, a dead mage's warrant-book. `read`/`examine`/theft can teach a spell,
  through the ordinary interaction and promise systems - a manual can bind a promise that
  realizes as a learned spell.
- **No skill trees.** The repertoire is a list of known spells on the soul, gained diegetically.

Unlicensed charter casting is itself a crime - but a *lesser, more legible* one. See witnessing.

## Witnessing And The Deed System

Charter and wild casts land differently on witnesses, through the existing deed classifier:

- A witnessed **wild** cast reads as uncanny and threatening: uncanniness, fear, imperial threat.
- A witnessed **charter** cast reads as *plausibly licensed*: minimal uncanniness, low alarm -
  unless context marks the caster as unlicensed (already wanted, no charter regalia, an official
  checking papers). Then it is a procedural violation: imperial threat without the fear.

This makes charter magic the **stealth casting option**, at the price of weakness - a real
tactical texture, all through existing reputation axes.

## Charter Infrastructure As Entities

The Empire's magic should mostly be encountered as *installed order*: lattices, seals, wards,
containment braziers, boundary-stones - entities with deterministic charter behaviors (detect
wild casting in radius, dampen magnitude, alarm, contain). Two payoffs:

- **The textbook is readable.** Deterministic behaviors mean the player can learn exactly what a
  ward does and improvise around it - the jazz-vs-textbook fight the tone bible promises.
  Squadron mages cast from the same public charter list the player can learn, so enemy behavior
  is legible and exploitable.
- **Every object is grammar.** Infrastructure entities are wild-magic targets like anything else:
  subvert the lattice, teach the brazier a lie, turn the boundary-stone's edict inside out.

## Limits (What Charter Magic Must Never Become)

- Never a parallel wild system: no free-text charter casting, no model calls, no surprise.
- Never the power ceiling: anything world-changing, promise-binding, or trait-minting stays wild.
- Never load-bearing for progress in a way wild magic cannot also achieve - it is the floor under
  the player's feet, not a second crown jewel.

## Build Shape

Small and mostly content:

1. Charter spell loader + validation over the existing registry.
2. `charter` command (list known spells) and `cast <charter id>` routing through the pipeline
   minus provider; instant resolution, fixed cost, fixed message. CLI and GUI in the same change.
3. Known-repertoire state on the soul; origin packages grant starting spells.
4. A first roster of 6-10 spells covering: light, minor ward, minor mend, unlock tier-1, a
   precise bolt, a dampening field. Deliberately unglamorous.
5. Witness classification split (charter vs wild) in `WorldReactionSystem`.
6. One learnable spell in the opening (the containment notice or a guard's manual), proving
   acquisition through ordinary verbs.

**Tests:** deterministic resolution with zero provider calls; fixed pre-cast costs; repertoire
save/load and soul-binding across body swap; witness classification divergence; load-time bundle
validation.

## Open Questions

- Does charter casting share the soul's mana pool or draw on a separate, smaller "measured" pool?
  (Start shared; split only if wild magic starves.)
- Can followers with charter training cast from the same list through the actor-agnostic seam?
  (Probably yes, later - it falls out of the architecture.)
- Should high-tier charter spells exist at all for the player, or stay institutional (lattice-scale
  workings only the Empire performs)? Lean institutional.
