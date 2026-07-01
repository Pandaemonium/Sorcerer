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
- body change

Body changes are extremely severe and should be rare.

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
