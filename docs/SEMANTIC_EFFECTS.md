# Semantic Effects - Living Descriptions As Dormant Mechanics

Status: proposed. Companion to [CORE_EXECUTION_MODEL.md](CORE_EXECUTION_MODEL.md),
[MAGIC_RESOLVER_ARCHITECTURE.md](MAGIC_RESOLVER_ARCHITECTURE.md), and
[ENTITY_MODEL.md](ENTITY_MODEL.md). Adapted from the Wild Magic prototype's design wisdom;
rebuilt on Sorcerer's entity/operation/view architecture.

This is one of the load-bearing reasons an LLM-driven world feels alive, and it is directly
in service of the north-star promise that **every object is grammar**.

## 1. The idea

A normal roguelike has a hard wall between **flavor text** and **mechanics**. A "righteous,
imperial-hating dagger" is a string; it does nothing until someone writes a rule for it.

Sorcerer does not have that wall, because the thing that resolves spells (and later voices
NPCs and drives creature behavior) is a language model that **re-reads descriptions at
decision time**. So a descriptor like "righteous, imperial-hating" is not flavor and not
mechanics - it is **mechanics in a dormant state**, waiting for a context where it becomes
relevant:

- The dagger is animated by a spell -> the resolver, seeing "imperial-hating," has it strike
  the ward-captain rather than the neutral merchant.
- An imperial NPC sees the player holding it -> later dialogue colors their reaction.
- A spell asks "what does the nearest weapon want?" -> the answer is already written.

None of those payoffs were authored as rules. They emerge because the trait was **present in
the context at a moment the model was making a judgment**. This is a genuine structural
advantage of an LLM-driven game, and it is cheap: a cast can mint evocative, specific content
- the resolver authoring it, often from how the player phrased the spell - and let the
mechanical payoff arrive later, through the model, in situations nobody enumerated in advance.

## 2. The one fact that drives every decision

> A semantic effect only exists **if it is in the model's context at the moment it becomes
> relevant.**

The dagger strikes the captain only if, when the dagger acts, both "imperial-hating" and
"there is an imperial at (x,y)" are in the same context. This is not a storage problem -
strings are free to store. It is a **context-assembly problem**, the same one the cast
context view and capability routing already solve.

This has one hard consequence:

> **Attach traits to entities and items, never to "the ambient space around things."**

In Sorcerer terms: every entity surfaced into the `MagicContextView` (RFC section 4) already
carries its name, materials, and tags. A trait on that entity rides into the resolver's
context **for free and reliably** whenever the entity is perceived. A free-floating "semantic
field on nearby tiles" is the expensive, leaky version - we would have to actively gather and
inject it, and it would usually be absent exactly when it mattered. Entity-attached traits
are Chekhov's guns that stay on the mantel; ambient traits wander off before Act 3.

## 3. Design principles (the load-bearing rules)

1. **Semantic by default, mechanical on demand.** A trait is, until proven otherwise, pure
   description with no fixed rule. It is cheap to mint precisely because it promises nothing.
   It becomes mechanical only when the model decides a situation squarely calls for it.

2. **Cost at mechanization, not at description.** Minting a trait is free. When a trait is
   **cashed out** into a real effect - the dagger actually swings - that action is emitted as
   a normal **operation** and passes through the standard pipeline: validate, apply
   transactionally, pay costs (RFC sections 2 and 5). This is self-balancing: an unredeemed
   trait that never becomes relevant costs nothing and quietly fades, so nothing was
   promised and nothing was broken. It also dodges the otherwise nasty question - "how do you
   price a power whose payoff is unknown?" - by never pricing the description at all.

3. **Never on the critical path.** The player must **never need** a semantic effect to fire
   in order to progress. Semantic effects are upside and delight, never a gate. They must not
   silently lock a quest, create an unwinnable state, or be the only way past an obstacle.
   This rule protects everything else.

4. **Weigh, don't apply.** When we tell the model about traits, the instruction biases
   **judgment**, not **mechanization**: "let them color tone, targeting, and plausibility;
   you *may* turn one into a concrete effect when the situation squarely calls for it" - never
   "use these traits to resolve the spell." The framing is the guardrail against over-firing.

## 4. The lifecycle: semantic -> judgment -> mechanical

Purely-semantic-and-load-bearing is a trap (a power that fires only sometimes corrodes
strategy). The way out is a two-stage life cycle that gives both emergence and eventual
reliability.

```text
   mint a trait            model judges it           crystallize
   (free, no rule)   -->   relevant this cast   -->  into a standing
   "imperial-hating"       (weighed, may fire)       mechanic (status/tag)
        |                        |                        |
   stage 1: SEMANTIC        one-off payoff           stage 2: MECHANICAL
   color only               (a single resolution)    reliable + legible
```

- **Stage 1 - semantic.** The trait sits on the entity as narrative weight. It colors tone,
  targeting, and plausibility whenever the entity is in context. No rule, no guarantee.
- **One-off payoff.** On a given cast the model may **weigh** the trait and let it shape a
  single resolution (the animated dagger targets the captain this turn). Still no standing
  rule.
- **Stage 2 - crystallization.** When a trait genuinely matters, the resolver can **promote**
  it into a standing modifier by emitting an operation that writes a durable status, tag, or
  component the engine reads deterministically every turn (e.g. an `addStatus` that an Actor
  carries, or a behavior tag the AI reads in target selection). After promotion it is
  reliable and legible - the player can see "Righteous (strikes at imperials)" and reason
  about it.

