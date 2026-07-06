# Dialogue Context Routing

Status: **active implementation**. Companion to
[DIALOGUE_SYSTEM.md](DIALOGUE_SYSTEM.md) and
[CAPABILITY_ROUTING.md](CAPABILITY_ROUTING.md).

Dialogue needs the same discipline as wild magic: do not dump the whole world
into every model call. The generator should receive enough context to answer
the player's actual question in character. The follow-up parser should receive
enough focused mechanical guidance to turn useful speech into validated
consequences. Neither call should receive every rumor, every object, every
memory, every faction fact, and every action schema on every line.

The design here adds a short foreground routing pass:

> Here is the speaker, the listener, and the player's latest line. Which context
> cards do you need?

The context-router selects context cards. The engine materializes card payloads
from authoritative state under NPC knowledge gates, routes over a tiny card
index, then sends only selected cards to the foreground generator. After visible
speech is available, a separate parser-router selects the smallest useful set of
parser capability cards before the detail parser runs. Neither router decides
truth or proposes mutations.

## Design Decisions

- Every generated dialogue turn goes through the routing layer. Tests, mock
  play, and disabled-provider runs may use deterministic-only routing, but the
  final request should still be assembled from selected cards rather than from
  one broad fixed context packet.
- Optimize for speed and richness together: spend a tiny route step to keep the
  final generated-dialogue prompt focused, then give deep context only for the
  player's actual topic.
- Route proposal-family guidance in the post-speech parser-router, not in the
  foreground speech generator or context-router. The parser-router can say
  "this turn probably needs claim/memory guidance" or "this turn probably needs
  local-action guidance" so the parser spends detail where it will matter.
- Respect NPC knowledge. Static world knowledge should be tier-gated by what
  this speaker plausibly knows before any model sees section descriptions or
  bodies.
- First optimization targets are rumors, local objects, factions/law, current
  zone NPCs, routes/travel, promise hooks, and recent magic/deeds.

## Goals

- Reduce final dialogue prompt size while preserving coherence.
- Let the model get deep context only when the player's line calls for it.
- Keep generated speech and mechanical parsing separate: foreground
  `spokenText`, then a background parser result that proposes claims,
  memories, actions, bond/want changes, or typed consequences.
- Make routing auditable and replayable.
- Bias toward recall: missing relevant context is worse than including one
  extra small card.
- Keep GUI and CLI identical by routing inside `GameSession`, behind the same
  backend.

## Non-Goals

- No router-authored facts.
- No router-authored dialogue.
- No direct world mutation from routing output.
- No LLM trade intent. Dialogue can reveal stock or services, but explicit
  `wares`, `buy`, `sell`, `services`, and `request` commands still execute
  trades and services.
- No player-authored claims becoming true because the router selected a claim
  card.

## Core Flow

1. `GameSession` resolves the `talk` target with the existing dialogue
   preparation path.
2. `DialogueContextAssembler` builds NPC-knowledge-gated context cards from
   live engine state and a compact `DialogueRouteRequest` containing only
   participant summaries, small scene hints, and one-line card candidates.
3. `IDialogueRouter` returns a `DialogueRouteResult` naming context cards.
4. The engine normalizes the route result, ignores unknown or denied ids, and
   uses the selected valid cards. If routing fails, times out, or selects no
   valid cards, it falls back to deterministic balanced cards.
5. `DialogueContextAssembler` builds the final generator request from selected
   card payloads, preserving compatibility fields derived from those cards.
6. The dialogue generator receives the final speech request, including the base
   participant context plus routed context cards. It does not receive mechanical
   schemas.
7. The generator returns visible `DialogueResponse` speech; the engine validates
   and displays it.
8. The engine queues the parser lane with the spoken text and compact
   provenance. `IDialogueParserRouter` runs under `dialogue_parser_router` and
   selects parser capability cards or returns `hasMechanics: false`.
9. If parser detail is useful, `IDialogueParser` runs under `dialogue_parser`,
   preferably CPU/background for local Ollama. If not, the detail call is
   skipped and an empty parser record is materialized.
10. Completed parser results are normalized, validated, and applied through the
   existing consequence path at an explicit session pump point or save flush.

Router technical failure should not fail the dialogue turn. It falls back to a
balanced default card set and records an audit entry. Only final dialogue
provider technical failure keeps the turn from being consumed.

