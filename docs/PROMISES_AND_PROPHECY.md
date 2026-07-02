# Promises And Prophecy

Promises and prophecy should be foundational in Sorcerer.

A promise is an engine-owned future obligation. It may begin as a prophecy, rumor, quest,
threat, debt, bargain, omen, or claim. The model can help phrase and interpret it, but the
engine decides whether it binds, where it can realize, and when it becomes visible.

## Why This Matters

The promise system is one of the clearest ways Sorcerer fulfills its central pitch:

> A game where you can do anything, but it actually matters what you do.

A spell can say:

```text
cast somewhere north, a blade waits with my name on it
cast in three turns a debt collector arrives because I stole tomorrow
cast let this shrine remember my mercy
```

The result should not be only flavor. The world should gain a structured future hook.

## Promise Sources

Promises can come from:

- wild spells
- NPC dialogue
- quests
- reading and examining
- world generation
- faction consequences
- background generation
- player deeds

All promise sources should flow into one ledger.

## Claims Before Promises

Dialogue and documents often create **reported claims** before they create promises. A claim is
something a character, record, rumor, or object says about the world:

```text
There is a town just south of here.
Old Maren has a niece named Nannerl.
Jimmer can sell you a fine blade.
The stone road refuses patrol boots after sunset.
```

The system should err toward "yes, and" by preserving plausible claims, but not to a ridiculous
degree. Most claims should first become memories, canon notes, or rumor records with provenance:
who said it, who heard it, where, when, and with what confidence. Only some claims become
engine-owned promises.

The distinction matters:

- A **reported claim** is useful context. It can color dialogue, appear in the journal, guide
  generation, or become evidence later, but the world has not sworn to make it true yet.
- A **bound promise** is an obligation. The engine has accepted that the world must deliver
  something buildable later.

Player-spoken claims are not binding world truth. A player saying "there is a palace under this
floor" during ordinary dialogue may influence the conversation, but it must not create a palace
unless backed by wild magic, a bargain, a validated engine action, or another authoritative source.

## Dialogue Claim Extraction

Organic dialogue should feed the promise system through a post-dialogue claim extractor. The player
should receive the dialogue immediately; claim extraction happens while they are reading and can
apply between turns. This keeps conversation responsive while still letting the world notice
important assertions.

The target pipeline:

1. The foreground dialogue command resolves and returns player-facing text.
2. A low-context **claim router** call runs using the same dialogue model already loaded for the
   conversation. It receives only the player/NPC exchange, compact speaker cards, and a short
   definition of claim/promise. It returns whether any mechanically relevant claim was made,
   claim spans, rough categories, confidence, and which claim capability cards are needed.
3. If the router says yes, a larger **claim structuring** call runs with the selected capability
   cards, compact world context, and strict JSON formats. It proposes structured claim records,
   memory writes, canon notes, merchant-stock hints, person/site/item promises, or rejection/no-op.
4. The engine validates and applies proposals at an explicit apply point. The model never mutates
   state directly.
5. The player may see a light follow-up such as "Lio's words settle into your journal" or "A rumor
   finds a place to stand." Debug/agent views expose the exact records.

Use the `dialogue` purpose for both router and structuring by default. Loading a separate tiny
model for the router risks local-model thrash and can cost more latency than it saves. Separate
purpose settings can be introduced later only if playtesting shows a real benefit.

Claim extraction should be broad but conservative about binding:

- "Old Maren has a niece named Nannerl" usually writes an NPC memory/canon claim and may reserve a
  future person only if the source is trusted, salient, and the world has a buildable person lane.
- "Jimmer can sell you a fine blade" should normally become a merchant-stock claim or promise. If
  Jimmer already exists as a merchant, the engine may add or reserve stock through ordinary item
  systems. If Jimmer is not present, it should bind a future merchant/stock promise rather than
  silently create a blade now.
- "There is a town just south of here" can bind as a site/town promise when it has a plausible
  spatial hint and capacity in generation.
- Vague, poetic, joking, contradictory, or low-confidence claims stay as flavor or memories.

The general rule is to choose the narrowest authoritative state that preserves the claim. A memory
is cheaper than a promise; a stock reservation is cheaper than a generated site; a reported rumor is
cheaper than a guaranteed truth.

Current implementation:

- `ClaimLedger` records reported dialogue claims with source, speaker, listener, subject,
  category, salience, confidence, status, visibility, tags, and optional promise/application ids.
- `GameSession` queues post-dialogue extraction and applies completed results on a later command,
  so the dialogue turn returns immediately.
- `save` should flush pending dialogue extraction first: wait for queued extraction to complete,
  apply accepted proposals or record technical failures, and only then snapshot durable state.
- `Sorcerer.Llm` provides a deterministic mock extractor and an Ollama extractor with the
  router-then-detail flow on the `dialogue` purpose.
- Structured proposals may record memories, bind promises, add stock to an existing merchant, or
  request an engine-clamped bond shift. Gift actions write memory only; any bond change from a
  gift must be inferred and proposed during later dialogue.
- Generated NPC speech now flows through `IDialogueProvider` when configured. Generated dialogue
  applies structured claim proposals directly; the extractor remains for fallback deterministic
  dialogue and future authored text.

## Binding

The engine may refuse to bind a promise that is too vague, too broad, contradictory, or
mechanically unusable.

Examples:

- "something interesting should happen someday" can remain flavor.
- "a silver door opens east of here after I betray a friend" can bind.
- "all empires everywhere fall instantly" should reject or become a severe local omen.

Binding should consider:

- promise kind
- subject
- spatial hint
- trigger condition
- reward or threat
- salience
- confidence
- whether the world has room to realize it

