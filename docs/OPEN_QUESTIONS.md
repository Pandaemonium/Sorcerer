# Open Questions

These questions are still genuinely open. Many earlier ones have since been settled - the
Godot project shape, the first scenario, and the world/content canon are now answered (see
[DESIGN_DECISIONS.md](DESIGN_DECISIONS.md), [WORLDBUILDING.md](WORLDBUILDING.md), and the built
first-playable slice) and have been removed from this list.

## Magic Resolver

- How should "interesting magic" be scored in evals, beyond human review?
- Should capability-card routing stay keyword-based, or add embeddings as the operation set
  grows?

## Casting Minigames

- What is the first minigame: rune tracing, rhythm, path drawing, symbol matching, or something
  else?
- Beyond feeding the resolver prompt, when should the engine apply a deterministic performance
  modifier?
- How should accessibility options present neutral casting?

## Promises

- How much should the player journal reveal versus keep mysterious as realization grows richer?
- Which realization archetypes are worth building next, beyond the current read/open/talk
  anchors?

## Persistence

- What save format should be used once saves begin?
- How much schema versioning should be introduced before actual saves?
- Should replay records be stored beside audit logs, under runs, or in a separate folder?

## Collaboration

- What branch and PR workflow should the new repo use?
- Which docs must be updated in every architecture-changing PR?
- How should agent-authored playtest reports be stored?