## Router Request

The route request must be tiny. It should include identifiers and short labels,
not full ledgers.

```json
{
  "t": 14,
  "text": "Lio, what rumors have you heard about the road east?",
  "speaker": "Lio of Hollowmere (prisoner_1) [npc,prisoner,hollowmere]",
  "listener": "You (player) [sorcerer,wild_magic]",
  "scene": "imperial_encounter/0,0",
  "nearby": [
    "Lio of Hollowmere (prisoner_1) at 14,5, range 1",
    "locked imperial cell door (cell_door_1) at 13,5, range 0",
    "brass containment brazier (brazier_1) at 6,4, range 7"
  ],
  "cards": [
    "rumors.full: Rumors the speaker or region plausibly carries.",
    "scene.object_detail: Visible object, fixture, door, and item details."
  ]
}
```

The router request should include a small `nearbyAnchors` list so the router can
focus object or person cards without seeing their full details. Engine reference
binding remains authoritative; router focuses are hints. Do not send bond
ledgers, wants, inventories, services, full card payloads, or recent claim text
to the foreground context-router; the router needs only enough state to choose
card ids.

## Router Response

The router returns JSON only. It names cards, not facts.

```json
{
  "selectedCardIds": ["rumors.full", "region.travel", "promise.hooks"],
  "reason": "The player explicitly asked what rumors the speaker has heard about the road."
}
```

Fields:

- `selectedCardIds`: requested context card ids. Unknown ids are ignored and
  audited.
- `reason`: short debug text.

The response should never contain dialogue prose, claims, memories, or
consequences. If it does, the engine drops those fields.

## Deterministic Fallback

Routing should never be purely model-dependent. If the live router fails, times
out, or returns no valid NPC-knowledge-gated card ids, the engine falls back to
cheap deterministic signals:

- Player text contains `rumor`, `gossip`, `heard`, `news`: add
  `rumors.full`.
- Player text names a visible object or uses `this/that/room/around here`: add
  `scene.object_detail`.
- Player text contains `sell`, `buy`, `wares`, `service`, `mend`, `can you`:
  add `services.available`.
- Player text contains `remember`, `trust`, `gift`, `why did you`: add
  `npc.relationship_memory`.
- Player text contains `Empire`, `law`, `soldier`, `warrant`, `patrol`: add
  `faction.law`.
- Player text contains `road`, `north/east/south/west`, `where`, `route`: add
  `region.travel`.
- Player text contains `help`, `fight`, `danger`, `open`, `move`: add
  relevant local action/proximity cards.

Under-selection hurts more than over-selection on fallback turns, so the
deterministic set is balanced toward common useful cards. On successful live
routes, the selected valid card ids are authoritative; unknown or access-denied
ids are ignored and audited rather than filled in with extra context. Hard card
budgets keep over-selection bounded.

## Always-On Base

The final dialogue request always includes:

- Speaker card: id, name, tags, faction, short profile, current status, active
  want summary.
- Listener card: controlled body, visible identity, faction, visible equipment
  summary.
- Relationship summary: bond, hostility, recent direct exchange count.
- Scene breadcrumb: region, zone, immediate danger/combat flag, very short
  nearby anchor index.
- Last one or two conversation turns with this speaker.
- Core rules: speak only as the NPC, player claims are not binding, use only
  supplied context, and do not emit mechanics.

Everything else is routed.

## Context Card Registry

Each card has metadata and a deterministic extractor:

```csharp
public sealed record DialogueContextCardSpec(
    string Id,
    string Topic,
    string Kind,
    string Title,
    string Summary);
```

Materialized card payloads should include `id`, `focus`, `truncated`, and
`items`. The final provider sees only materialized card payloads, never the
whole registry internals.

### Initial Card Set

`zone.current`

Visible zone state, nearby entities, items, and recent events. This remains the
small always-useful scene card for ordinary broad questions.

`zone.npcs`

Other dialogue-relevant NPCs in the current zone, including names, ids, tags,
factions/roles, range, and active want summaries when present. Triggered by
"who else is here", guard/soldier/person questions, recruitment pressure, and
current-zone social context.

`rumors.full`

Rumors the speaker, their soul, or the current region plausibly carries.
Triggered by questions about rumors, gossip, news, roads, threats, or what
people are saying. Includes source kind/id, text, salience, status, hops, and
whether the rumor is distorted. Budget can be higher than other cards because
"what rumors have you heard?" is explicitly asking for this lane; use a hard cap
and `truncated: true` if needed.

