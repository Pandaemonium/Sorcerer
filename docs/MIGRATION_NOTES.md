# Migration Notes From Wild Magic

Sorcerer is not a direct port. These notes identify lessons from Wild Magic that should
shape the new implementation.

## Keep The Strong Contracts

Wild Magic's most valuable architectural contract was that the GUI and CLI both routed
through a shared session/action layer. Sorcerer should make that the foundation from the
first commit.

Preserve:

- one backend for all interfaces
- action results for every command
- read-only state views
- audit records for live model calls
- mock provider for fast playtests
- technical LLM failures that do not consume turns
- intentional magical rejections that do consume turns

## Simplify The Shape

The clean-slate port should avoid carrying over systems only because they exist.

Likely redesign targets:

- props and items as separate concepts
- duplicated prompt/context paths
- overly broad experimental world systems before the core loop is fun
- fallback logic that risks becoming a second hidden spell engine
- UI screens that can drift from CLI capability
- persistent facts that exist only as prose and cannot be inspected structurally
- LLM calls that decide whether obvious interactions such as trade are happening

## Preserve The Spirit

Wild Magic's central promise remains right: the player should be able to type strange
spell ideas and usually get a meaningful, reusable mechanical result.

Examples the new architecture should eventually express through general systems:

- bind a creature in blue glass
- summon a brass moth familiar
- turn a prop into a spell anchor
- animate a statue
- transform an item into a reagent
- write a debt into the future
- create a promise that generation later fulfills
- possess another body and continue playing from there

## Do Not Port By Module

Do not recreate a Python file structure in C# by habit.

Port by contract:

- What player capability exists?
- What engine primitive supports it?
- What state must persist?
- What view must GUI, CLI, agents, and prompts receive?
- What tests prove the contract?

## First Playable Target

The first useful Sorcerer build should be small but honest:

- deterministic test chamber
- player movement
- basic combat
- inspectable entities
- inventory and interactables
- ASCII GUI
- headless CLI
- mock spell resolution
- initial live-provider spell resolution
- audit logs

The live provider does not have to be Ollama specifically. The important contract is that a
real provider can exercise the spell resolver early.

That is enough to prove the architecture before larger systems arrive.