Current implementation records both what was claimed and what the engine committed to:
`source`, `subject`, `claimedPlace`, `boundPlace`, `boundTargetId`, `triggerHint`,
`realizationKind`, and `realizedIn`. The `createPromise` and `addCurse` operations may
provide an explicit `target` plus `trigger`/`triggerHint`; otherwise the engine can bind
to the selected target, infer an anchor from nearby entity names/tags, or bind some
promise kinds to the current region.

Entities can carry `PromiseAnchorComponent`, which stores all promise ids attached to
that entity. This keeps promise realization object-owned without making promises a
renderer concern.

## Always-Honor (Yes-And)

The promise economy is pure yes-and: **not everything an NPC or spell says becomes a promise,
but every promise the world makes, it keeps.** The quality gate lives *upstream*, at binding -
once something is a bound promise, the world is obligated to deliver it. This is what makes the
world feel coherent rather than random: the player learns that a bound omen always means
something.

**Keep what was said separate from what the engine chose.** Record the claimed space and shape
(what was said) apart from the bound space and shape (what the engine committed to), so when
they differ it is narratable, never a silent rewrite. If the named place is already taken or
explored, **relocate rather than break** - "the chapel is further north than they said" - with
the relocation visible in the record.

**Honor what you can build; the rest stays talk.** A claim binds only if it maps to a buildable
realization archetype (a site, an NPC, an item, a threat, an event). No archetype match means
it stays flavor lore - it still colors dialogue, it just never realizes. This is how
always-honor stays sane: the world only promises what it can actually deliver.

**Binding guardrails (lessons from live runs).** Match the claimed thing against whole words in
the subject and tags, never against free prose (or a temple keeper's stray philosophy mints
phantom shrines). A non-quest claim should bind only if it names a buildable thing or carries a
real spatial hint; vague or low-confidence chatter stays flavor. Player-asserted claims are
never silently captured as world truth.

**Reported claims are allowed to be wrong or incomplete.** A character can be mistaken, lying, or
speaking from old information. A bound promise should still be honored, but a mere reported claim
can later be contradicted, reinterpreted, or corrected by the world. Preserve provenance so the
game can say "Lio believed this" rather than silently converting every NPC sentence into fact.

**Cost scales with binding strength.** Vague color promises are cheap; a guaranteed item, ally,
or threat is major magic. A prophecy spell that writes a concrete future obligation should
carry an engine cost floor on top of the resolution's own costs - writing the world a debt is
powerful, and powerful magic is paid for.

**Quests and debts are promise-kinds, not separate systems.** A quest is a promise with an
objective and reward; a debt is the world committing to a future collection. One ledger, one
lifecycle. Quest reservations are never the ones dropped when space is tight - an objective
that never realizes is an impossible quest.

## Visibility

Promises should feel mysterious to ordinary players.

The player may see:

- a `Lead:` journal line for high-salience actionable promises
- a journal omen
- a rumor
- a partial phrase
- a marked map hint
- an NPC warning
- a dream

Developer tools and agent debug observations may expose perfect structured promise state.
This is acceptable because agents are expected to use debug state for playtesting.

Not every visible claim should become a journal lead. A concrete promise such as
"there is a magical sword in a burned-out oak tree north of here" belongs in
leads. A small relationship fact such as "Ricky has a brother named Taylor"
should usually remain memory/claim context unless Taylor becomes actionable.

## Promise Types

Initial types:

- prophecy
- rumor
- quest
- threat
- debt
- bargain
- omen

Types are not hard genre boxes. They are engine routing hints for how the promise can bind,
realize, and display.

## Realization

A promise may realize as:

- an entity
- an item
- a site
- a room feature
- an NPC memory
- a faction reaction
- a delayed event
- a quest objective
- a hazard
- a reward
- a message or dream

Realization should happen at explicit engine-controlled apply points, never from arbitrary
background worker mutation.

Dialogue claim extraction may complete between turns, but its durable output must still apply
through the same authoritative session/engine boundary as other background results. A completed
extractor can write memories, canon, merchant-stock reservations, or promises only when the engine
validates and applies the structured proposal.

Current apply points include:

- `travel`: realizes region-bound promises as generated sites, items, people, or threats.
- `read`: realizes matching promises anchored to the readable entity.
- `open`: realizes matching promises anchored to the opened door, then applies normal
  door consequences such as the first prisoner rescue.
- `talk`: realizes matching promises anchored to the spoken-to actor.
- `inspect`/`examine`: realizes matching promises anchored to the examined entity without
  consuming a turn.

The first shared realization service is `PromiseRealizationSystem`. Travel generation and
anchored interaction payoffs route through it so promise realization has one authoritative
engine-side path for trigger matching, status updates, concrete payoff creation, and deltas.

Current concrete realization archetypes are deliberately small but real:

- `memory`: writes a shareable memory to the world memory ledger and to the anchored entity.
- `threat`: spawns a hostile promised claimant near the anchor when space allows.
- `item`: creates a tangible promised item near the anchor.
- `merchant_stock`: adds stock to an existing merchant when possible, or realizes a
  travel-bound promise as an ordinary merchant entity carrying the promised ware.
- `quest`, `site`, and other omens: write durable canon records or generated site anchors,
  depending on whether they realize through an entity interaction or travel generation.

Trigger hints are intentionally simple. Empty hints can realize at the first matching
anchor interaction, while hints such as `read`, `open`, `door`, `talk`, `name`, `inspect`,
or `examine` route
the promise to an ordinary verb. This is not a full quest system yet; it is the first
engine-owned bridge from wild narrative claim to durable typed consequence.

## Agent And Debug Surface

The agent/debug view should include:

- promise id
- type
- status
- subject
- spatial hints
- trigger hints
- bound location, if known
- bound target id, if known
- realization state
- realization kind
- player-visible text

The normal GUI can be more mysterious, but the backend state must be inspectable.