`claims.recent`

Recent reported claims available as conversation context. Triggered by leads,
secrets, named people/items/places, or "what did you say before?"

`promise.hooks`

Visible or speaker-linked promises and bound claim hooks that could shape future
delivery. Triggered by promises, debts, prophecies, oaths, route hooks, and
questions about what has been set in motion.

`scene.object_detail`

Focused object/fixture/item details: name, id, tags, description, material,
claim seeds, readable/examinable verbs, blocking/sight properties, and distance.
Triggered by a named local object or deictic speech such as "that brazier" when
reference binding can resolve it.

`npc.relationship_memory`

Speaker memories involving the listener, gifts, claims, promises, witnessed
deeds, and prior exchanges. Triggered by trust, apology, threats, kindness,
debt, "remember", "why", or bond-sensitive questions.

`services.available`

Speaker inventory, merchant wares, revealed services, service costs, and
service completion metadata. Triggered by buy/sell/wares/service/mend/heal/open
requests. This card can allow `offer_trade`, `reveal_service`, and
`request_service` proposal guidance, but explicit purchases still require
engine commands.

`faction.law`

Speaker faction, local imperial pressure, public laws/canon, faction standing,
known warrants/suspicion/deeds relevant to the speaker, plus authored lore
sections that pass the speaker's knowledge gate. Triggered by questions about
Vigovia, guards, law, patrols, paperwork, penalties, or politics.

`npc.knowledge.region`

Authored static canon from the existing `LoreCatalog`/`LoreRouter`, selected for
the speaker and topic. Triggered by questions about factions, cultures, regions,
traditions, imperial law, famous people, or why a local custom works the way it
does. This is separate from run-dynamic claims, promises, and rumors: it may
color the answer, but it is not a fresh claim to extract or bind.

`region.travel`

Region summary, known routes, nearby landmarks, atlas hints, travel-relevant
promises, and directionally relevant claims. Triggered by "where", "road",
"north/east/south/west", escape route, towns, landmarks, or travel planning.

`recent.magic_deeds`

Recent visible magic, statuses, persistent effects, strange traits on entities,
deeds, suspicions, and public consequences the speaker plausibly witnessed.
Triggered by "what did I do", "what happened", visible weirdness, accusations,
reputation, curses, transformations, or magical objects.

## Parser Capability Routing

Context cards answer "what state should the generator see?" Parser capability
cards answer "which structured outputs should the parser prompt explain in
detail?"

This is mostly a token and attention tool. The engine can still parse and
validate every known proposal type, but the parser prompt does not need to spend
equal detail on merchant stock, door actions, faction consequences, bond
updates, and promise binding on every exchange. For a rumor question, the prompt
should emphasize claim, memory, rumor, and promise guidance. For an object
question, it should emphasize local object claims, `add_canon`, `spawn_fixture`,
`open_or_unlock`, or other object-affordance consequences. For a faction
question, it should emphasize faction/law claims, known standings, suspicion,
and canon/faction consequences while keeping trade and relationship mechanics
compact.

Implemented capability cards:

- `claims`: `DialogueClaimProposal` guidance.
- `promises`: claim-to-promise binding guidance.
- `canon`: durable lore or public fact guidance.
- `memory`: durable conversation memories.
- `bond_want`: relationship and active-want updates.
- `local_actions`: step aside, flee, call help, open, give, recruit, attack.
- `services_trade`: offer trade, reveal service, request known service.
- `typed_consequences`: generic typed consequence actions.

The parser-router is authoritative for prompt detail, but the engine still
normalizes and validates every parser proposal that arrives. If the router
returns `hasMechanics: false`, the detail parser call is skipped.
Successful live parser-router selections are capped to the first three valid
capability ids before detail prompt assembly. The live prompt asks for the
minimum useful set, normally zero to two ids, with three reserved for unmistakably
mixed speech. Deterministic fallback remains broader because it only runs after a
router failure or empty valid live route.

Claims and memories should usually remain available because ordinary speech can
create durable claims unexpectedly. Verb-heavy action families should be routed
more narrowly.

