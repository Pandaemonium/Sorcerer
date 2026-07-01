# Dialogue System

Dialogue is a first-class simulation system, not a flavor wrapper and not a
scripted story lane.

The rule is the same as wild magic:

> The model may speak for the NPC. The engine decides what becomes real.

Sorcerer should let the player talk naturally to almost anyone, hear answers
that feel specific to the person and place, and allow those answers to seed
real mechanics: claims, promises, memories, bonds, faction reactions, stock,
warnings, and opportunities. The first twenty minutes should sing because this
system is strong, not because the opening is hardcoded.

## Design Goals

- Let NPCs speak in character, with memory, mood, role, faction, body, local
  context, and relationship all visible to the dialogue model.
- Let the player improvise language instead of guessing keywords.
- Treat NPC-spoken concrete claims as possible world seeds, while never making
  player-spoken claims binding.
- Make high-salience dialogue mechanically useful without turning every line
  into a quest.
- Use the same engine pathways for opening NPCs, later villagers, enemies,
  merchants, prisoners, functionaries, witnesses, summoned beings, and body-swap
  victims.
- Keep GUI and CLI behavior identical by routing both through `GameSession` and
  the same dialogue backend.
- Keep latency humane: show the NPC's spoken response as soon as it is valid,
  then allow heavier proposal handling to apply between turns where appropriate.
- Keep the system auditable, replayable, and regression-testable.

## Non-Goals

- No keyword dialogue handlers such as "if the player says secret, create a
  secret."
- No direct world mutation from LLM output.
- No generic chatbot sandbox detached from the game state.
- No player-authored facts becoming true merely because the player stated them.
- No GUI-only or CLI-only dialogue affordances.
- No hidden quest, trade, or promise engine inside fallback prose.
- No LLM calls for ordinary trade intent. Trade should remain explicit engine
  interaction (`buy`, `sell`, `give`, `wares`, and related commands). Dialogue
  may mention goods or propose stock, but purchases and sale agreements are
  engine commands.

## Current Implementation Status

The first generated-dialogue slice is implemented.

`GameSession` owns an `IDialogueProvider` alongside wild-magic and claim
providers. When configured, `talk` now resolves a speaker through the engine,
builds a compact `DialogueRequest`, receives generated `spokenText` plus
structured proposals, validates the spoken line, applies accepted proposals, and
returns the result through the same GUI/CLI backend. Mock and Ollama dialogue
providers live in `Sorcerer.Llm`; provider technical failures do not consume
turns.

The older deterministic `InteractionSystem.Talk` path still exists as the
no-provider fallback and for tests. `IDialogueClaimExtractor` remains useful for
authored or deterministic text, but generated dialogue proposals are applied
directly and are not re-extracted a second time.

Implemented proposal handling covers claims/promises, memories, merchant stock,
bounded bond shifts, and the first concrete action proposals:
`step_aside`, `flee`, `call_help`, `give_item`, and `open_door`. Unsupported
actions are rejected with diagnostic deltas.

Live-model robustness is also implemented for the Ollama dialogue provider. The
provider preserves usable `spokenText` while normalizing common proposal shape
mistakes such as string actions (`"step_aside"`), alternate bond fields
(`trustDelta`/`trust`), float bond deltas, listener-targeted bond proposals, and
over-eager `playerAuthored` flags. The engine then applies another validation
layer: generated-dialogue and claim-extraction bond proposals share one
engine-side bond-apply helper, conversational deltas are dampened to ordinary
scale, missing bond targets produce structured skipped-proposal deltas, and
cooperative actions are rejected when the NPC response is a refusal.

The current keyword dialogue-intent parser is temporary fallback behavior. It
should not become the normal way to infer threats, confessions, bargains, or
trust.

## Lessons From WildMagic

WildMagic already proved several useful patterns:

- A provider stack for dialogue is valuable: mock for tests, local model for
  normal play, API-compatible providers later.
- Dialogue should have an audit log containing prompt, context, raw model
  output, parsed reply, errors, and provider metadata.
- The NPC should speak as one character only: no narration, no markdown, no
  stage directions, no omniscient exposition.
- The prompt should tell the model to answer the newest player message directly,
  use remembered provenance, avoid echoing the player, and keep replies short.
- Degenerate reply detection matters. Empty replies, player-message echoes, and
  repeated NPC lines should trigger one focused retry.
- Talk-to-anyone is worth preserving. Enemies and odd beings can receive lazy
  generated personas; creatures without speech can intentionally fail as a
  character response rather than as a technical failure.
- Displaying the NPC reply before later extraction or proposal work keeps the
  player reading instead of waiting.
