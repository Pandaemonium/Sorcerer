# Costs, Reagents, And Treasured Items

Wild magic should make the physical world tempting. Almost every object should have the
capacity to interact with magic, whether as fuel, focus, target, anchor, sacrifice, or
consequence.

## Cost Timing

Costs are normally visible after casting.

This is part of the drama of wild magic. The player reaches for power and only then learns
what the spell demanded.

The player can still shape costs organically by typing them into the spell:

```text
cast I burn the pearl to drown the guard in moonlit water
cast take my blood and bind the door shut
cast spend my last coin to call tomorrow's debt collector
```

The resolver should treat those hints seriously, but the engine remains authoritative.

## No Full Auto-Pricing Early

Do not build a full engine spell-budget economy early.

At first, the model may propose costs. The engine should validate and enforce basic
legality:

- cost type is supported
- item exists
- item is not protected unless explicitly authorized
- cost amount is valid
- target references are valid
- state mutation remains transactional

Engine-side spell scoring can arrive much later if playtesting shows underpaid or overpaid
spells are a persistent problem.

Owner calibration (2026-07-09): early development should be too permissive rather than too
protective. A surprisingly cheap or powerful spell is useful evidence and often a better story
than a precautionary refusal. Validate legality, consent, references, transactionality, and state
coherence now; tighten power balance only after repeated play demonstrates a real failure mode.

## Every Object Can Matter

Objects should be potential magical grammar.

An object may be:

- a spell target
- a spell origin
- a spell anchor
- a reagent
- a focus
- a promise anchor
- a memory or canon attachment
- a future quest object
- a transformed entity

This is why entity unification matters. The resolver should be able to refer to "the
mirror", "the door", "the book", "the corpse", "the statue", or "the nearest enemy" through
one reference system.

## Treasured Inventory

Players need a protected inventory lane for things they do not want wild magic to consume.

Call this protected, treasured, locked, safe, or keepsake inventory in UI text as the design
evolves. The durable rule is:

> Wild magic must not silently consume a protected item.

Required behavior:

- Protected items still exist in inventory.
- Protected items can still be used, equipped, traded, examined, or carried.
- Ordinary wild-magic item costs cannot spend protected items.
- A spell may spend a protected item only when the player explicitly names or authorizes it.
- CLI and GUI must both expose protected-item state.

## Valuable Item Confirmation

If a spell demands an unprotected but clearly valuable or unique item, the game may enter a
confirmation state after the cast:

```text
The spell asks for the moon pearl.
Accept the cost?
```

If the player declines, the refusal should consume the turn. The spell may fail, fizzle, or
twist into a harmless result. This preserves the danger and surprise of post-cast costs
without making the game feel like it stole a prized item.

The prompt should also offer a way to mark the item treasured from that confirmation.

## Cost Types

Sorcerer should eventually support many cost families:

- mana
- health
- max health
- max mana
- item
- status
- curse
- promise
- reputation exposure
- faction attention
- memory
- body or identity change

Body and identity changes are extremely severe, but severity alone is not an early-development
reason to reject them. Let them happen when the player intent and validated resolution support
them; use the resulting runs to learn where later balance or clearer consent is actually needed.

## The Cost Palette And The Overreach Ladder

Status: proposed 2026-07.

"Overreach is answered with severe cost, not a refusal" is the game's core instinct, but left to
the model alone, costs regress to mana and HP taxes - merely punitive, exactly what the tone
bible forbids. The fix is a **designed palette the resolver composes from**, organized as a
severity ladder the world climbs when the player reaches too high.

> The best costs are story generators. A cost that binds a promise ("a debt collector from
> tomorrow has accepted the charge") feeds the world instead of draining a bar.

The ladder, from mundane to terrible:

| Tier | Family | Examples | Lands as |
|---|---|---|---|
| 0 - mundane | mana, minor health | fatigue, a nosebleed | resource deltas |
| 1 - material | reagents, items, blood | the pearl is spent; the wand chars | item costs (treasured guard applies) |
| 2 - strange | statuses and traits on the caster | a memory of a name gone; your shadow lags; your voice loses its color for a day | `addStatus` / resolver-minted traits with real mechanical hooks |
| 3 - binding | curses, debts, attention | a curse with a clearing condition; a bound debt-promise; the deed ledger marks the cast louder than intended; a visible omen stains the place | `addCurse`, `createPromise`, deed/suspicion writes |
| 4 - severe | max stats, body, treasured things | max health carved away; a body change; the spell demands the moon pearl by name | max-stat deltas, transformation, treasured confirmation flow |

Rules that make the ladder work:

- **Overreach climbs, never refuses.** Magnitude beyond the caster's band is priced up-ladder
  rather than rejected. Rejection is reserved for the mechanically impossible.
- **Stats and performance shift where the world reaches.** Low Composure or a poor casting
  performance reaches higher on the ladder for the same spell; high Composure keeps costs near
  the floor. High Vigor makes physical costs plausible; low Vigor steers the reach toward
  strange and binding tiers instead ([CHARACTER_AND_STATS.md](CHARACTER_AND_STATS.md)).
- **Every cost must be mechanically real.** A tier-2 "strange" cost is a status or trait with an
  actual hook (the resolver and dialogue read it; some have engine ticks), never prose-only. If a
  cost cannot land in a ledger, it is not a cost.
- **Every cost carries its cause.** The player may not know the price before casting, but they
  must always understand afterward what was taken and by which spell.
- **Tier 3-4 costs are content, not punishments.** A curse has a clearing condition someone in
  the world knows; a debt-promise realizes as a claimant you can fight, pay, or out-bargain; a
  place-marking omen creates witnesses, rumors, or a promise site. Severe costs should open play,
  not close it.

Implementation shape: the palette ships as resolver guidance (a cost card alongside operation
cards) with tier vocabulary and examples; the engine validates that proposed costs map to real
cost families and enforces the existing bite/treasured rules. A deterministic fallback prices by
tier when the model omits costs on a high-magnitude resolution.

Implemented discipline: spell costs do not mutate their target state through a private
cost-side path. Mana, HP, max HP, and max mana costs submit `adjust_actor_resource`
consequences; item costs submit `modify_inventory`; status costs submit `apply_status`;
curse/debt costs submit `create_promise` with `operation: cost:curse`. Cost-specific messages
and the full `cost:*` consequence delta stream are preserved while the actual mutation uses the
same validation and audit lifecycle as dialogue, magic operations, services, promises, and world
turns. `modify_inventory` rejects insufficient `consume`/`remove`/`subtract` requests at apply
time, so costs and service payments remain authoritative even when an earlier preflight was too
permissive.

## Focus Versus Sacrifice

Focus and reagent sacrifice are different choices:

- Focus: keep the item and let it color future casts.
- Sacrifice: spend the item for a stronger or more specific cast.

The same object can support both paths. A crystal lens might be an excellent focus while
kept, or a powerful one-shot reagent when shattered.

## UI And CLI Requirements

Both interfaces should expose:

- item name
- quantity or instance identity
- tags/material/value when known
- spell-bias hints for resolver guidance
- protected/treasured state
- focus state
- whether it can be used, equipped, read, or examined
- whether it is visible to the resolver as a reagent

The JSON CLI should expose these fields structurally. Agents should not infer them from
prose.

Current implementation note: `MagicContextView.Reagents` includes only unprotected carried reagent
cards. Generated curios register definitions in the shared item catalog so their material, tags,
value, and spell-bias metadata survive pickup and can appear as resolver-visible fuel.
