# Significant Interiors

Sorcerer's ordinary homes, stalls, workshops, and rooms remain part of their district map.
Separate zones are reserved for places whose threshold, scale, access, or persistence matters:
palaces, archives, sanctums, great halls, public works, dungeons, and impossible spaces.

The first slice is one zone per significant interior. Every regional primary settlement has one;
the Vigovian Capital has both the Palace of Reasonable Peace and the Archive of Moving
Responsibility. A building may feel larger inside because the interior is a full tactical zone,
but multi-zone buildings remain deferred until ordinary settlement traversal is consistently fun.

## Authoritative model

Interior content lives in `content/regions/initial_interiors.json` and attaches to the existing
region catalog. Each definition supplies:

- a stable id, name, kind, summary, floor, wall material, and semantic tags;
- a district binding that determines which primary-settlement zone owns the threshold;
- `public` or `restricted` access, plus an optional ordinary key/item route;
- culturally specific semantic fixtures with names, descriptions, glyphs, materials, tags, and
  optional collision.

Generation creates the exterior threshold as an ordinary unified entity with
`InteriorEntranceComponent`, `InteractableComponent`, fixture, position, render, physical,
description, and tags. It creates the return threshold with `InteriorExitComponent`. The same
entities appear in `GameView`, resolver context, dialogue scenery, CLI observations, persistence,
and spell targeting. There is no renderer-owned doorway or second interior scene system.

Entering lazily generates or restores a stable zone id of the form
`interior:<region>:<interior>:<exterior-zone>`. Interior terrain, exit, fixtures, and residents are
created in the detached generation sandbox. Walls use hidden `set_terrain`; fixtures and exits use
`spawn_fixture`; residents use the existing regional population and `spawn_entity` paths. Bound
place promises may realize there through the same generated-zone apply point.

Before a transition, the active zone is captured without the controlled body or bond-followers.
The destination snapshot is restored, those travelers are placed through hidden `move_entity`
consequences, and the selected tactical target is cleared through `set_selected_target`. Exiting
performs the inverse operation. Consequently, moved fixtures, magical terrain, residents, items,
tile flows, exploration, and promise payoffs remain exactly where the player left them.

## Access and time

`enter [target]` and `leave` are shared typed commands. They are exposed as nearby context actions
in Godot and accepted as text or JSON by the CLI. A successful crossing consumes one ordinary
turn, allowing the normal turn and actor pumps to run. It does not independently pump the bounded
world-turn system: crossing a threshold is not elapsed travel time.

Public thresholds admit immediately. Restricted thresholds are soft world problems, never story
locks. Access succeeds through any of these general state routes:

- carrying the definition's required item;
- a permission/access world flag for the interior;
- an `open`, `unlocked`, `forced_open`, `access_granted`, or `permission_granted` tag on the
  threshold, including tags created by validated wild magic or another typed consequence.

A failed attempt explains the available categories, consumes no turn, and changes no state. Keys
are evidence of access rather than single-use tolls, so crossing does not silently consume them.
Later stealth, force, dialogue, faction, and service work should grant one of these ordinary state
lanes instead of adding special palace or archive flags.

## Current authored set

The regional set includes an evidence registry, memory house, afterlight conservatory, many-witness
hall, oath court, counterloom, civic letterhouse, mare-dream house, ancestor guesthouse, curse-map
vault, third-water house, submerged exchange, and after-festival tent, plus the capital palace and
living archive. Their fixtures are semantic mechanics: evocative prose and tags remain dormant
until dialogue or the resolver squarely calls them into a validated consequence.

## Deliberate limits

- Ordinary interiors are not separate zones.
- Interior layouts currently use one spacious room with a hard perimeter, authored fixtures, and
  a small local population; room subdivision is a later general layout grammar.
- Destroying or transforming a return threshold can make `leave` unavailable. This is intentional
  world state, not an invisible fallback; magic, restoration, teleportation, or another created
  route should solve it through ordinary mechanics.
- Diegetic fast travel is a separate future system. It advances elapsed world time; an interior
  threshold does not.