Crystallization is just an operation (RFC section 2) whose **content** came from a trait the
model read. It is the second dividend of the operation registry: promotion has somewhere to
land.

## 5. How it sits in Sorcerer's architecture

Concrete mapping onto the existing scaffold and the RFC:

- **Storage: a traits channel on the entity.** A `TraitsComponent(IReadOnlyList<string>
  Traits)` (in `Sorcerer.Core/Entities`), capped (about 6, newest-wins). Items and fixtures
  carry theirs through pickup, equip, and transformation. This is distinct from a free-text
  description: a short, labeled list the model can scan. Traits should be vivid and culturally
  situated per the tone bible - "remembers the ocean," "imperial, despises wild magic,"
  "righteously hates goblins" - never generic.
- **Surfacing: the cast context view.** The `MagicContextView` builder includes each
  perceived entity's traits in its `PerceivedEntity` record. No new retrieval machinery -
  that is the entire point of entity-attachment. The same builder feeds the agent
  observation, so traits are inspectable.
- **Framing: the resolver preamble.** One short block in the provider system prompt (shared
  with dialogue later, from one source, so a trait means the same thing to both): the
  "weigh, don't apply" instruction from principle 4.
- **Minting: resolver-only.** An `addTrait` operation attaches a trait to a target entity.
  Free, no severity, no cost - it promises nothing. **Only the resolver emits it.** The player
  never authors traits directly; their sole influence is how they phrase a spell request,
  which the resolver may interpret into a trait - consistent with the engine-authoritative
  rule that the player proposes in natural language and the engine owns truth.
- **Mechanization and crystallization: ordinary operations.** Cashing a trait out, or
  promoting it, is just the resolver emitting `damage` / `addStatus` / a behavior tag, priced
  and validated normally.

The new surface area is small: a labeled traits channel, the prompt framing, and an optional
promotion affordance. Everything else reuses the pipeline.

## 6. Risks and how each is mitigated

| Risk | Why it bites | Mitigation |
|---|---|---|
| Illegibility / unfairness | A trait that fires only sometimes (model nondeterminism) breaks the player's mental model. | Principle 3 (never on critical path) + the lifecycle (crystallization makes load-bearing traits reliable and legible). Semantic stays delight; mechanical stays predictable. |
| Trait soup / context bloat | Over a long run, entities accumulate contradictory descriptors, bloating context and confusing the model. | Per-entity cap, newest-wins; optional periodic consolidation pass later. |
| Chekhov's whiff | A trait surfaced once and never again is wasted tokens and a quiet broken promise. | Entity-attached storage (section 2) re-surfaces traits whenever the entity is perceived. Resist the ambient-field version. |
| Over-firing / power creep | "Use traits to resolve" makes every descriptor a power. | "Weigh, don't apply" framing; cost-at-mechanization means firing is never free. |
| Contradiction with balance | Powerful outcomes are fine *with costs*, but semantic payoff is deferred and unpriced. | Cost-at-mechanization resolves this directly: the description is free; the cashed-out action is priced normally. |

## 7. Near-term scope (what to build first)

High-value, low-risk core, in order:

1. `TraitsComponent` on entities, capped, carried through pickup/equip/transform.
2. Traits surfaced in `MagicContextView` / `PerceivedEntity` (and therefore in the agent
   observation).
3. The "weigh, don't apply" framing block in the resolver preamble.
4. The validation probe (section 8) to confirm surfacing and framing actually land.

Deferred until the status/tag system exists: the `addTrait` operation as a first-class verb,
and Stage-2 crystallization. They are incremental and should wait for the probe result.

## 8. Cheap validation before building (the probe)

Do not build the whole thing first. One experiment tells us whether surfacing and the
"weigh, may-promote" framing land. Using the imperial encounter scenario:

1. Give the brass containment brazier a trait: `["imperial, despises wild magic"]`, and a
   dropped dagger a trait: `["righteously hates imperials"]`.
2. Cast something that addresses each - "wake the dagger and let it choose a foe" next to the
   ward-captain, and separately next to a neutral.
3. Observe whether the resolver (a) targets the captain and spares the neutral, and (b) ever
   chooses to crystallize the trait into a standing tag the engine then reads each turn.

If (a) works, entity-attached surfacing plus "weigh" framing is enough to ship steps 1-3
above. If (b) ever happens, the crystallization path is worth building out. If neither lands,
the problem is surfacing or framing, and we learned it for the price of one scripted scenario
instead of a subsystem. This belongs in the spell-eval corpus.

## 9. Settled decisions

1. **Traits are resolver-authored only - permanently.** The player can never directly author
   a trait. Their only influence is to *request* through spell text; the resolver decides
   whether that becomes a trait, exactly as it decides any other resolution. This keeps the
   engine authoritative and sidesteps the legibility trap of a player expecting a
   self-stamped trait to fire reliably. There is no player-facing trait-authoring command,
   now or later.
2. **Crystallization is the documented target but deferred.** The full
   semantic -> judgment -> crystallization lifecycle is the goal, but only semantic surfacing
   (section 7, steps 1-4) is built first. Stage-2 promotion lands once the status/tag system
   exists.
3. **Firing is generous but strictly off the critical path.** The model should translate
   traits into concrete effects readily, for wonder and mischief, matching the wonder-first,
   wildly-swingy power curve in [GRAND_VISION.md](GRAND_VISION.md) - but a semantic effect is
   never load-bearing and never gates progress.

## 10. One line to remember

**Semantic by default, mechanical on demand, never on the critical path.** That lets Sorcerer
be lavish with weird, specific, evocative content without making the game's reliability
hostage to it.
