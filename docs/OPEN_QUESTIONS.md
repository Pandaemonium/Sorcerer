# Open Questions

These questions are still genuinely open. Many earlier architecture questions are now
settled in [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md).

## Godot Project Shape

- Should the first CLI executable live in the same solution as the Godot project from the
  first commit, or be introduced immediately after the Godot shell exists?
- Which Godot version should be pinned for the initial project?
- Which C# test runner should be used first?

## First Scenario

- What is the first playable location: dungeon, imperial holding room, frontier ruin,
  academy escape, village outskirts, or another small site?
- Which enemies and neutral NPCs best prove the resolver without creating too much content
  burden?
- What first promise should be easy to trigger in the test chamber?

## Magic Resolver

- Which live provider should be recommended first: Ollama, an OpenAI-compatible local
  server, a hosted API provider, or a configurable default with no recommendation?
- What model should be the first known-good baseline for spell resolution?
- How should "interesting magic" be scored in evals beyond human review?
- Should capability-card routing start keyword-only, or include embeddings from the first
  live resolver milestone?

## Casting Minigames

- What is the first minigame: rune tracing, rhythm, path drawing, symbol matching, or
  something else?
- Should minigames affect only prompt context at first, or should the engine also apply a
  deterministic performance modifier?
- How should accessibility options present neutral casting?

## Promises

- How mysterious should the normal player journal be in the first playable?
- Which promise types should exist in the first implementation: prophecy, debt, quest,
  omen, threat, or only one or two?
- What is the minimum realization behavior that proves promises matter?

## Content Direction

- What is the Empire called?
- What is the emperor called?
- What is the first named region?
- Which tradition should define the first region?
- What are the first player origins?

## Persistence

- What save format should be used once saves begin?
- How much schema versioning should be introduced before actual saves?
- Should replay records be stored beside audit logs, under runs, or in a separate folder?

## Collaboration

- What branch and PR workflow should the new repo use?
- Which docs must be updated in every architecture-changing PR?
- How should agent-authored playtest reports be stored?