- Post-dialogue lore extraction is a good fallback for authored text, books,
  signs, and deterministic mock output.

Sorcerer should change the shape of the main path:

- WildMagic mostly generated plain prose, then ran separate lore and trade
  structuring calls. Sorcerer should prefer one generated dialogue response with
  two lanes inside it: spoken text for the player and structured proposals for
  the engine.
- Trade should not copy WildMagic's LLM trade-intent path. Sorcerer already has
  a clear rule: explicit trade commands are engine-side; dialogue can create
  context, not execute bargains.
- Provider fallback must not quietly invent fake facts. If the model fails, the
  action result should report a technical failure and avoid consuming a turn.

## Core Flow

1. The player issues a `talk` command in the GUI or CLI.
2. The engine resolves the target entity and builds a `DialogueRequest`.
3. The dialogue provider generates a `DialogueResponse`.
4. The engine parses, repairs, normalizes, and validates the response.
5. The spoken text is reported to the player.
6. Valid proposals are applied transactionally or queued for between-turn
   handling, depending on their cost and risk.
7. The exchange is recorded as dialogue history and memory.
8. The action result reports whether a turn was consumed, which proposals were
   accepted, which were rejected, and what claims/promises entered the ledger.

Technical failures should not consume a turn. Intentional character responses
do consume a turn: refusal, evasion, silence, threats, lies, panic, or "I will
not speak to you" are all successful dialogue outcomes.

## Generated Dialogue Response

The main generated dialogue path should return structured JSON. The spoken line
is still just prose; the surrounding fields are the model's proposals, not
authority.

Example shape:

```json
{
  "spokenText": "Old Maren keeps a red-glass knife under the chapel floor. I saw her wrap it in lambswool after the bell cracked.",
  "delivery": "hushed",
  "intent": "confide",
  "proposals": {
    "claims": [
      {
        "kind": "item",
        "subject": "red-glass knife",
        "text": "Old Maren keeps a red-glass knife under the chapel floor.",
        "sourceEntityId": "npc_prisoner_01",
        "confidence": 0.72,
        "salience": "high",
        "status": "reported",
        "tags": ["secret", "weapon", "chapel"]
      }
    ],
    "memories": [
      {
        "ownerEntityId": "npc_prisoner_01",
        "text": "Confided to the sorcerer that Old Maren hid a red-glass knife under the chapel floor.",
        "provenance": "conversation",
        "salience": "high"
      }
    ],
    "bond": {
      "entityId": "npc_prisoner_01",
      "trustDelta": 1,
      "fearDelta": 0,
      "reason": "The player offered help and the prisoner chose to confide."
    },
    "actions": []
  }
}
```

The exact schema can evolve, but the separation is important:

- `spokenText` is what the player hears.
- `proposals` are candidate engine operations.
- The engine can accept, clamp, transform, defer, or reject each proposal.

The current C# shape uses `DialogueClaimProposal` for claim proposals, with
separate `DialogueMemoryProposal`, `DialogueBondProposal`, and
`DialogueActionProposal` records.

## Request Context

The dialogue request should be compact but rich enough for the model to make
good choices. It should include:

- Speaker entity: id, name, role, faction, body, tags, current status, inventory
  summary, speech capability, and visible temperament.
- Listener entity: controlled body, appearance, known reputation, visible
  equipment, active status, recent magic, and recent deeds known to the speaker.
- Relationship: bond values, faction standing, hostility, fear, gratitude,
  debts, gifts received, and recent interactions.
- Scene: region, local culture, current tile or room, visible entities, visible
  items, nearby landmarks, danger state, imprisonment or combat context.
- Memory: firsthand memories, overheard claims, gossip, previous conversation,
  gift memories, claim-ledger references, and promise references with
  provenance clearly labeled.
- Conversation history: the last few exchanges with this NPC, clipped for
  budget.
- Capability cards: which proposal types are allowed in this call, with compact
  schemas and limits.
- Current constraints: player claims are not binding, model output is untrusted,
  do not invent unsupported engine operations, answer the latest message.

The request builder belongs behind the engine boundary. Renderers should not
assemble prompt context directly.

## Proposal Types

Dialogue should start with a small set of general proposal types and grow only
when a new type unlocks many interactions.

### Claims

Claims are NPC-authored statements that may become entries in the `ClaimLedger`.
They can be true, false, mistaken, exaggerated, or incomplete. They are useful
because the world can later redeem, contradict, or reincorporate them.

Initial claim kinds:

- `place`
- `person`
- `item`
- `threat`
- `faction`
- `landmark`
- `event`
- `custom`