Deterministic fallback includes `claims`, `promises`, `canon`, `memory`,
`bond_want`, `local_actions`, `services_trade`, and `typed_consequences` based
on the player line plus NPC speech. Legacy `IDialogueClaimExtractor` adapters
use a compatibility parser-router that always selects the claim-oriented cards
they can represent.

## Access Policies

Every card has an access policy. The assembler enforces it, not the router.

- `speaker_known`: memories, rumors, claims, and deeds the speaker plausibly
  knows.
- `local_visible`: entities, items, and fixtures visible or obvious in the local
  scene.
- `listener_visible`: information visible on the player-controlled body, such
  as equipment and statuses.
- `public_world`: public laws, canon, region identity, and faction facts.
- `debug_perfect`: optional CLI/debug-only data. Never enabled for ordinary
  player-facing dialogue unless a test explicitly asks for perfect state.

This prevents the router from becoming an information leak. It can request
`rumors.full`; the engine still decides which rumors this speaker can plausibly
carry.

### Tiered NPC Knowledge

Sorcerer already has a small file-backed lore layer:

- `content/lore/*.md` supplies authored lore cards.
- `LoreCatalog` loads and normalizes those cards.
- `LoreRouter.Select` picks relevant sections by subjects, triggers, access
  level, and limit.

Dialogue should extend this into an NPC-knower system rather than adding a
second lore path. The WildMagic pattern to adopt is:

```text
speaker knowledge profile
-> access-gate eligible lore sections
-> route by player topic/subjects/triggers
-> inject selected section bodies into world_knowledge
```

The important rule is that inaccessible sections are hidden before routing. A
low-knowledge guard should not even expose the one-line description for deep
Vigovian policy lore; a local magistrate, scholar, or faction official can.

Implemented first-slice C# shape:

```csharp
public sealed record KnowledgeComponent(Dictionary<string, int> TopicTiers);
```

`TopicTiers` are sparse per-topic permissions such as `rumors: 2`,
`faction.law: 3`, `npc.knowledge.region: 2`, `hollowmere: 2`,
`folk_magic.water: 4`, or `scene.object: 1`. Tier meanings are:

- `0`: common knowledge, when common lore exists for the subject.
- `1`: basic familiarity.
- `2`: deep familiarity.
- `3`: specialist knowledge.
- `4`: secret knowledge.

The assembler first uses topic tiers to hide whole context cards before routing.
For authored lore, it also expands known topics into subject-specific access
aliases. This lets an NPC know Hollowmere culture at level 2, wild magic at
level 3, and Hollowmere water magic at level 4 without giving them level 4
access to every unrelated regional or imperial-law card. `LoreQuery.AccessLevel`
remains the broad compatibility floor; `LoreQuery.SubjectAccessLevels` raises
access only for matching card subjects.

Inaccessible sections are filtered before the generator sees them. The router
may know that an `npc.knowledge.region` card exists, but it never sees lore
bodies or section descriptions above the speaker's access. Truth simulation is
still deliberately light: the current layer exposes accessible beliefs and
authored lore, and future work can add belief variants without changing the
same gate.

Initial seeding can be deterministic:

- Commoner, prisoner, laborer, or ordinary guard: home region/faction level 1.
- Merchant, traveler, courier, or scout: home subject level 1 plus one or two
  route/faction subjects at level 1.
- Local elder, innkeeper, guide, or experienced resident: home region level 2.
- Soldier, clerk, censorate aide, magistrate, noble, or official: faction/law
  subjects level 2, with local region at level 1-2.
- Scholar, priest, philosopher, archivist, charter mage, or specialist:
  relevant tradition/faction subjects level 3-4.

The assembler should use this profile for `npc.knowledge.region` and any card
that carries authored canon, especially `faction.law`. Rumors and memories keep
their existing carrier/provenance gates; they are dynamic run knowledge, not
static lore tiers.

Opening seed example: Lio knows Hollowmere, local people, travel, and the current
zone at level 2; Hollowmere water magic at level 4; wild magic at level 3;
folk-magic services at level 2; oaths/promises at level 3; and Vigovian public
law only at level 1. That profile lets him answer with richer local texture
while still keeping imperial procedure and unrelated secrets out of his context.

## Budgeting

Cards should own explicit budgets:

- `base`: always-on and tiny.
- `small`: one focused object/person/service set.
- `medium`: a handful of memories, claims, or local entities.
- `large`: full heard rumor set, expanded conversation history, or travel
  context.

Recommended first caps:

- Maximum selected routed cards: 4, widened to 6 on low confidence or
  multi-question input.
- Maximum card payload items by default: 5.
- `rumors.full`: 12, with `truncated`.
- `zone.current` / `zone.npcs`: 10 visible entities/items/NPCs, ordered by
  distance and salience.
- `npc.relationship_memory`: 6, ordered by salience and recency.
- `claims.recent` / `promise.hooks`: 8, ordered by relevance, promise status,
  and recency.

The audit log should record bytes by card so budgets can be tuned from real
play.

## Examples

### Rumor Question

Player: "Lio, what rumors have you heard about the road east?"

Route:

- `rumors.full`
- `promise.hooks`
- `region.travel`

Not included:

- Full local object details.
- Full trade/service schemas.
- Faction ledgers except public details already in base.

### Object Question

Player: "What is that brass brazier for?"

Route:

- `scene.object_detail`, focus `brass brazier`
- `faction.law`, focus `containment`
- `recent.magic_deeds` only if the brazier has visible magical effects or recent
  magic touched it.

Not included:

- Full rumor set.
- Relationship memories unless the player frames the question personally.

### Faction/Law Question

Player: "What does the Censorate do to unlicensed mages?"

Route:

- `faction.law`, focus `Censorate unlicensed mages`
- `npc.knowledge.region`
- `claims.recent` only for local warrants, recent accusations, or active
  claims involving the speaker/player.

Knowledge gate:

- A frightened prisoner may know only the public version.
- A soldier may know procedure and penalties.
- A censorate clerk may know deliberate blind spots or local exceptions.

Not included:

- Full object details.
- Trade/service cards.
- Deep imperial lore above the speaker's knowledge tier.

### Relationship Question

Player: "I gave you grave salt. Do you trust me now?"

Route:

- `npc.relationship_memory`
- `recent.magic_deeds` if the speaker witnessed or heard relevant deeds
- `promise.hooks` only if the exchange references a prior promise/secret

Likely parser capabilities:

- `memory`
- `bond_want`

### Trade/Service Question

Player: "Can you sell me a blade or mend this cloak?"

Route:

- `services.available`
- `claims.recent`
- `scene.object_detail` only if `cloak` resolves to a visible/carried item with
  useful detail.

The model may reveal stock or a service. It must not complete a purchase.

### Local Action Question

Player: "Open the cell door and step aside."

Route:

- `scene.object_detail`
- `zone.npcs`
- `npc.relationship_memory` if refusal/trust context matters

Likely parser capabilities:

- `local_actions`

## Integration Points

Implemented first-slice core abstractions:

- `DialogueRouteRequest`
- `DialogueRouteResult`
- `IDialogueRouter`
- `IDialogueParserRouter`
- `DialogueContextCardPayload`
- `DialogueContextCardSpec`
- `DialogueRouteCandidate`
- `DialogueContextAssembler`
- `DialogueRouteRecord`
- `DialogueRouteMetrics`
- `KnowledgeComponent`
- `DialogueParserCapabilityCard`
- `DialogueParserRouteRecord`
- `DialogueParserRouteMetrics`

Still-planned refinement:

- `DialogueContextRegistry` if the static card spec list grows beyond a small
  table.

Provider implementations:

- `NullDialogueRouter`: reports no live route so `GameSession` falls back to
  deterministic balanced cards.
- `MockDialogueRouter`: deterministic and test-friendly.
- `OllamaDialogueRouter`: short JSON call, `think:false`.
- `OpenAiCompatibleDialogueRouter`: JSON-mode chat completion.
- `ReplayDialogueRouter`: feeds materialized route records during transcript
  replay.
- `DeterministicDialogueParserRouter`: keyword fallback over player text plus
  NPC speech.
- `OllamaDialogueParserRouter` and `OpenAiCompatibleDialogueParserRouter`:
  short JSON parser-capability routers under `dialogue_parser_router`.
- `ReplayDialogueParserRouter`: feeds materialized parser-route records during
  transcript replay.
- `DialogueParser`: the post-speech `IDialogueParser` lane. It emits the full
  dialogue proposal set for claims, memories, bond/want changes, actions,
  services, trade offers, and typed consequences. Claim-only extractors remain
  usable through the compatibility adapter.

Configuration:

- `dialogue_router` is implemented and defaults to the same provider, host, and
  model as `dialogue` to avoid local model thrash.
