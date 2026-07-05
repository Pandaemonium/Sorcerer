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

The routed generated-dialogue slice is implemented.

`GameSession` owns an `IDialogueRouter`, `IDialogueProvider`,
`IDialogueParserRouter`, and `IDialogueParser` alongside wild-magic providers.
The generated-dialogue path is four steps: a short context-router selects
NPC-knowledge-gated context cards, the foreground generator speaks as the NPC
without mechanical schemas, a post-speech parser-router decides whether detailed
parsing is useful, and the parser extracts supported mechanical proposals from
the spoken reply. Mock, Ollama, and OpenAI-compatible dialogue
routers/providers/parsers live in `Sorcerer.Llm`;
provider technical failures do not consume turns. [Dialogue Context Routing](DIALOGUE_CONTEXT_ROUTING.md)
defines the router/card layer for current-zone context, full rumors, focused
object detail, relationship memory, services, faction law, travel context, and
parser capability cards.

The older deterministic `InteractionSystem.Talk` path still exists as the
no-provider fallback and for tests. `IDialogueParser` is now the post-speech
mechanical lane: generated speech without a `proposals` envelope queues it after
the player-visible response, and the parser may emit the same
`DialogueProposalSet` grammar as legacy proposal-bearing dialogue. Existing
`IDialogueClaimExtractor` implementations are adapted into that parser lane for
compatibility. Legacy/replay/provider responses that still include an explicit
`proposals` envelope are applied directly and are not parsed a second time,
which preserves transcript compatibility while the system migrates.

Implemented proposal handling covers claims/promises, memories, merchant stock,
bounded bond shifts, and the first concrete action proposals:
`step_aside`, `flee`, `call_help`, `give`, `open`, `attack`,
`recruit`, `create_promise`, `offer_trade`, `reveal_service`, `mark_location`, and
`spawn_fixture`, plus a generic `consequence` action that carries `consequenceType`
and a typed `consequencePayload` for local effects already owned by
`WorldConsequenceApplier`; non-immediate timing schedules one delayed consequence
through the shared turn pump. Legacy aliases such as `give_item`, `open_door`, and
`promise` still accepted. Unsupported actions are rejected with diagnostic
deltas.

The shared typed consequence-grammar slice is implemented in
`WorldConsequence`/`WorldConsequenceApplier`, exposed through
`GameEngine.ApplyConsequence`. Dialogue claim records, dialogue memory
proposals, automatic exchange memories, dialogue bond proposals, claim-extraction bond proposals,
merchant-stock claim payoffs, and a first set of immediate tactical effects
(damage, healing, mana, movement, terrain, statuses, spawns, promises, and
messages) now submit typed consequences instead of mutating those ledgers or
tactical primitives directly. General state changes for inventory, tags/traits,
faction allegiance, controller/AI policy, world flags, scheduled events, triggers, transformations,
damage resistance/weakness, delayed incoming damage, status acceleration, memory
edits, persistent combat hooks, behavior tags, tile flows, rumor records/updates,
faction standing/resource adjustments, legend tags, and canon records also use
the same applier, so dialogue, magic, services, world reactions, promise
payoffs, books, and future AI/faction plans can share one validated mutation
lifecycle. The old dialogue proposal records remain the provider-facing schema
for now; internally they are normalized into source-agnostic typed consequences
before mutation. New dialogue side effects should prefer the generic
`consequence` action when an existing consequence type already fits, instead of
adding another dialogue-only action helper. Non-immediate `timing` schedules the same
typed consequence through the shared scheduled-event pump; dialogue should use
specialized schedule/trigger consequences only when it needs a broader event,
repeating ward, or other shape beyond one delayed consequence.

NPC wants are also in the first implementation slice. `WantComponent` gives a
notable NPC one active desire with salience, stakes, and tags. Authored notable
NPCs, including opening figures and late-game figures like Emperor Odran, have
authored wants; generated residents receive deterministic region-shaped wants at
instantiation; promise-generated people, merchants, and service providers receive
promise-shaped wants through `spawn_entity` payloads. If a typed `spawn_entity`
creates a notable NPC without an explicit want, the applier synthesizes a default
want from faction, roles, tags, interactable verbs, promise anchors, and AI policy;
systems can pass `autoWant: false` when a spawned actor should remain creature-like
or intentionally blank. Dialogue participant cards include an active-want summary
so the generator has direction without a scripted quest path. The parser may
also propose one `want` update when the NPC's spoken reply shows their own
active desire was materially satisfied, blocked, or redirected; `GameSession`
applies that through the shared `update_want` consequence. Dialogue want updates
opt into a hidden `record_memory` child delta, so the NPC remembers why the desire
changed without making the want itself player-facing truth. Bounded world-turns may also emit a
private `want_stir` memory for one high-salience active want; generated dialogue sees it only as
ordinary recent memory, not as a special quest directive.