Player-spoken claims should be context only. If the player says "your niece
Nannerl is in the south town," that does not create Nannerl. If the NPC replies
"Yes, Nannerl fled south to Bellweather," the NPC's reply can create a claim.

### Memories

Memories are local, durable facts attached to entities. Gifts should create
memory immediately. Bond changes should be model-proposed during dialogue, using
recent gifts and behavior as context.

Useful memory provenance values:

- `conversation`
- `gift`
- `witnessed_magic`
- `combat`
- `deed`
- `rumor`
- `faction_order`
- `body_swap`

### Bonds

Bond changes should be proposed by dialogue, not hardcoded into the `give`
command alone. Giving still creates a memory right away; later conversation can
turn that memory into trust, fear, admiration, resentment, debt, or devotion.

Bond deltas must be bounded and validated. A single ordinary exchange should
not swing a relationship wildly unless the context is extraordinary.
Generated-dialogue bond proposals and delayed claim-extraction bond proposals
should share the same clamp, entity resolution, mutation, visible-message, and
skipped-proposal behavior.

### World Actions

Dialogue can propose immediate outcomes, but it should not own private mechanics
for those outcomes. It should emit the same small world-action grammar that
player commands, AI, magic, prophecy, and background systems can eventually
share:

- `actorId`: who is trying to do the thing.
- `verb`: the ordinary engine verb being requested.
- `targetId`: the target entity or location, when one exists.
- `parameters`: typed supporting fields such as item name, quantity, service, or
  claim category.
- `source`: dialogue, player command, magic, AI, prophecy, or background
  generation.
- `reason`: optional model-facing explanation for audits, never direct
  authority.

Dialogue therefore does not "open doors" as a special case. It requests an
`open` action by an NPC actor against a door target. The engine then applies the
same reachability, lock, ownership, consequence, promise, deed, message, and
audit rules that it would apply if the player, AI, or magic requested the
action.

Early outcome verbs:

- `none`
- `step_aside`
- `flee`
- `call_help`
- `attack`
- `give_item`
- `open`
- `create_promise`
- `offer_trade`
- `reveal_service`
- `mark_location`

Each action must pass ordinary preconditions. An NPC cannot give an item they do
not have or open a door they cannot reach. `create_promise` still passes
through the claim/promise validators; `offer_trade` reveals stock or services
for later explicit commands rather than completing a trade inside dialogue.
`step_aside` and `flee` use ordinary entity movement checks. `call_help`
currently schedules a modest help response: imperial callers can schedule an
`empire_patrol`, while non-imperial calls schedule a generic help-call message.

Implementation can migrate one verb at a time. `open` is actor-aware already:
player `open`, dialogue `open_door`, and future magic/AI open requests flow
through one engine action, with the source controlling turn consumption and
audit wording rather than world truth. Promise payoff also has a shared first
path now: travel, talk, read, open, and examine/inspect all route concrete
promise realization through `PromiseRealizationSystem`.

### Stock And Services

NPC dialogue may propose that an NPC gains stock or reveals available services.
This is not a completed trade. It is a state update that makes later explicit
commands possible.

Examples:

- "Jimmer can sell you a fine blade" can add or reveal a blade in Jimmer's
  stock. If no valid merchant exists yet, it can bind as a future
  `merchant_stock` promise and realize through travel as a merchant carrying
  that ware.
- "The midwife keeps fever-salt" can create an item claim or service claim.
- "I can mend that cloak" can reveal a service if the NPC's role supports it.

Folk-magic services are practiced, but they are hush-hush. The dialogue model
may reveal them through trust, fear, leverage, rumor, or coded speech, but
characters should understand that Vigovia can execute people for practicing
them. Once implemented mechanically, services should use the same validated
effect/action machinery as wild magic rather than becoming a separate hidden
spell engine.

## Claim And Promise Relationship

The claim ledger is the broad memory of what was said. Promises are the subset
of claims that the world should try to redeem into content.

The default path:

1. NPC says something concrete.
2. The dialogue response proposes one or more claims.
3. The engine accepts high-salience claims into the `ClaimLedger`.
4. Some claims become promise candidates.
5. The promise system realizes candidates organically when the world has room:
   a person appears, a town is generated, a landmark is placed, a merchant
   carries promised stock, an item is added, or a threat enters a region.
6. The original dialogue remains attached as provenance.

The system should err toward "yes, and" for NPC-authored claims, but not to a
ridiculous degree. Salience, specificity, fit, and current world pressure should
decide whether a claim becomes a promise.

Dialogue prompt guidance should use this practical threshold:

- Bind major actionable NPC claims as promises when they name a useful later
  place, person, landmark, item location, merchant stock, service, future threat,
  escape route, or prophecy.
- Bind concrete useful NPC-authored claims even when they are reported as rumor,
  spoken by a cautious NPC, attached to the current speaker's own service, or
  presented as a warning rather than a quest offer.
- If the model creates a salience 3+ claim in an actionable category such as
  `site`, `town`, `landmark`, `person`, `item`, `merchant_stock`, `service`,
  `trade`, `threat`, `escape_route`, `prophecy`, or `door_rule`, it should bind
  by default unless a do-not-bind rule applies.
- Every claim must be plainly supported by the NPC's `spokenText`. The model
  should not add useful places, people, items, or threats that the NPC did not
  actually say.
- Use `playerVisible: true`, `bindAsPromise: true`, salience 3-5, and a
  practical `realizationKind` such as `site`, `town`, `landmark`, `person`,
  `item`, `threat`, `service`, or `quest`.
- Use a concrete `triggerHint` such as `travel`, `talk`, `buy`, `trade`, `open`,
  `wait`, or `inspect`.
- Do not bind denials, obvious jokes, child-invented monsters, tiny ambience,
  ordinary weather, vague mood, insults, impossible boasts, or claims authored
  only by the player.

Pattern examples that should bind. Bracketed terms are placeholders, not canon
facts to copy:

- "`<merchant>` can sell `<item>`."
- "`<elder>` has a niece named `<person>`."
- "`<person>` tends the fever-sick in `<place>`."
- "`<landmark>` marks `<road>`."
- "I can mend `<item>` for `<price>`."
- "There is a hidden tunnel behind `<fixture>`."
- "`<authority>` keeps `<key>` in `<container>`."
- "`<healer>` keeps `<medicine>`."
- "If you linger, `<threat>` will come."
- "`<door>` opens only to `<condition>`."

Prompt examples are shape examples, not canonical facts. The model should invent
claim details only from the current speaker's line and context.

Examples that should not bind:

- "There is no chapel here."
- "The pantry monsters are imaginary."
- "It will rain tonight," unless weather is a real mechanical pressure.
- "Everyone north of here hates you," unless it names a concrete threat,
  faction, or place.

## Latency Model

The player should never wait on avoidable follow-up work before seeing the NPC's
line.

Recommended behavior:

- Generated dialogue call: foreground. The spoken line depends on it.
- Parse/repair/validate spoken text: foreground and fast.
- Display spoken text: immediate after validation.
- Apply simple proposals: same action if already parsed and valid.
- Heavy realization: between turns or background queue.
- Claim extraction for authored or fallback text: between turns.
- `save` is a synchronization point: if claim extraction or dialogue proposal
  work is pending, the session should wait for it to complete, apply accepted
  results or record technical failures, and only then write the save.
- Audit and eval logging: asynchronous where possible.

Use the same loaded model as ordinary dialogue by default to avoid local model
thrash. Separate provider purposes can exist, but the default local setup should
not unload one model just to run a tiny router.

Pending extraction tasks should not be serialized. Saves should contain the
durable result of completed dialogue work, not live provider tasks. If an
extractor fails while a save is waiting on it, the failure should be recorded as
an audit/debug delta before saving.

## Provider Architecture

Core interfaces should live where `GameSession` can depend on abstractions
without depending on provider implementations.

Suggested interfaces:

- `IDialogueProvider`
- `IDialogueClaimExtractor` (already present conceptually)
- `IDialogueAuditSink`

Suggested implementations:

- `MockDialogueProvider`: deterministic, schema-correct, good for tests and
  agent playthroughs.
- `OllamaDialogueProvider`: local model path, JSON response, retry on degenerate
  output.
- API-compatible providers later, behind the same interface.

The provider should receive a purpose such as `dialogue`, but foreground
dialogue and follow-up extraction should default to the same configured local
model unless the user opts into separate models.

Current dialogue action results include provider, raw output, delivery, intent,
and generated/fallback markers on the `dialogue` delta. CLI and Godot wire
`JsonlDialogueAuditSink` to `logs/dialogue_audit.jsonl`; the audit entry records
the request, raw provider output, parsed response, validation issues, action
result, and delta operation names.

## Validation Rules

Dialogue output is untrusted. Validation should cover both the spoken line and
all proposals.

Spoken text validation:

- Non-empty.
- Reasonable length.
- No markdown, JSON leakage, or stage directions.
- Does not echo the player message.
- Does not repeat the NPC's previous line.
- Speaks as the target NPC only.

