# Magic Resolver Architecture

Wild magic is the crown jewel of Sorcerer. The resolver must make typed spell ideas feel
surprising, powerful, and mechanically real without letting the model own game truth.

## Core Rule

The model proposes a composition of engine operations. The engine normalizes, validates,
prices where supported, applies transactionally, and records the result.

```text
spell text
  -> cast context view
  -> provider
  -> raw JSON
  -> parse and repair
  -> normalize
  -> validate
  -> apply operations transactionally
  -> action result, deltas, audit
```

## Primary Failure Modes

The two biggest resolver failures are:

- bad LLM outputs
- boring magic

The architecture should make both visible. Every live cast should be auditable. Spell
evaluation should measure not only schema validity, but also whether resolutions are
specific, local, consequential, and interesting.

## Capability Cards

Sorcerer should preserve and improve the capability-card approach.

A capability card describes one family of magical expression:

- name
- triggers or routing text
- effect types it unlocks
- required context slices
- prompt block
- examples
- version
- audit tags

The resolver should receive:

- a compact always-on core
- a one-line index of available capabilities
- full detail for selected cards only
- a narrowed schema or operation list when practical

Routing should be recall-biased. Loading one extra relevant card is much less harmful than
missing the card that would make a spell work.

## Operation Registry

Operations should be first-class registered engine verbs.

Each operation should eventually own or point to:

- canonical name
- aliases
- JSON shape
- validator
- applier
- cost/pricing metadata, once pricing exists
- state delta shape
- prompt guidance
- examples
- required state context

This keeps magic extensible. Adding a new mechanic should mean adding one disciplined verb,
not editing scattered prompt strings, validators, docs, and handlers by memory.

Current implementation:

- C# operation classes own validation and application.
- `OperationRegistry.CreateDefault()` registers the code-owned operation set.
- `OperationCardLoader` searches upward from the current/app base directory for
  `content/operations` and attaches any matching data cards.
- Built-in operation cards remain the fallback when content files are absent.
- Data aliases from cards are exposed to provider context and can supplement resolver
  aliases, but exact operation names stay authoritative.

This means prompt guidance can improve without creating a second source of truth for
mechanics.

## Live Output Repair

Live local models often return semantically useful JSON in a dialect that is not quite the
schema. The parser should repair broad, reusable shape mistakes before validation, while
the engine still decides legality.

Current repairs include:

- operation keys: `type`, `operation`, `op`, `kind`, `effectType`, `effect_type`, and
  legacy `name` when no stronger operation key exists.
- target aliases: `targetId` and `target_id` become `target`.
- summon aliases: `entityName` and `entity_name` become `name`.
- compact effect ids such as `status_webbed_target_id:soldier_1...` and
  `message_text:...`.
- nested `details` fields folded into the effect when they provide names or descriptions.
- coordinate shorthand such as `{ "target": { "x": [4], "y": [5] } }` and
  `"target/x/y": [[4,5], ...]`.
- array-valued traits, so `trait: ["friendly"]` does not become `System.Object[]`.
- simple cost strings and numbers such as `"10 mana"` or `10`.

Status ids are canonicalized at the engine boundary. Flavor names that contain known
status aliases, such as `webbed_blue_webbing`, inherit the registered mechanics while
keeping the model's display phrase for messages.

Malformed provider shapes are technical failures, not in-world spell rejections. For
example, an unrecognized target object must not be stringified into a .NET type name and
treated as a missing entity; it should fail visibly, preserve audit evidence, and leave the
turn clock untouched. By contrast, a well-formed but impossible or overreaching spell may
still be rejected in-world and consume the cast turn.

## Engine Pricing

Full spell-budget auto-pricing is deferred.

Early Sorcerer should let the model propose costs and let the engine enforce:

- supported cost types
- target validity
- protected/treasured inventory rules
- impossible references
- hard safety clamps
- transaction rollback
- technical failure turn rules

Later, the engine may score spell power and paid costs. That should wait until the game has
enough feel to know what pricing should mean.

## Strong Spells

Overpowered spells should usually happen with severe costs rather than be rejected outright.

Examples of severe costs:

- major health loss
- max health loss
- max mana loss
- curse
- reputation exposure
- dangerous promise
- item sacrifice
- body change, only as an extremely severe outcome

Literal win buttons, infinite resources, and global rewrites can still be rejected, but the
default instinct should be "yes, at a price."

## Wildness

Wild magic should be unpredictable. Stronger requested spells can be more monkey-paw.

Backfires should generally become most likely when the player tries a powerful spell with
poor casting performance. See [CASTING_AND_MINIGAMES.md](CASTING_AND_MINIGAMES.md).

## Post-Cast Costs

Costs are normally revealed after casting. The resolver and engine should support the
feeling that the player reaches for power before knowing exactly what it will take.

The player may still hint in the spell text that they want to spend a specific resource.
That should remain organic.

## Provider Neutrality

Do not bake the resolver around one provider.

The provider interface should support:

- local model providers
- Ollama
- API-key providers
- mock providers
- future provider experiments

Provider-specific behavior belongs behind the provider interface. Engine rules should not
care where the JSON came from.

## Audits

Every live resolver call should record:

- provider and model
- request purpose
- spell text
- selected capability cards
- context slices
- prompt or compact prompt representation
- raw response
- repaired/normalized JSON
- validation errors
- final action result
- state deltas
- technical failure flag

Audits are not optional polish. They are how the team improves the most important system.