Rumors are now another request-context lane. High-salience visible dialogue
claims mint durable `RumorRecord`s through the shared `record_rumor`
consequence. Rumor propagation and later distortion update those records through
`update_rumor`; propagation into a local NPC also writes a hidden
`record_memory` consequence so the rumor becomes durable personal context.
Failed propagation rolls back the rumor update and heard-memory packet with hidden
`rumorPropagationSkipped` audit context.
Local propagation prefers carriers whose active wants, tags, faction roles, or
profile text match the rumor, keeping transport deterministic while making later
dialogue more likely to surface what that NPC would actually care about.
Generated dialogue requests include `RecentRumors` that the
current speaker or region plausibly carries. Rumors are reported stories, not
authoritative truth; the model may cite or color speech with them, but any new
concrete fact still has to appear in `spokenText` and be validated as a
claim/promise/consequence. World-reaction deeds can also write hidden
`deedWitnessMemory` records for NPC witnesses; these enter generated dialogue as
ordinary recent memories with `deed:<id>` provenance, so an NPC can later discuss
what they saw without a bespoke witness dialogue path.

Live-model robustness is also implemented for legacy proposal-bearing dialogue
responses. The provider preserves usable `spokenText` while normalizing common proposal shape
mistakes such as string actions (`"step_aside"`), alternate bond fields
(`trustDelta`/`trust`), float bond deltas, listener-targeted bond proposals, and
over-eager `playerAuthored` flags. The engine then applies another validation
layer: generated-dialogue and parser/extraction bond proposals share one
engine-side bond-apply helper, conversational deltas are dampened to ordinary
scale, rejected generated memory/bond/want proposals produce structured
skipped-proposal audit deltas alongside the raw `worldConsequenceRejected`
child, and cooperative actions are rejected when the NPC response is a refusal.
Dialogue memory proposals mark their `record_memory` consequence as requiring a
live owner entity, so the broad memory ledger can still store abstract world
memories while NPC-authored personal memories cannot silently attach to missing
actors.
Direct `create_promise` actions also repair unresolved natural-language
`target` values into promise hints rather than treating them as hard entity ids;
resolved entity ids still become anchors.
Deterministic fallback threats stage the fear/resentment `update_bond` and
owner-required `record_memory` as one packet, rolling back with a hidden
`threatDialogueSkipped` audit if either child rejects. Recruitment routes
relationship changes through `update_bond`, but ordinary fallback prisoner
dialogue only records the exchange as memory; it does not hardcode a bond shift.
Recruitment and prisoner rescue route follower control through `update_control`;
and gift, claim, threat, and recruit memories route through `record_memory`.

The current keyword dialogue-intent parser is temporary fallback behavior. It
should not become the normal way to infer threats, confessions, bargains, or
trust.

## Lessons From WildMagic

WildMagic already proved several useful patterns:

- A provider stack for dialogue is valuable: mock for tests, local model for
  normal play, and API-compatible endpoints for hosted or alternate local models.
- Dialogue should have an audit log containing prompt, context, raw model
  output, parsed reply, errors, and provider metadata.
- The NPC should speak as one character only: no narration, no markdown, no
  stage directions, no omniscient exposition.
- The prompt should tell the model to answer the newest player message directly,
  use remembered provenance, avoid echoing the player, and keep replies
  substantial but compact: about two short paragraphs.
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
  structuring calls. Sorcerer should keep that useful separation of concerns,
  but make it disciplined: route context first, generate speech second, then
  run one background parser for supported mechanics.
- Trade should not copy WildMagic's LLM trade-intent path. Sorcerer already has
  a clear rule: explicit trade commands are engine-side; dialogue can create
  context, not execute bargains.
- Provider fallback must not quietly invent fake facts. If the model fails, the
  action result should report a technical failure and avoid consuming a turn.

## Core Flow

1. The player issues a `talk` command in the GUI or CLI.
2. The engine resolves the target entity. `DialogueContextAssembler` builds
   NPC-knowledge-gated context cards and a compact route request.
3. The context-router result selects context cards. Unknown or access-denied
   ids are ignored and audited; router failure falls back to deterministic
   balanced cards.