Proposal validation:

- Proposal type is known and allowed by current capability cards.
- Entity references resolve or are intentionally new promise subjects.
- Player-spoken claims are not converted into truths.
- Claim salience, confidence, and status are normalized.
- Bond deltas are bounded.
- Memories have an owner and provenance.
- Actions pass normal engine preconditions.
- Stock updates match an NPC, role, faction, or claim capability.
- Rejected proposals do not mutate state.

The accepted result should be auditable: raw proposal, normalized proposal,
accepted/rejected status, reason, and resulting operation ids.

## Turn Semantics

- Successful dialogue consumes a turn.
- Character refusal, evasion, lying, or silence consumes a turn.
- Technical provider failure does not consume a turn.
- Invalid JSON after retry is a technical failure.
- Valid spoken text with rejected proposals still consumes a turn.
- Between-turn claim realization should happen after the action result is
  visible and before the next observation where feasible.
- Save waits for pending dialogue extraction/proposal work and snapshots the
  post-application state.

## Opening Sequence Use

The opening should use dialogue as a pressure test for the general system.

Useful opening NPC patterns:

- A frightened prisoner who may confide a person, item, or escape route.
- A goods-owner whose stock or missing object can become real later.
- A functionary who speaks in lawful abstractions but reveals a place, office,
  ledger, or faction procedure.
- A witness who describes a landmark, omen, monster, or local scandal.
- Someone who reacts differently if the player gives them something first.

These are not scripted quest givers. They are entity profiles plus context,
memories, local culture, and allowed proposal cards. If their dialogue creates
claims, the promise system should decide when and how the world pays them off.

## Implementation Slices

1. Define the dialogue contract: request, response, proposals, validation
   results, and audit record. Done.
2. Build the context builder from engine state only. Done for the first compact
   request shape.
3. Add a deterministic `MockDialogueProvider`. Done.
4. Add parser, repair, normalization, and validation tests. First slice done.
5. Wire `talk` through `GameSession` for both GUI and CLI. Done.
6. Add action-result fields for accepted and rejected dialogue proposals. Done
   through deltas.
7. Add `OllamaDialogueProvider` with audit logging and degenerate reply retry.
   Done.
8. Integrate accepted claim proposals with the existing `ClaimLedger`. Done.
9. Keep `IDialogueClaimExtractor` for authored text, books, signs, and
   fallback/mock dialogue where no generated proposal was returned.
10. Add validated handlers for concrete dialogue action proposals such as
    `step_aside`, `flee`, `call_help`, `give_item`, and `open_door`. First
    handlers are done.
11. Add opening NPC profiles that rely on the same system as the rest of the
    game.
12. Add live-model eval transcripts for directness, specificity, promise
    quality, and latency.

## Tests And Evals

Focused tests should cover:

- CLI and GUI parity through shared `GameSession` behavior.
- Talk target binding and invalid targets.
- Technical failure does not consume a turn.
- Character refusal consumes a turn.
- Spoken text is displayed even when proposals are rejected.
- Player-authored claims are not binding.
- NPC-authored high-salience claims enter the ledger.
- Duplicate claims are merged or suppressed.
- Gifts create memory immediately.
- Dialogue can propose bond changes using recent gift memory.
- Dialogue action proposals obey normal preconditions.
- Promise realization preserves dialogue provenance.
- Save/load preserves conversation history, memories, claims, and promises.
- Saving immediately after dialogue waits for pending extraction, applies or
  records the result, and does not lose claims on reload.
- Replay can use recorded dialogue outputs.

Live-model evals should score:

- Direct answer to the player's latest line.
- Character voice and cultural specificity.
- Non-echoing behavior.
- Appropriate claim salience.
- Low hallucinated mechanics.
- Interesting but coherent promises.
- Latency from command to visible spoken text.

## Open Design Questions

- Should generated dialogue always return JSON, or should plain text plus a
  follow-up extractor remain configurable for very small local models?
- How many previous exchanges should be kept per NPC before summarization?
- Which NPC action proposals should be allowed in combat?
- Should the temporary keyword dialogue-intent parser survive as a no-provider
  fallback, or be deleted once generated dialogue is stable?
- Should bond deltas be hidden from the player, summarized diegetically, or
  exposed in debug views only?
- How aggressively should duplicate claims from different NPCs corroborate each
  other?
- Should lies be explicit model proposals, or should the engine treat claim
  confidence/status as enough?

The recommended starting answer is conservative: one JSON response for generated
dialogue, a small proposal vocabulary, strict validation, visible audit logs,
and broad use in the opening so improvements compound across the whole game.