- Allow `SORCERER_DIALOGUE_ROUTER_*` overrides for small hosted/local router
  models.
- Allow disabling the LLM router while keeping deterministic fallback routing.
- `dialogue_parser_router` is implemented for post-speech capability routing and
  defaults local Ollama to CPU (`SORCERER_DIALOGUE_PARSER_ROUTER_NUM_GPU=0`).
- `dialogue_parser` is implemented for post-speech mechanical parsing and
  defaults local Ollama to CPU (`SORCERER_DIALOGUE_PARSER_NUM_GPU=0`). Users can
  override provider, host, model, timeout, concurrency, and GPU layers with the
  matching `SORCERER_DIALOGUE_PARSER_ROUTER_*` or
  `SORCERER_DIALOGUE_PARSER_*` variables.

`DialogueRequest` migration:

1. Keep existing fields for compatibility.
2. Add `ContextCards` and `SelectedContextCardIds`.
3. Populate old fields from equivalent cards during the migration.
4. Once providers and tests consume `ContextCards`, make the old flat fields
   compatibility-only or remove them in a narrow follow-up.

## Audit And Replay

Dialogue audit entries should include:

- route provider/model
- route request
- raw route response
- normalized selected cards
- deterministic fallback cards
- rejected/unknown cards
- card payload byte counts
- final dialogue request byte count
- router elapsed time
- final dialogue elapsed time
- parser purpose/provider/model
- parser elapsed time and fallback/timeout reason
- fallback reason, if any

Generated `talk` results now materialize a `DialogueRouteRecord` with the route
request, selected ids, deterministic fallback ids, provider, raw text, error
state, fallback flag, and `DialogueRouteMetrics`. Metrics include available,
selected, fallback, and denied card counts; denied card ids; unknown or
access-denied selected ids; route request bytes; available/selected card bytes;
generator request bytes; and router elapsed time. Dialogue audit entries carry
the same route record. Transcripts should also materialize parser records.
Replay runs the same assembler and final dialogue provider path from recorded
route output, then feeds recorded parser results rather than calling live parser
providers.

Save files should not serialize pending route tasks; routing is foreground and
short. If a future implementation makes routing asynchronous, `save` must flush
it the same way it flushes pending dialogue extraction.

## Latency Policy

The router is only worth having if total time to visible NPC speech improves or
quality improves enough to justify the extra hop.

Rules:

- Every generated dialogue turn runs the routing layer. The routing layer may
  be deterministic-only, live-model-backed, or replay-fed, but the final request
  should come from selected cards.
- The router prompt is tiny: speaker/listener summary, latest player line,
  nearby names, and a one-line card index. No ledgers.
- Use `think:false` on Ollama routes.
- Use the `dialogue` model by default to avoid local model unload/reload.
- Allow deterministic-only routing when live routing is disabled, times out, or
  audits show no benefit.
- Keep the generator blind to mechanical schemas; smaller foreground prompts
  help directness and preserve NPC voice.
- Run parser-router and parser work after visible speech, ideally under
  `dialogue_parser_router` and `dialogue_parser` on CPU or a separate
  background host/model. Parser completion can apply on the next command, world
  pump, or save flush.
- Keep a short router timeout. If it expires, fall back and continue.
- Log route elapsed time, final dialogue elapsed time, and final request bytes
  together. Router success is measured by end-to-end command-to-visible-text
  time, not by route-call speed alone.
- Treat prompt-token stats as the source of truth for live wire size. Some audit
  byte metrics describe the authoritative request object, which intentionally
  remains richer than the compact provider wire.
- Enforce the live LLM route when it succeeds. If the route fails, times out, or
  selects no valid knowledge-gated cards, fall back to deterministic balanced
  cards and continue to generation.

The live router should be a quality and context-budget tool. If audits show that
deterministic routing already selects the right compact cards for trivial small
talk, configuration may use deterministic routing for those turns while keeping
the same route-result and card-assembly path.

## Failure Modes

Router technical failure:

- Audit it.
- Fall back to deterministic balanced cards.
- Continue to final dialogue.
- Do not consume a turn unless final dialogue succeeds.

Router under-selects:

- If no valid selected cards remain, deterministic balanced fallback catches
  common categories.