4. The assembler builds a speech-only `DialogueRequest` from selected cards.
5. The dialogue provider generates a `DialogueResponse` containing visible NPC
   speech, delivery, and intent.
6. The engine parses, repairs, normalizes, validates, and reports the spoken
   text to the player.
7. The engine queues the parser lane after visible speech. A parser-router first
   selects the smallest useful parser capability cards, then the parser extracts
   supported mechanical proposals from the spoken reply.
8. Parser results are applied transactionally at an explicit pump point or save
   flush.
9. The exchange is recorded as dialogue history and memory.
10. The action result reports whether a turn was consumed and materializes any
   completed parser/proposal records.

Technical failures should not consume a turn. Intentional character responses
do consume a turn: refusal, evasion, silence, threats, lies, panic, or "I will
not speak to you" are all successful dialogue outcomes.

## Generated Dialogue Response

The foreground generated dialogue path returns structured JSON, but only for
speech metadata. It should not receive or emit mechanical proposal schemas.

Example shape:

```json
{
  "spokenText": "Old Maren keeps a red-glass knife under the chapel floor. I saw her wrap it in lambswool after the bell cracked.\n\nIf you go looking, step where the boards have been waxed. She hates squeaking more than sin.",
  "delivery": "hushed",
  "intent": "confide"
}
```

The parser lane then receives the spoken reply, player line, speaker/listener
ids, recent memories/claims, selected context card ids, and routed proposal
families. Its output is proposals, not authority.

Parser example shape:

```json
{
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
- Parser `proposals` are candidate engine operations.
- The engine can accept, clamp, transform, defer, or reject each proposal.
- Legacy generated-dialogue responses that include `proposals` may still be
  accepted for replay and tests, but live generator prompts should not ask for
  them.

The current provider-facing C# shape uses `DialogueClaimProposal` for claim
proposals, with separate `DialogueMemoryProposal`, `DialogueBondProposal`, and
`DialogueActionProposal` records. The engine-facing direction is to normalize
these into typed consequence records so the same applier can be used by
dialogue, documents, services, promises, AI plans, and magic.

## Request Context

The dialogue request should be compact but rich enough for the model to make
good choices. The current request shape is a first compact slice; the target
architecture is routed context cards, described in
[DIALOGUE_CONTEXT_ROUTING.md](DIALOGUE_CONTEXT_ROUTING.md).

The always-on base should include:

- Speaker entity: id, name, role, faction, body, tags, current status, speech
  capability, visible temperament, and active want when present.
- Listener entity: controlled body, appearance, known reputation, visible
  equipment, active status, recent magic, and recent deeds known to the speaker.
- Relationship: bond values, faction standing, hostility, fear, gratitude,
  debts, gifts received, and recent interactions.
- Scene breadcrumb: region, local culture, current tile or room, nearby anchors,
  danger state, imprisonment or combat context.
- Conversation history: the last one or two exchanges with this NPC, clipped for
  budget.
- Current constraints: player claims are not binding, model output is untrusted,
  do not invent unsupported engine operations, answer the latest message.

Routed cards then add only the deeper state the player's line calls for:

- Rumors: heard local stories with source and salience, explicitly treated as
  possibly distorted rather than guaranteed truth.
- Local scene and object detail: visible entities, items, fixtures, nearby
  landmarks, claim seeds, and affordances.
- Memory and relationship: firsthand memories, overheard claims, gifts, prior
  conversation, deeds, and bond-relevant provenance.
- Claims, promises, services, wares, faction law, travel routes, recent magic,
  and other specialized lanes.
- Parser proposal-family cards: which action/proposal schemas need full prompt
  detail in the background parser call. These should not be sent to the
  foreground speech generator.

The request builder belongs behind the engine boundary. Renderers should not
assemble prompt context directly.

## Proposal Types

Dialogue should start with a small set of general proposal types and grow only
when a new type unlocks many interactions.

## Typed Consequence Grammar

Dialogue consequences should be part of the broader typed consequence grammar.
The first implemented envelope is:

- `type`: the consequence kind, such as `record_memory`, `update_bond`, or
  `add_merchant_stock`
- `source`: where the proposal came from, such as dialogue, claim extraction,
  a promise payoff, a service, AI, or magic
- `timing`: when the consequence applies, normally `immediate`; deferred or
  world-pump behavior must be explicit
- `sourceEntityId` and `targetEntityId`: provenance and mutation target
- `salience`, `confidence`, and `visibility`: player/debug surfacing signals
- `evidence` and `reason`: audit context for why this was proposed
- `payload`: typed consequence-specific fields

The applier handles claim recording, claim status updates, memory recording, bond updates, want updates, merchant
stock, trade offers, service offers, door open/unlock effects, route creation,
immediate tactical effects, terrain lifecycle updates, inventory changes, tag/trait changes, faction
allegiance, world flags, scheduled event creation/lifecycle, trigger creation/lifecycle, transformations,
resistance and weakness changes, delayed incoming damage/release, status acceleration,
memory edits, persistent effect creation/update, behavior tag creation/update, tile flow creation/update, rumor records/updates,
faction standing/resource adjustments, suspicion records/updates, deed records, legend tags, and canon records. It owns
target validation, clamping, mutation, visible messages, and deltas. This keeps
the same claim, bond, want, stock, service, route, inventory, faction, trigger, memory
edit, transformation, or door change from behaving differently depending on
whether it came from generated dialogue, delayed claim extraction, a service, a
promise payoff, wild magic, world reaction, or some other source.

Immediate consequences remain fast because their `timing` is immediate and
their handlers are concrete engine code, not because they live in a separate
grammar. Durable narrative facts use the existing `add_canon` consequence;
dialogue may request this through a `canonize_fact`/`add_canon` action, and
delayed claim extraction may request it with `bindAsCanon`, when the NPC's
spoken line plainly supports the fact.

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
Both live generated-dialogue claims and delayed extractor claims must pass a
spoken-text support check before the engine records, canonizes, or promotes
them. The extractor interface can mark a source as prevalidated only for
authored/test sources; live model extractors should keep support validation on.

Claims can have several outcomes:

- `bindAsPromise` means the world should later deliver the claim organically
  through ordinary promise realization.
- `bindAsCanon` means the claim should become durable world lore now through
  `add_canon`, without forcing a future payoff site or item.
- immediate categories such as `merchant_stock`, `service`, and `trade` can
  apply concrete affordances to existing entities.

Initial claim intake is a packet of its own: the claim's `record_claim`, the
speaker's `record_memory` (`claimMemory`), and any first visible rumor
(`rumorMinted`) stage together before promise binding, canonization, bond
changes, stock, services, or trades run. If an intake child rejects, the staged
claim/memory/rumor state rolls back and the result keeps only hidden audit
evidence through `claimIntakeSkipped` plus any rejected child diagnostics.

Immediate claim applications use the same packet discipline as promise binding:
the concrete consequence (`update_bond`, `add_canon`, `add_merchant_stock`,
`offer_service`, or `offer_trade`) and the claim's `update_claim` status commit
together. If either child rejects, the application rolls back with hidden
`claimApplicationSkipped`.

Use `bindAsCanon` for local law, custom, public history, known relationships,
taboos, lineage, or other facts that should inform later context but are not
obligations. Use `bindAsPromise` for useful future things the world should
surface or generate.

### Memories

Memories are local, durable facts attached to entities. Gifts should create
memory immediately, and the explicit gift path commits the item transfer plus
gift memory together so the world never loses the provenance for a gift that
changed hands. Bond changes should be model-proposed during dialogue, using
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

Explicit interaction outcomes that move relationships, such as recruitment and
rescue, should also submit `update_bond` consequences. They may keep their own
action-specific deltas for readability, but the bond ledger should have one
apply path.

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
- `give`
- `open`
- `recruit`
- `create_promise`
- `offer_trade`
- `reveal_service`
- `mark_location`
- `spawn_fixture`
- `consequence` with `consequenceType` and `consequencePayload`

Each action must pass ordinary preconditions. An NPC cannot give an item they do
not have or open a door they cannot reach. `create_promise` is applied by
`WorldConsequenceApplier`, which owns promise creation, binding, anchor attachment,
visibility, and audit deltas for every source. Rebinding or realizing an existing promise
uses the matching `update_promise` consequence. Direct dialogue `create_promise`
actions are for explicit vows, prophecies, curses, or mechanically important
commitments in NPC `spokenText`; ordinary reports such as distant towns or
item locations should still flow through claims that may bind as promises.
Rejected dialogue actions produce hidden `dialogueActionRejected` audit deltas
with both the proposed type and normalized type, so unsupported or invalid model
actions remain inspectable without becoming player-facing truth.
Generic `consequence` actions are for local effects that already have a typed
handler, such as adding tags, applying a status, or requesting an already-known
service. They also cover local prop/fixture transformations through
`transform_entity`, so an NPC can collapse, mark, repair, retag, or make
interactable an existing bridge, shrine, sign, door-like prop, or fixture
without a bespoke dialogue action. They submit directly through
`GameEngine.ApplyConsequence`;
non-immediate `consequenceTiming` values such as `after_turn`, `world_pump`, or
`deferred` schedule one typed consequence through the shared turn pump. Use
explicit `schedule_event` only for broader future events such as calling help or
setting a patrol, not for a simple delayed local consequence.
The adapter accepts the same common model-shaped nested aliases as magic, including
`consequencePayload.world_consequence_type`, `target_id` / `entity_id`,
`consequence_timing`, and `consequence_visibility`.
If a provider emits a known consequence id as the action type itself, such as
`request_service` or `add_tags`, the adapter normalizes it into this same generic
`consequence` path. Common top-level action fields such as `targetEntityId`,
`tags`, `serviceId`, `itemName`, `quantity`, and `fixtureType` are promoted into
the typed payload when `consequencePayload` does not already provide them, and
the live provider parser uses the shared consequence payload merge helper to
preserve additional top-level typed fields such as `status`, `duration`,
`amount`, or `resource` as payload data. Named dialogue
actions like `create_promise`, `add_canon`, `offer_trade`, and `spawn_fixture`
keep their richer handlers.
The shared consequence applier canonicalizes common source spellings such as
`addTags` or `requestService` to the engine's snake_case consequence ids before
validation, so dialogue, magic, scheduled events, triggers, and persistent
effects all get the same repair lane.
`offer_trade` reveals stock or merchant affordances for later explicit commands
rather than completing a trade inside dialogue.
`step_aside` and `flee` submit `move_entity` consequences with ordinary entity
movement checks. `call_help` submits a visible `schedule_event` consequence:
imperial callers can schedule an `empire_patrol`, while non-imperial calls
schedule a generic help-call message. `attack` submits the same damage
consequence as melee after validating speaker, target, living actor state, and
adjacency. `recruit` submits the shared recruitment bundle: faction change,
follower control, bond update, memory, and visible message all use the same
validated helpers as explicit `recruit`, and the accepted bundle commits
together or rolls back with `recruitmentSkipped`; the dialogue action itself
does not consume an extra turn beyond the conversation. `offer_trade` submits the shared trade-offer consequence: it can turn
an NPC into a normal merchant and optionally add stock, but buying and selling
still require explicit trade commands. `mark_location` and `spawn_fixture`
submit local `spawn_fixture` consequences after nearby-placement validation;
`transform_entity` changes existing local entities and fixtures through the
same applier. Remote places described by an NPC should be claims/promises, not
immediate map edits. `reveal_service` submits `offer_service`, so dialogue can reveal a
folk-magic or mundane service without performing it until the player uses an
explicit `request` command. If the same dialogue also produces a service claim,
the claim attaches to the already-revealed service instead of submitting a second
`offer_service` consequence; the claim ledger still records what was said.
`canonize_fact`/`add_canon` submits the shared `add_canon` consequence for
local durable lore, law, custom, or entity/place facts that should inform later
context but are not future obligations. The action is hidden by default so the
spoken line remains the player-facing surface, and the same token-overlap
support check used for claims prevents the provider from canonizing facts the
NPC did not actually say.

Implementation can migrate one verb at a time. `open` is actor-aware already:
player `open`, dialogue `open`/`open_door`, services, and future magic/AI open
requests flow through `open_or_unlock`, with the source controlling turn
consumption and audit wording rather than world truth. Door-triggered rescue consequences are
not dialogue specials either: after a captive-door opens, the follow-on
faction/control/bond/want/deed/message bundle is requested as the shared
`free_captive` consequence and commits together or rolls back with
`freeCaptiveSkipped`. Ordinary player movement, dialogue
movement, AI movement, tile-flow movement, and magic movement likewise converge
on `move_entity`; follower yielding uses the same consequence with an explicit swap target rather
than a private pathing shortcut. Promise payoff also has a shared first path now: travel, talk,
read, open, examine/inspect, and explicit time-triggered wait payoffs all
route concrete promise realization through `PromiseRealizationSystem`; `wares`,
`buy`, and `sell` also route anchored merchant-stock promises through that
service before ordinary stock listing or `execute_trade`; `services` and
`request` route anchored service promises through it before listing or
submitting `request_service`; `open` routes anchored door-rule promises through it
before the lock check. Anchored route, service, door-rule, and canon payoffs
apply as `create_route`, `offer_service`, `open_or_unlock`, and `add_canon`
consequences.

The first service/action consequence slice is live:

- `offer_trade` can make an NPC a normal merchant and optionally add stock.
- `execute_trade` completes explicit buy/sell commands by mutating buyer inventory,
  merchant stock, and merchant gold in one validated transaction.
- `offer_service` attaches a `ServiceComponent` to an NPC.
- `reveal_service` lets dialogue expose one of those services through the same
  `offer_service` consequence.
- `services [target]` lists revealed services and can wake anchored service
  promises first.
- `request <service> [from <target>]` asks the provider to perform it, after
  waking a matching anchored service promise if needed, by submitting
  `request_service`. The command wrapper owns turn consumption; the consequence
  owns service effect, payment, want completion, and narration.
- A generated dialogue `consequence` action can also submit `request_service`
  when the NPC is performing an already-known local service right now. In that
  dialogue path, the speaker/provider remains the consequence target, while the
  player-controlled entity is the default requester/payer unless the payload
  explicitly names another `actorEntityId`.
- `move_entity` handles dialogue `step_aside` and `flee` movement.
- `damage` handles dialogue `attack` after ordinary adjacency and living-actor checks.
- `create_promise` handles explicit dialogue vows, curses, and prophecies
  through the same promise ledger applier as claim promotion and magic.
- `add_canon` handles dialogue `canonize_fact`/`add_canon` actions and
  extracted `bindAsCanon` claims for durable local facts that should inform
  later context without becoming promises.
- `schedule_event` handles dialogue `call_help` responses.
- `open_or_unlock` can unlock/open a nearby door through an engine-validated
  consequence.
- `free_captive` can release a captive, update faction/control/bond/want/deed,
  and emit narration as one reusable transactional consequence.
- `create_route` creates a discoverable route fixture.
- `spawn_fixture` creates a generic fixture/place/prop entity for marked
  locations, promise sites, shrines, hazards, dialogue markers, and other
  concrete world features.

### Stock And Services

NPC dialogue may propose that an NPC gains stock or reveals available services.
This is not a completed trade. It is a state update that makes later explicit
commands possible. Actual buy/sell commands then submit `execute_trade`; dialogue
does not complete a purchase by implication.

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
them. Mechanically, revealed services use `ServiceComponent` plus explicit
`services`/`request` commands. A request submits `request_service`, whose child
effects route through
`WorldConsequenceApplier`, including door open/unlock, route creation, and
durable memory, rather than becoming a separate hidden spell engine. Listed
service payments and gift item spending use the shared `modify_inventory`
consequence as well, so social actions do not keep private inventory decrement
helpers beside the consequence grammar. A revealed service can optionally carry
completion metadata (`wantStatusOnComplete`, `wantStakesOnComplete`,
`wantAddTagsOnComplete`, `wantRemoveTagsOnComplete`). The engine reads those
fields only after a successful explicit `request` and applies them through
`update_want`, which lets a service satisfy or redirect its provider's active
desire without inventing a private service quest path. When a service declares
that completion metadata, the want update is authoritative: failure to update
the want rolls back the requested service effect, payment, and request message.
The same `serviceRequestSkipped` rollback audit is used if the effect, payment,
want update, or final request narration rejects, so service requests have one
lifecycle rather than several partial failure lanes.
Service-completion want updates also record a private memory through the same
consequence; the public service fact remains the separate service memory/deed
lane.

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

Implementation invariant: claim-to-promise promotion uses the shared
`create_promise` world consequence with dialogue-specific metadata (`claimId`,
source, visibility, salience, subject, claimed place, trigger hint, and
realization kind), claim recording itself uses `record_claim`, claim memory uses
`record_memory`, first rumor minting uses `record_rumor`, and later claim
status changes use `update_claim`. The binding step is transactional: creating
or rebinding the promise and marking the claim promised commit together, and a
rejected child leaves only a hidden `claimPromiseSkipped` audit delta. Dialogue
extraction may decide *whether* a claim should bind, but it does not write the
claim or promise ledgers through private paths.

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
- Use a concrete `triggerHint` such as `travel`, `talk`, `buy`, `trade`,
  `services`, `request`, `open`, `wait`, or `inspect`.
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

- Dialogue router call: foreground and tiny. It selects context cards only.
- Generated dialogue call: foreground, speech-only. The spoken line depends on
  it.
- Parse/repair/validate spoken text: foreground and fast.
- Display spoken text: immediate after validation.
- Parser-router call: background and tiny. It decides whether the spoken reply
  needs mechanical parsing and which parser capability cards to include.
- Parser call: background. It reads the spoken line and selected mechanical
  guidance after the player-visible response is available.
- Apply completed parser proposals: next command, world pump, or save flush.
- Heavy realization: between turns or background queue.
- Claim/mechanical extraction for generated text without a proposal envelope:
  between turns. Legacy or replayed generated dialogue that includes a
  `proposals` object is treated as already parsed and is not re-extracted.
- `save` is a synchronization point: if parser/extraction or dialogue proposal
  work is pending, the session should wait for it to complete, apply accepted
  results or record technical failures, and only then write the save.
- Audit and eval logging: asynchronous where possible.

Use the same loaded model as ordinary dialogue by default for the foreground
router/generator unless audits prove a smaller route model improves end-to-end
latency. The parser is different: it should run after visible speech on the
`dialogue_parser_router` and `dialogue_parser` purposes, which both default
local Ollama to CPU (`SORCERER_DIALOGUE_PARSER_ROUTER_NUM_GPU=0` and
`SORCERER_DIALOGUE_PARSER_NUM_GPU=0`) so mechanical parsing does not monopolize
the foreground GPU model. Users can override those lanes with
`SORCERER_DIALOGUE_PARSER_ROUTER_*` and `SORCERER_DIALOGUE_PARSER_*` settings.

Pending extraction tasks should not be serialized. Saves should contain the
durable result of completed dialogue work, not live provider tasks. If an
extractor fails while a save is waiting on it, the failure should be recorded as
an audit/debug delta before saving. Task-level extractor faults and cancellations
are materialized as technical-failure extraction records too, so transcript
replay can reconstruct the attempted failure without calling the live model.

CLI transcripts materialize dialogue separately from save files. A generated
`talk` result records the normalized `DialogueResponse` on the `ActionResult`,
and any completed post-dialogue parser result records the original request, full
proposal set, provider, raw text, error state, and spoken-text-support flag.
Legacy claim extraction records are still written from the same result for old
tooling. Transcript replay feeds materialized records through
`ReplayDialogueProvider` and `ReplayDialogueParser` when full parse records are
present, falling back to `ReplayDialogueClaimExtractor` for older transcripts.
Replay therefore exercises the same `GameSession` proposal and consequence path
without calling the live model.

## Provider Architecture

Core interfaces should live where `GameSession` can depend on abstractions
without depending on provider implementations.

Suggested interfaces:

- `IDialogueProvider`
- `IDialogueRouter` for selecting context cards before the final dialogue call.
  The first implementation is always-on and authoritative; live router failures
  fall back to deterministic balanced cards.
- `IDialogueParserRouter` for selecting post-speech parser capability cards
  before the detailed mechanical parser runs. It can return `hasMechanics:
  false`, in which case the detail parser call is skipped and an empty proposal
  record is materialized.
- `IDialogueParser` for post-speech mechanical proposal extraction. It emits
  full `DialogueProposalSet` records: claims, memories, bond/want changes,
  local actions, service/trade offers, and typed consequences.
- `IDialogueClaimExtractor` as a compatibility interface adapted into
  `IDialogueParser` for older tests, transcripts, and narrow claim-only
  providers.
- `IDialogueAuditSink`

Suggested implementations:

- `MockDialogueProvider`: deterministic, schema-correct, good for tests and
  agent playthroughs.
- `MockDialogueRouter`: deterministic card selection for tests and agent runs.
- `OllamaDialogueProvider`: local model path, JSON response, retry on degenerate
  output.
- `OpenAiCompatibleDialogueProvider`: OpenAI-compatible `/v1/chat/completions`
  path, using the same response parser and retry policy.
- `OllamaDialogueRouter` and `OpenAiCompatibleDialogueRouter`: short JSON
  context-card routers under the `dialogue_router` purpose.
- `OllamaDialogueParserRouter` and `OpenAiCompatibleDialogueParserRouter`:
  short JSON parser-capability routers under the `dialogue_parser_router`
  purpose, defaulted to CPU for local Ollama.
- `OllamaDialogueClaimExtractor` and
  `OpenAiCompatibleDialogueClaimExtractor`: post-speech JSON parsers that also
  implement `IDialogueParser`, preferably on the `dialogue_parser` purpose and
  CPU for local runs.

The generator should receive the `dialogue` purpose. Follow-up routing and
parsing should receive `dialogue_parser_router` and `dialogue_parser`, which may
use separate models, hosts, API keys, timeouts, concurrency, or Ollama GPU layer
counts.

Current dialogue action results include a materialized dialogue record for
generated provider calls, plus provider, raw output, delivery, intent,
and generated/fallback markers on the `dialogue` delta. CLI and Godot wire
`JsonlDialogueAuditSink` to `logs/dialogue_audit.jsonl`; the audit entry records
the request, raw provider output, parsed response, validation issues, action
result, and delta operation names.

## Validation Rules

Dialogue and parser output are untrusted. Validation should cover both the
spoken line and all parser or legacy proposals.

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

1. Define the dialogue contract: request, response, parser proposals, validation
   results, and audit record. Done.
2. Build the context builder from engine state only. Done for the first compact
   request shape.
3. Add a deterministic `MockDialogueProvider`. Done.
4. Add parser, repair, normalization, and validation tests. Done for the
   speech-only generator, full parser proposal records, and replay materialized
   parser results.
5. Wire `talk` through `GameSession` for both GUI and CLI. Done.
6. Add action-result fields for accepted and rejected dialogue proposals. Done
   through deltas.
7. Add live `OllamaDialogueProvider` and `OpenAiCompatibleDialogueProvider`
   paths with audit logging and degenerate reply retry. Done.
8. Integrate accepted parser/claim proposals with the existing `ClaimLedger`. Done.
9. Keep `IDialogueClaimExtractor` as a compatibility lane for authored text,
   books, signs, deterministic fallback dialogue, old transcripts, and narrow
   claim-only providers. Adapt it into `IDialogueParser` when no full parser is
   configured. For authored
   objects with known hooks, prefer `ClaimSourceComponent`: `read`/`examine`
   records those seeds through `record_claim`, mints high-salience visible rumors
   through `record_rumor`, binds buildable hooks through `create_promise`, and
   updates claim status through `update_claim`.
10. Add validated handlers for concrete parser/dialogue action proposals such as
    `step_aside`, `flee`, `call_help`, `give`, `open`, `attack`,
    `create_promise`, and `offer_trade`, plus service and local scene fixture
    actions such as `reveal_service`, `mark_location`, and `spawn_fixture`.
    First handlers are done.
11. Move live generated-dialogue prompts to speech-only and wire parser work
    through `dialogue_parser`, defaulting local Ollama parser calls to CPU.
    Done for the live generator/parser split.
12. Add dialogue context routing: card registry, deterministic assembler,
    router request/result contracts, audit byte counts, replay records, then
    optional live LLM router. See
    [DIALOGUE_CONTEXT_ROUTING.md](DIALOGUE_CONTEXT_ROUTING.md). First routed
    slice done with `IDialogueRouter`, `DialogueContextCardSpec`,
    `DialogueContextCardPayload`, `DialogueContextAssembler`,
    `DialogueRouteRecord`, `DialogueRouteMetrics`, `KnowledgeComponent`,
    tier 0 common lore, subject-specific lore access through
    `LoreCatalog`/`LoreRouter`, mock/live/replay routers, route records in
    dialogue audits, deterministic failure fallback, and high-value cards for
    zone NPCs, travel/routes, promise hooks, and recent magic/deeds.
13. Widen `IDialogueClaimExtractor` into a full parser interface for memories,
    actions, wants, services, and typed consequences. Done through
    `IDialogueParser`; old claim extractors remain supported through an adapter.
14. Add parser-router capability selection before the detailed parser call. Done
    with `IDialogueParserRouter`, deterministic/live/replay parser routers,
    parser-route metrics, CPU-default `dialogue_parser_router` configuration,
    and parser requests trimmed to selected capability cards.
15. Add opening NPC profiles that rely on the same system as the rest of the
    game. First pass done for opening NPC knowledge permissions, including
    Lio's Hollowmere/current-zone, water-magic, wild-magic, service, oath, and
    public-law tiers; broader authored profiles remain.
16. Add live-model eval transcripts for directness, specificity, promise
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
- Dialogue can propose want changes when an NPC's active desire materially shifts.
- Dialogue action proposals obey normal preconditions.
- Promise realization preserves dialogue provenance.
- Save/load preserves conversation history, memories, claims, and promises.
- Saving immediately after dialogue waits for pending extraction, applies or
  records the result, and does not lose claims on reload.
- Replay can use recorded dialogue outputs.
- Routed dialogue selects the expected context cards, enforces access policies,
  respects budgets/truncation, and replays without live router calls.

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