- Low confidence may widen cards in a later router schema.
- The final provider may return a `needs_context` diagnostic only in debug
  builds or eval mode; ordinary player-facing dialogue should answer in
  character with the context provided.

Router over-selects:

- Hard budgets and card caps prevent prompt explosions.
- Route metrics reveal chronic over-selection through available, selected, and
  generator byte counts.

Router requests forbidden or unknown state:

- Ignore it.
- Audit `unknown_card` or `access_denied`.
- Do not expose hidden state.

Parser proposes an action outside routed capability detail:

- Early implementation: parse and validate as today, but keep the selected
  capability ids in the parser-route audit.
- Later implementation: reject or schema-block only after evals prove routing
  recall is strong.

## Testing

- Router unit table: player line plus speaker summary -> expected card ids.
- Negative routing: "thank you" should not pull `rumors.full`,
  `services.available`, or `scene.object_detail`.
- Focus binding: "that brazier" selects `scene.object_detail` and resolves to
  the visible brazier id through the engine binder.
- Access policy: `rumors.full` returns only speaker/region-carried rumors.
- Knowledge gate: `npc.knowledge.region` exposes public law to an ordinary
  guard, deeper Censorate detail to an official, and no inaccessible section
  descriptions to the router.
- Budget test: a region with 100 rumors returns capped `rumors.full` with
  `truncated: true`.
- Proposal-family routing: rumor, object, and faction questions include the
  expected parser-detail families and keep unrelated families compact.
- Failure fallback: router timeout still reaches final dialogue with balanced
  deterministic cards.
- Replay: recorded route output replays without live router calls.
- Parser lane: speech-only generated dialogue queues exactly one parser task;
  proposal-bearing legacy/replay dialogue still applies proposals directly and
  does not duplicate parser work.
- Audit: route records include card ids, byte counts, elapsed time, and fallback
  reasons.
- Parity: CLI and GUI `talk` share the same route and final request.
- CPU parser config: Ollama parser-router and parser requests include
  `num_gpu=0` by default and honor
  `SORCERER_DIALOGUE_PARSER_ROUTER_NUM_GPU` and
  `SORCERER_DIALOGUE_PARSER_NUM_GPU`.

## Migration Plan

1. Add card payloads and a deterministic assembler. Done as
   `DialogueContextAssembler` with a small `DialogueContextCardSpec` registry.
2. Add speaker `KnowledgeComponent`/knowledge-profile seeding for authored
   world knowledge, reusing `LoreCatalog` and `LoreRouter` rather than creating
   a parallel lore registry. `KnowledgeComponent`,
   `DialogueKnowledgeProfile`, opening/generated NPC seeding, tier 0 common
   lore, subject-specific lore access, and regional lore-card access are done;
   broader authored tier catalogs remain.
3. Add `ContextCards` to `DialogueRequest` and materialized route records. Done.
4. Replace fixed lanes (`RecentRumors`, `RecentClaims`, broad scene lists) with
   card-derived payloads while leaving equivalent compatibility fields populated
   for providers/tests. Done for generated dialogue requests.
5. Prioritize the first routed lanes: `rumors.full`, `scene.object_detail`,
   `faction.law`/`npc.knowledge.region`, plus the high-value zone NPC,
   travel, promise-hook, and recent-magic cards.
6. Add `IDialogueRouter` with `Null` and `Mock` implementations. Done.
7. Add live Ollama/OpenAI-compatible routers under `dialogue_router`, defaulted
   to the dialogue model. Done.
8. Move generated dialogue prompts to speech-only and run parser work through
   `dialogue_parser` after visible speech. Done for the current live providers:
   `IDialogueParser` materializes full proposal records, while
   `IDialogueClaimExtractor` remains a compatibility adapter.
9. Add richer route audit byte counts and elapsed-time reporting. Done for
   context route request/card/generator byte counts, parser route
   request/capability/parser bytes, denied/unknown ids, and router/parser elapsed
   time; final dialogue elapsed time remains an eval/audit follow-up.
10. Evaluate enforced routed context against live transcripts for directness,
   specificity, and claim quality.
11. Route parser proposal-family prompt detail. Done with parser capability
   cards; stricter dynamic schemas remain a future eval-gated option.

## One-Line Rule

The dialogue router chooses **which windows to open**, not what is outside them.
The engine opens those windows from authoritative state, and the final dialogue
response still goes through the same parse, normalize, validate, and consequence
application pipeline.
