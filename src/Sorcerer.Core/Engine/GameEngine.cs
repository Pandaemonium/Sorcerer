using Sorcerer.Core.Characters;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Status;
using Sorcerer.Core.Validation;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine;

internal sealed record DeedCaptureResult(DeedCapturePlan Plan, IReadOnlyList<StateDelta> Deltas);

public sealed class GameEngine
{
    private readonly AiSystem _aiSystem;
    private readonly BodySwapSystem _bodySwapSystem;
    private readonly GenerationSystem _generationSystem;
    private readonly InteractionSystem _interactionSystem;
    private readonly ItemCatalog _itemCatalog = ItemCatalog.LoadDefault();
    private readonly LoreCatalog _loreCatalog = LoreCatalog.LoadDefault();
    private readonly InventoryService _inventoryService;
    private readonly ItemSystem _itemSystem;
    private readonly MovementSystem _movementSystem;
    private readonly ObjectiveProgressSystem _objectiveProgressSystem;
    private readonly PerceptionSystem _perceptionSystem;
    private readonly PersistentEffectSystem _persistentEffects;
    private readonly StatusRegistry _statusRegistry = StatusRegistry.CreateDefault();
    private readonly TurnSystem _turnSystem;
    private readonly EngineViewBuilder _viewBuilder;
    private readonly WorldConsequenceApplier _worldConsequences;
    private readonly WorldReactionSystem _worldReactions = new();

    public GameEngine(GameState state, IBackgroundTextGenerator? backgroundTextGenerator = null)
    {
        State = state;
        CharacterMath.EnsureCharacterState(State);
        _worldConsequences = new WorldConsequenceApplier(State, this);
        _aiSystem = new AiSystem(this, _statusRegistry);
        _bodySwapSystem = new BodySwapSystem(this, _statusRegistry);
        _generationSystem = new GenerationSystem(State, _itemCatalog, _loreCatalog, ApplyConsequence);
        _inventoryService = new InventoryService(_itemCatalog);
        _itemSystem = new ItemSystem(this, _itemCatalog, _inventoryService);
        _movementSystem = new MovementSystem(this, _statusRegistry);
        _objectiveProgressSystem = new ObjectiveProgressSystem(this);
        _perceptionSystem = new PerceptionSystem(State, _statusRegistry);
        _persistentEffects = new PersistentEffectSystem(this);
        _turnSystem = new TurnSystem(this, State, _statusRegistry, _loreCatalog, backgroundTextGenerator);
        _interactionSystem = new InteractionSystem(this, _itemSystem, _turnSystem);
        _viewBuilder = new EngineViewBuilder(this, _inventoryService, _statusRegistry, _perceptionSystem, _generationSystem, _loreCatalog);
        RecordControlledExploration("perception_init", "recordInitialExploration");
    }

    public GameState State { get; }

    public StatusRegistry Statuses => _statusRegistry;

    internal ClaimSeed? CreateObjectiveHandoff(Entity speaker, string trigger) =>
        _generationSystem.CreateObjectiveHandoff(speaker, trigger);

    internal WorldConsequenceApplyResult? ApplyGeneratedObjectiveHandoff(Entity speaker, string trigger) =>
        _interactionSystem.ApplyGeneratedObjectiveHandoff(speaker, trigger);

    public IReadOnlyList<StateDelta> EvaluateObjectiveProgress(ActionResult action) =>
        _objectiveProgressSystem.Evaluate(action);

    public WorldConsequenceApplyResult ApplyConsequence(WorldConsequence consequence) =>
        WorldConsequenceGuard.Apply(State, consequence, _worldConsequences.Apply);

    public ActionResult MoveControlled(Direction direction) => _movementSystem.MoveControlled(direction);

    public ActionResult Wait() => _movementSystem.Wait();

    public ActionResult Travel(Direction direction)
    {
        var turnBefore = State.Turn;
        var pursuers = _aiSystem.PursuersForTravel();
        var travelDeltas = _generationSystem.Travel(direction, pursuers);
        var turnDeltas = AdvanceTurn();
        var pursuitDeltas = pursuers.Count == 0
            ? Array.Empty<StateDelta>()
            : ApplyConsequence(WorldConsequence.Message(
                "travel",
                pursuers.Count == 1
                    ? "A hostile crosses the boundary after you; this pursuit is not escaped."
                    : $"{pursuers.Count} hostiles cross the boundary after you; this pursuit is not escaped.",
                targetEntityId: State.ControlledEntityId.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: "Engaged hostiles saw the controlled body leave the zone.",
                operation: "pursuitContinues")).Deltas.ToArray();
        var deltas = travelDeltas.Concat(turnDeltas).Concat(pursuitDeltas).ToArray();
        return new ActionResult
        {
            Action = "travel",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = deltas.PlayerMessages().ToArray(),
            Deltas = deltas,
        };
    }

    /// <summary>
    /// Cross several uneventful zone legs toward a named place.  The route pauses for at most two
    /// ambient generated scenes; authored promise payoffs, the destination, and pursuit interrupt
    /// immediately and do not consume that budget.
    /// </summary>
    public ActionResult Journey(string destinationText)
    {
        var turnBefore = State.Turn;
        var destination = _generationSystem.ResolveJourneyDestination(destinationText);
        if (destination is null)
        {
            return ActionResult.Simple(
                "journey",
                false,
                false,
                turnBefore,
                State.Turn,
                $"No mapped destination matches '{destinationText}'. Use atlas to review known place names.");
        }

        var active = State.PromiseLedger.Promises
            .LastOrDefault(promise => promise.Kind.Equals("journey", StringComparison.OrdinalIgnoreCase)
                && promise.Status.Equals("active", StringComparison.OrdinalIgnoreCase));
        if (active?.Journey is not null
            && !active.Journey.DestinationZoneId.Equals(destination.ZoneId, StringComparison.OrdinalIgnoreCase))
        {
            ApplyConsequence(WorldConsequence.UpdatePromise(
                "journey",
                active.Id,
                status: "abandoned",
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: "The player named a different overland destination.",
                operation: "abandonJourney"));
            active = null;
        }

        JourneyPlan plan;
        if (active?.Journey is { } existingPlan)
        {
            plan = existingPlan;
        }
        else
        {
            plan = new JourneyPlan(
                destination.Id,
                destination.Name,
                destination.ZoneId,
                SceneBudget: 2,
                StartedTurn: State.Turn);
            var created = ApplyConsequence(WorldConsequence.CreatePromise(
                "journey",
                "journey",
                $"Travel to {destination.Name}.",
                triggerHint: "journey",
                visibility: WorldConsequenceVisibility.Journal,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: "The player deliberately began a named overland journey.",
                operation: "beginJourney",
                playerVisible: true,
                salience: 3,
                claimedPlace: destination.ZoneId,
                bindPlace: destination.ZoneId,
                useCurrentRegionAsClaimedPlace: false,
                emitMessage: false,
                journey: plan));
            active = State.PromiseLedger.Promises.FirstOrDefault(promise => promise.Id == created.TargetId)
                ?? State.PromiseLedger.Promises.Last(promise => promise.Journey is not null);
        }

        if (_generationSystem.JourneyDistanceTo(destination) == 0)
        {
            ApplyConsequence(WorldConsequence.UpdatePromise(
                "journey", active.Id, status: "completed", realizedIn: State.CurrentZoneId,
                sourceEntityId: State.ControlledEntityId.Value, evidence: "The journey destination is the current zone.",
                operation: "completeJourney"));
            return ActionResult.Simple("journey", true, false, turnBefore, State.Turn, $"You are already at {destination.Name}.");
        }

        var deltas = new List<StateDelta>();
        var crossedThisCommand = 0;
        var interruptedBy = "progress";
        const int ambientStride = 3;
        const int safetyLegLimit = 48;
        while (_generationSystem.JourneyDistanceTo(destination) > 0 && crossedThisCommand < safetyLegLimit)
        {
            var pursuers = _aiSystem.PursuersForTravel();
            var direction = _generationSystem.JourneyDirectionTo(destination);
            var travel = _generationSystem.Travel(direction, pursuers);
            deltas.AddRange(travel);
            deltas.AddRange(AdvanceTurn());
            crossedThisCommand++;
            plan = plan with { ZonesCrossed = plan.ZonesCrossed + 1 };

            if (pursuers.Count > 0)
            {
                interruptedBy = "pursuit";
                var pursuit = ApplyConsequence(WorldConsequence.Message(
                    "journey",
                    pursuers.Count == 1
                        ? "Your route breaks into a chase: a hostile follows you across the boundary."
                        : $"Your route breaks into a chase: {pursuers.Count} hostiles follow you across the boundary.",
                    targetEntityId: State.ControlledEntityId.Value,
                    visibility: WorldConsequenceVisibility.Message,
                    sourceEntityId: State.ControlledEntityId.Value,
                    evidence: "An engaged hostile crossed during compressed journey travel.",
                    operation: "journeyInterruptedByPursuit"));
                deltas.AddRange(pursuit.Deltas);
                break;
            }

            if (_generationSystem.JourneyDistanceTo(destination) == 0)
            {
                interruptedBy = "destination";
                break;
            }

            if (travel.Any(delta => delta.Operation.Equals("realizePromise", StringComparison.OrdinalIgnoreCase)))
            {
                interruptedBy = "promise";
                break;
            }

            if (plan.ScenesSpent < plan.SceneBudget && crossedThisCommand >= ambientStride)
            {
                plan = plan with { ScenesSpent = plan.ScenesSpent + 1 };
                interruptedBy = "ambient_scene";
                var pause = ApplyConsequence(WorldConsequence.Message(
                    "journey",
                    $"You pause where the road has become interesting (journey scene {plan.ScenesSpent}/{plan.SceneBudget}). Continue with 'journey {destination.Name}' when ready.",
                    targetEntityId: State.ControlledEntityId.Value,
                    visibility: WorldConsequenceVisibility.Message,
                    sourceEntityId: State.ControlledEntityId.Value,
                    evidence: "The bounded ambient-scene budget paused a compressed route.",
                    operation: "journeyScene"));
                deltas.AddRange(pause.Deltas);
                break;
            }
        }

        var complete = _generationSystem.JourneyDistanceTo(destination) == 0;
        var updated = ApplyConsequence(WorldConsequence.UpdatePromise(
            "journey",
            active.Id,
            status: complete ? "completed" : "active",
            realizedIn: complete ? State.CurrentZoneId : null,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: "Journey progress was committed after all crossed legs succeeded.",
            operation: complete ? "completeJourney" : "updateJourney",
            emitMessage: complete,
            message: complete ? $"You reach {destination.Name}." : null,
            journey: plan,
            details: new Dictionary<string, object?>
            {
                ["destination"] = destination.Name,
                ["destinationZoneId"] = destination.ZoneId,
                ["zonesCrossed"] = crossedThisCommand,
                ["totalZonesCrossed"] = plan.ZonesCrossed,
                ["scenesSpent"] = plan.ScenesSpent,
                ["sceneBudget"] = plan.SceneBudget,
                ["interruptedBy"] = interruptedBy,
                ["complete"] = complete,
            }));
        deltas.AddRange(updated.Deltas);

        return new ActionResult
        {
            Action = "journey",
            Success = true,
            ConsumedTurn = crossedThisCommand > 0,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = deltas.PlayerMessages().ToArray(),
            Deltas = deltas,
        };
    }

    public ActionResult EnterInterior(string? target)
    {
        var turnBefore = State.Turn;
        var transition = _generationSystem.EnterInterior(target);
        if (!transition.Success)
        {
            return ActionResult.Simple(
                "enter",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
                transition.Error ?? "You cannot enter here.");
        }

        var turnDeltas = AdvanceTurn();
        var deltas = transition.Deltas.Concat(turnDeltas).ToArray();
        return new ActionResult
        {
            Action = "enter",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = deltas.PlayerMessages().ToArray(),
            Deltas = deltas,
        };
    }

    public ActionResult LeaveInterior()
    {
        var turnBefore = State.Turn;
        var transition = _generationSystem.LeaveInterior();
        if (!transition.Success)
        {
            return ActionResult.Simple(
                "leave",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
                transition.Error ?? "You cannot leave from here.");
        }

        var turnDeltas = AdvanceTurn();
        var deltas = transition.Deltas.Concat(turnDeltas).ToArray();
        return new ActionResult
        {
            Action = "leave",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = deltas.PlayerMessages().ToArray(),
            Deltas = deltas,
        };
    }

    public ActionResult Atlas()
    {
        var turn = State.Turn;
        return ActionResult.Simple(
            "atlas",
            true,
            false,
            turn,
            turn,
            _generationSystem.AtlasLines().ToArray());
    }

    /// <summary>
    /// The current region's short voice spec (docs/AESTHETICS_AND_TONE.md, region-by-region voice).
    /// Used to steer NPC dialogue toward the local register, the same block the resolver lens reads.
    /// </summary>
    public string CurrentRegionVoice => _generationSystem.CurrentRegion.VoiceSummary;

    public WorldPlaceProfile CurrentPlace => _generationSystem.CurrentPlace;

    public NearestSettlement CurrentNearestSettlement => _generationSystem.CurrentNearestSettlement;

    public IReadOnlyList<StateDelta> RunActorTurns() => _aiSystem.RunActorTurns();

    public ActionResult Inspect()
    {
        var player = State.ControlledEntity;
        var position = player.Get<PositionComponent>().Position;
        var actor = player.Get<ActorComponent>();
        var perception = _perceptionSystem.RefreshControlled();
        var messages = new List<string>
        {
            $"Turn {State.Turn}. You are at {position.X},{position.Y}.",
            $"HP {actor.HitPoints}/{actor.MaxHitPoints}; MP {actor.Mana}/{actor.MaxMana}.",
        };
        if (_viewBuilder.ObjectiveCards().FirstOrDefault() is { } objective)
        {
            messages.Add($"Next: {objective.NextStep}");
        }

        foreach (var entity in State.Entities.Values.OrderBy(e => e.Id.Value))
        {
            if (entity.Id == State.ControlledEntityId)
            {
                continue;
            }

            if (!entity.TryGet<PositionComponent>(out var entityPosition))
            {
                continue;
            }

            if (perception.VisibleEntityIds.Contains(entity.Id))
            {
                var label = entity.TryGet<ActorComponent>(out var entityActor) && !entityActor.Alive
                    ? $"{entity.Name}'s corpse"
                    : entity.Name;
                messages.Add($"{label} at {entityPosition.Position.X},{entityPosition.Position.Y}.");
            }
        }

        var threats = DescribeThreats();
        if (threats.Count > 0)
        {
            messages.Add("Threats:");
            foreach (var threat in threats)
            {
                messages.Add($"  {threat.Name} is {threat.Telegraph}.");
            }
        }

        return ActionResult.Simple(
            "inspect",
            success: true,
            consumedTurn: false,
            State.Turn,
            State.Turn,
            messages.ToArray());
    }

    // The AI pursues within this Chebyshev distance and strikes when adjacent (see AiSystem); the
    // telegraph reads the same reach so what it foretells is exactly what the enemy will do.
    private const int HostileEngagementRange = 8;

    /// <summary>
    /// Generous, deterministic combat telegraphs (tactical-mastery pillar): every hostile the
    /// sorcerer can perceive within engagement range, nearest first, with what it is poised to do
    /// under the ordinary pursue-and-strike AI. Read by the observation and the structured view so
    /// the player is never ambushed -- a death should read as a wasted turn, not a surprise.
    /// </summary>
    public IReadOnlyList<ThreatCard> DescribeThreats()
    {
        var player = State.ControlledEntity;
        if (!player.TryGet<PositionComponent>(out var position)
            || !player.TryGet<ActorComponent>(out var actor)
            || !actor.Alive)
        {
            return Array.Empty<ThreatCard>();
        }

        var origin = position.Position;
        var threats = new List<ThreatCard>();
        foreach (var entity in State.Entities.Values)
        {
            if (entity.Id == State.ControlledEntityId
                || !entity.TryGet<ActorComponent>(out var stats)
                || !stats.Alive
                || !entity.TryGet<PositionComponent>(out var entityPosition))
            {
                continue;
            }

            // Telegraph what this actor will do to the player. Relationship hostility can be
            // asymmetric (a claimant may honor a personal settlement even while the player still
            // opposes their institution), so use the same actor -> player orientation as AiSystem.
            if (!IsHostile(entity, player) || !CanPerceiveSubject(player, entity))
            {
                continue;
            }

            var distance = Distance(origin, entityPosition.Position);
            if (distance > HostileEngagementRange)
            {
                continue;
            }

            var (held, subdued) = HostileRestraint(entity);
            var compelled = ActiveCompelledThreatState(entity);
            var archetype = AiSystem.ArchetypeFor(entity);
            var committed = entity.TryGet<BehaviorTagsComponent>(out var behaviors)
                && behaviors.Tags.TryGetValue("tactical_committed", out var commitmentExpiry)
                && (commitmentExpiry is null || commitmentExpiry > State.Turn);
            var rangedIntent = archetype is not null
                && (archetype.BehaviorTags.Contains("ranged", StringComparer.OrdinalIgnoreCase)
                    || archetype.BehaviorTags.Contains("caster", StringComparer.OrdinalIgnoreCase));
            var intentReach = rangedIntent ? 5 : 1;
            var willCommit = compelled is null
                && !held
                && !subdued
                && archetype is not null
                && !committed
                && distance <= intentReach;
            // A hostile the shared AI cannot act with (rooted/frozen/stunned) will neither advance
            // nor strike this turn, so it is not imminent and the telegraph must say so instead of
            // falsely reading "closing in" (GameView: the telegraph reflects changed state).
            var imminent = distance <= 1 && !subdued && compelled is null && archetype is null;
            var direction = WorldPlaceGraph.DirectionText(
                entityPosition.Position.X - origin.X,
                entityPosition.Position.Y - origin.Y);
            var rangeText = distance == 1 ? "1 tile" : $"{distance} tiles";
            var telegraph = held
                ? $"bound in place to the {direction}, {rangeText} away — it cannot move or strike"
                : subdued
                    ? $"reeling to the {direction}, {rangeText} away — it cannot act"
                    : compelled is not null
                        ? $"{compelled.Telegraph} to the {direction}, {rangeText} away — it will not make its ordinary attack"
                    : willCommit
                        ? $"poised to commit an intent to the {direction}, {rangeText} away: {archetype!.Intent}; counter: {archetype.Counter}"
                    : committed && archetype is not null
                        ? $"committed to the {direction}, {rangeText} away: {archetype.Intent}; counter: {archetype.Counter}"
                    : imminent
                        ? $"in striking range to the {direction} — it strikes if you hold position"
                        : archetype is not null
                            ? $"closing in from the {direction}, {rangeText} away; intent: {archetype.Intent}; counter: {archetype.Counter}"
                            : $"closing in from the {direction}, {rangeText} away";
            threats.Add(new ThreatCard(
                entity.Id.Value,
                entity.Name,
                distance,
                compelled is null && (imminent || committed),
                telegraph,
                compelled?.Intent ?? archetype?.Intent,
                compelled is null ? archetype?.Counter : null,
                compelled is not null
                    ? "An active magical compulsion currently suppresses its ordinary attack."
                    : archetype is null ? null : "Inspect, counter with a matching item, brace in defensive gear, move, bargain, or cast."));
        }

        return threats
            .OrderBy(threat => threat.Distance)
            .ThenBy(threat => threat.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record CompelledThreatState(string Intent, string Telegraph);

    private CompelledThreatState? ActiveCompelledThreatState(Entity entity)
    {
        if (!entity.TryGet<BehaviorTagsComponent>(out var behaviors))
        {
            return null;
        }

        bool Active(string tag) => behaviors.Tags.TryGetValue(tag, out var expiry)
            && (expiry is null || expiry > State.Turn);
        return Active("freeze_dread")
            ? new("freeze in dread", "frozen by dread")
            : Active("dance")
                ? new("dance instead of attacking", "compelled to dance")
                : Active("coward")
                    ? new("flee from you", "compelled to flee")
                    : Active("mimic")
                        ? new("copy your last movement", "compelled to mimic your movement")
                        : null;
    }

    public ActionResult Threats()
    {
        var threats = DescribeThreats();
        var messages = threats.Count == 0
            ? new[] { "You perceive no immediate threats." }
            : threats.Select(threat =>
            {
                var details = new List<string> { $"{threat.Name}: {threat.Telegraph}." };
                if (!string.IsNullOrWhiteSpace(threat.Intent)
                    && !threat.Telegraph.Contains(threat.Intent, StringComparison.OrdinalIgnoreCase))
                {
                    details.Add($"Intent: {threat.Intent}.");
                }
                if (!string.IsNullOrWhiteSpace(threat.Counter)
                    && !threat.Telegraph.Contains(threat.Counter, StringComparison.OrdinalIgnoreCase))
                {
                    details.Add($"Counter: {threat.Counter}.");
                }
                if (!string.IsNullOrWhiteSpace(threat.EquipmentHint))
                {
                    details.Add(threat.EquipmentHint);
                }

                return string.Join(" ", details);
            }).ToArray();
        return ActionResult.Simple("threats", true, false, State.Turn, State.Turn, messages);
    }

    /// <summary>Whether an active status leaves a hostile unable to advance (BlocksMovement) or
    /// unable to act at all (BlocksAction) this turn, so the threat telegraph can stay honest.</summary>
    private (bool Held, bool Subdued) HostileRestraint(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return (false, false);
        }

        var held = false;
        var subdued = false;
        foreach (var status in container.Statuses)
        {
            if (status.ExpiresTurn is not null && status.ExpiresTurn <= State.Turn)
            {
                continue;
            }

            held |= _statusRegistry.BlocksMovement(status.Id);
            subdued |= _statusRegistry.BlocksAction(status.Id);
        }

        return (held, subdued);
    }

    public ActionResult Map(int radius = 8)
    {
        var player = State.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;
        var perception = _perceptionSystem.RefreshControlled();
        var safeRadius = Math.Clamp(radius, 1, Math.Max(State.Width, State.Height));
        var minX = Math.Max(0, origin.X - safeRadius);
        var maxX = Math.Min(State.Width - 1, origin.X + safeRadius);
        var minY = Math.Max(0, origin.Y - safeRadius);
        var maxY = Math.Min(State.Height - 1, origin.Y + safeRadius);
        var messages = new List<string>
        {
            $"Map radius {safeRadius}; zone {State.CurrentZoneId}; region {State.RegionId}; center {origin.X},{origin.Y}; x {minX}-{maxX}; y {minY}-{maxY}.",
        };
        for (var y = minY; y <= maxY; y++)
        {
            var row = new char[(maxX - minX) + 1];
            for (var x = minX; x <= maxX; x++)
            {
                row[x - minX] = MapGlyph(new GridPoint(x, y), perception);
            }

            messages.Add($"{y:00} {new string(row)}");
        }

        messages.Add("Legend: @ you, letters visible actors, ! visible items, & fixtures, # blocking, ~ water, * growth, . floor, space unseen.");
        return ActionResult.Simple(
            "map",
            success: true,
            consumedTurn: false,
            State.Turn,
            State.Turn,
            messages.ToArray());
    }

    private char MapGlyph(GridPoint point, PerceptionSnapshot perception)
    {
        if (!perception.VisibleTiles.Contains(point))
        {
            return perception.ExploredTiles.Contains(point) ? RememberedTerrainGlyph(point) : ' ';
        }

        var entity = State.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && position.Position == point)
            .OrderBy(entity => entity.Id == State.ControlledEntityId ? 0 : 1)
            .ThenBy(entity => entity.TryGet<PhysicalComponent>(out var physical) && physical.BlocksMovement ? 0 : 1)
            .ThenBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (entity is not null)
        {
            if (entity.Id == State.ControlledEntityId)
            {
                return '@';
            }

            return entity.TryGet<RenderableComponent>(out var renderable)
                ? renderable.Glyph
                : entity.TryGet<ActorComponent>(out _) ? 'a' : '!';
        }

        return TerrainGlyph(point);
    }

    private char RememberedTerrainGlyph(GridPoint point)
    {
        var glyph = TerrainGlyph(point);
        return glyph == '#' ? '#' : ',';
    }

    private char TerrainGlyph(GridPoint point)
    {
        if (State.BlockingTerrain.Contains(point))
        {
            return '#';
        }

        var terrain = State.Terrain.TryGetValue(point, out var tile) ? tile : "floor";
        if (terrain.Contains("water", StringComparison.OrdinalIgnoreCase)
            || terrain.Contains("river", StringComparison.OrdinalIgnoreCase))
        {
            return '~';
        }

        if (terrain.Contains("flower", StringComparison.OrdinalIgnoreCase)
            || terrain.Contains("grass", StringComparison.OrdinalIgnoreCase)
            || terrain.Contains("moss", StringComparison.OrdinalIgnoreCase)
            || terrain.Contains("vine", StringComparison.OrdinalIgnoreCase)
            || terrain.Contains("reed", StringComparison.OrdinalIgnoreCase))
        {
            return '*';
        }

        if (terrain.Contains("fire", StringComparison.OrdinalIgnoreCase)
            || terrain.Contains("flame", StringComparison.OrdinalIgnoreCase))
        {
            return '^';
        }

        return '.';
    }

    public ActionResult Pickup(string? target) => _itemSystem.Pickup(target);

    public ActionResult DropItem(string item) => _itemSystem.DropItem(item);

    public ActionResult UseItem(string item) => _itemSystem.UseItem(item);

    public ActionResult EquipItem(string item) => _itemSystem.EquipItem(item);

    public ActionResult UnequipItem(string slotOrItem) => _itemSystem.UnequipItem(slotOrItem);

    public ActionResult FocusItem(string slotOrItem) => _itemSystem.FocusItem(slotOrItem);

    public ActionResult UnfocusItem(string? slotOrItem) => _itemSystem.UnfocusItem(slotOrItem);

    public ActionResult Reagents() => _itemSystem.Reagents();

    public ActionResult Inventory() => _itemSystem.Inventory();

    public ActionResult Wares(string? target = null) => _itemSystem.Wares(target);

    public ActionResult Buy(string item, string? target = null) => _itemSystem.Buy(item, target);

    public ActionResult Sell(string item, string? target = null) => _itemSystem.Sell(item, target);

    public ActionResult Services(string? target = null) => _interactionSystem.Services(target);

    public ActionResult RequestService(string service, string? target = null) =>
        _interactionSystem.RequestService(service, target);

    public ActionResult Journal()
    {
        return ActionResult.Simple(
            "journal",
            true,
            false,
            State.Turn,
            State.Turn,
            JournalViewBuilder.Build(State).ToArray());
    }

    public ActionResult Rumors()
    {
        return ActionResult.Simple(
            "rumors",
            true,
            false,
            State.Turn,
            State.Turn,
            RumorViewBuilder.BuildLines(State, limit: 12).ToArray());
    }

    public ActionResult Talk(string text) => _interactionSystem.Talk(text);

    public DialoguePreparation PrepareDialogue(string text) => _interactionSystem.PrepareDialogue(text);

    public ActionResult ApplyGeneratedDialogue(
        PreparedDialogueTurn turn,
        string spokenText,
        string provider,
        string? rawText = null,
        string? delivery = null,
        string? intent = null) =>
        _interactionSystem.ApplyGeneratedDialogue(turn, spokenText, provider, rawText, delivery, intent);

    public ActionResult Give(string item, string? target) => _interactionSystem.Give(item, target);

    public ActionResult Recruit(string? target) => _interactionSystem.Recruit(target);

    public ActionResult RecruitFromDialogue(string actorId, string provider, string? reason) =>
        _interactionSystem.RecruitFromDialogue(actorId, provider, reason);

    public ActionResult Bonds(string? target) => _interactionSystem.Bonds(target);

    public ActionResult Read(string? target) => _interactionSystem.Read(target);

    public ActionResult Examine(string? target) => _interactionSystem.Examine(target);

    public ActionResult Open(string? target) => _interactionSystem.Open(target);

    public ActionResult OpenDoor(Entity actor, Entity door, WorldActionContext context) =>
        _interactionSystem.OpenDoor(actor, door, context);

    public ActionResult Possess(string? target) => _bodySwapSystem.Possess(target);

    public bool CanPossess(Entity newBody, out string? reason) => _bodySwapSystem.CanPossess(newBody, out reason);

    public IReadOnlyList<StateDelta> PossessEntity(Entity newBody) => _bodySwapSystem.PossessEntity(newBody);

    public ActionResult Standing()
    {
        var messages = State.Factions.Factions
            .Where(faction => !faction.Role.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || faction.Standing.Count > 0
                || faction.Resources.Count > 0
                || faction.HostileRoles.Count > 0)
            .OrderBy(faction => faction.Id)
            .Select(faction =>
            {
                var standing = faction.Standing.Count == 0
                    ? "unchanged"
                    : string.Join(", ", faction.Standing.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
                var pressure = PressureLabel(faction);
                var mood = MoodLabel(faction);
                var rank = RankLabel(faction);
                return $"{faction.Name} ({faction.Role}): {standing}; pressure {pressure}; mood {mood}; rank {rank}";
            })
            .ToList();
        var playerSoul = State.ControlledEntity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : State.ControlledEntityId.Value;
        var legend = State.Legend.Tags
            .Where(tag => tag.ActorSoulId.Equals(playerSoul, StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Tag)
            .Select(group => $"{group.Key}:{group.Sum(tag => tag.Weight)}")
            .OrderBy(text => text)
            .ToArray();
        if (legend.Length > 0)
        {
            messages.Add($"Legend ({playerSoul}): {string.Join(", ", legend)}");
        }

        var capitalDefenses = State.Factions.FactionsByRole("empire_bloc")
            .Sum(faction => State.Factions.ResourceValue(faction.Id, "defenses"));
        messages.Add($"Capital reach: reachable through the eastern road; imperial defenses tracked at {capitalDefenses}.");

        return ActionResult.Simple("standing", true, false, State.Turn, State.Turn, messages.Count == 0 ? new[] { "No faction standing is known." } : messages.ToArray());
    }

    public bool EmperorReachable() =>
        State.Factions.FactionsByRole("empire_bloc")
            .All(faction => State.Factions.ResourceValue(faction.Id, "defenses") <= 0);

    public ActionResult Followers()
    {
        var playerSoulId = State.ControlledEntity.TryGet<SoulComponent>(out var playerSoul)
            ? playerSoul.SoulId
            : State.ControlledEntityId.Value;
        var followers = State.Entities.Values
            .Where(entity => entity.Id != State.ControlledEntityId)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor)
                && actor.Alive)
            .Where(entity =>
                State.Bonds.TryGet(SoulIdFor(entity), playerSoulId, out var bond)
                && IsFollowerBond(bond))
            .OrderBy(entity => entity.Id.Value)
            .Select(entity => $"{entity.Name} ({entity.Id.Value}) - {FollowerPosture(entity, playerSoulId)}")
            .ToArray();
        return ActionResult.Simple("followers", true, false, State.Turn, State.Turn, followers.Length == 0 ? new[] { "No one is following you." } : followers);
    }

    public ActionResult Jobs()
    {
        var jobs = _viewBuilder.BuildBackgroundJobCards()
            .OrderBy(job => job.State)
            .ThenBy(job => job.Id)
            .Select(job =>
            {
                var timing = job.State switch
                {
                    "Queued" => $"queued on turn {job.CreatedTurn}",
                    "Running" => $"started on turn {job.StartedTurn}",
                    "Completed" => $"completed on turn {job.CompletedTurn}",
                    "Applied" => $"applied on turn {job.AppliedTurn}",
                    "Failed" => $"failed: {job.Error}",
                    _ => job.State,
                };
                return $"{job.Id} [{job.State}] {job.Purpose} -> {job.TargetId} ({timing})";
            })
            .ToArray();
        return ActionResult.Simple(
            "jobs",
            true,
            false,
            State.Turn,
            State.Turn,
            jobs.Length == 0 ? new[] { "No background jobs are queued." } : jobs);
    }

    public ActionResult CharacterSheet()
    {
        var sheet = _viewBuilder.BuildCharacterSheet();
        var messages = new List<string>
        {
            $"{sheet.PublicName} ({sheet.OriginName})",
            $"Body: Vigor {sheet.Vigor}; HP {sheet.HitPoints}/{sheet.MaxHitPoints}; appearance: {sheet.Appearance}",
            $"Soul: Attunement {sheet.Attunement}, Composure {sheet.Composure}; MP {sheet.Mana}/{sheet.MaxMana}",
            $"Signature: {sheet.MagicalSignature}",
        };
        var repertoire = _viewBuilder.BuildRepertoire();
        messages.Add(repertoire.CharterSpells.Count == 0
            ? "Charter forms: none learned. They are learned from manuals, warrants, and licensed paraphernalia."
            : $"Charter forms: {string.Join(", ", repertoire.CharterSpells.Select(spell => spell.Name))} — cast with 'charter <id>'.");
        if (repertoire.EchoesEnabled)
        {
            messages.Add($"Echoes: {repertoire.Echoes.Count} in grimoire ('echoes' to list).");
        }

        if (IsWearingBorrowedBody())
        {
            // The body/soul stat split above already shows this (body row vs. soul row belong
            // to different beings while possessing), but that split reads as mysterious rather
            // than legible on first encounter without this line spelling it out.
            messages.Add("You wear this body; the mind and magic are your own.");
        }

        return ActionResult.Simple(
            "character",
            true,
            false,
            State.Turn,
            State.Turn,
            messages.ToArray());
    }

    private bool IsWearingBorrowedBody() =>
        State.ControlledEntity.TryGet<StatusContainerComponent>(out var statuses)
        && statuses.Statuses.Any(status =>
            (status.ExpiresTurn is null || status.ExpiresTurn > State.Turn)
            && _statusRegistry.Canonicalize(status.Id).Equals("borrowed_body", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<StateDelta> AdvanceTurn()
    {
        var messageCount = State.Messages.Count;
        var structuredDeltas = _turnSystem.AdvanceTurn().ToList();
        structuredDeltas.AddRange(ApplyBargainDeadlines());
        structuredDeltas.AddRange(ApplyCurseCadence());
        structuredDeltas.AddRange(ApplyPendingSuspicionUpdates());
        structuredDeltas.AddRange(RecordControlledExploration("perception", "recordExploration"));

        var unmatchedSummaries = structuredDeltas
            .GroupBy(delta => delta.Summary, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var messageDeltas = new List<StateDelta>();
        foreach (var message in State.Messages.Skip(messageCount))
        {
            if (unmatchedSummaries.TryGetValue(message, out var remaining) && remaining > 0)
            {
                if (remaining == 1)
                {
                    unmatchedSummaries.Remove(message);
                }
                else
                {
                    unmatchedSummaries[message] = remaining - 1;
                }

                continue;
            }

            messageDeltas.Add(new StateDelta(
                "turnEvent",
                $"turn:{State.Turn}:{messageDeltas.Count}",
                message,
                new Dictionary<string, object?> { ["turn"] = State.Turn }));
        }

        return structuredDeltas.Concat(messageDeltas).ToArray();
    }

    private IReadOnlyList<StateDelta> ApplyBargainDeadlines()
    {
        var deltas = new List<StateDelta>();
        foreach (var promise in State.PromiseLedger.Promises
            .Where(candidate => candidate.BargainAgreement?.Status.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
            .Where(candidate => candidate.BargainAgreement!.Terms.Any(term =>
                term.Kind.Equals(BargainTermKinds.Deadline, StringComparison.OrdinalIgnoreCase)
                && term.DueTurn is { } due
                && due < State.Turn))
            .ToArray())
        {
            var applied = ApplyConsequence(WorldConsequence.FulfillBargain(
                "agreement_deadline",
                promise.Id,
                "deadline",
                State.ControlledEntityId.Value,
                action: "breach",
                visibility: WorldConsequenceVisibility.Message,
                evidence: $"Turn {State.Turn} passed the agreement deadline.",
                reason: "Expired typed deadlines breach still-pending agreements.",
                operation: "breachBargainDeadline"));
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyCurseCadence()
    {
        if (State.Turn == 0
            || State.Turn % 12 != 0
            || !State.PromiseLedger.Promises.Any(promise =>
                promise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase)
                && promise.Status is not "cleared" and not "fulfilled"
                && promise.CostProfileId?.Equals("curse_hollow_name", StringComparison.OrdinalIgnoreCase) == true
                && (string.IsNullOrWhiteSpace(promise.BoundTargetId)
                    || promise.BoundTargetId.Equals(State.ControlledEntityId.Value, StringComparison.OrdinalIgnoreCase))))
        {
            return Array.Empty<StateDelta>();
        }

        var playerSoul = State.ControlledEntity.TryGet<SoulComponent>(out var controlledSoul)
            ? controlledSoul.SoulId
            : State.ControlledEntityId.Value;
        var deltas = new List<StateDelta>();
        foreach (var bond in State.Bonds.Bonds
            .Where(record => record.TargetSoulId.Equals(playerSoul, StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            var subject = State.Entities.Values.FirstOrDefault(entity =>
                entity.TryGet<SoulComponent>(out var soul)
                    ? soul.SoulId.Equals(bond.SubjectSoulId, StringComparison.OrdinalIgnoreCase)
                    : entity.Id.Value.Equals(bond.SubjectSoulId, StringComparison.OrdinalIgnoreCase));
            deltas.AddRange(ApplyConsequence(WorldConsequence.UpdateBond(
                "hollow_name",
                subject?.Id.Value ?? bond.SubjectSoulId,
                playerSoul,
                loyaltyDelta: -1,
                fearDelta: 0,
                admirationDelta: -1,
                resentmentDelta: 0,
                posture: null,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: $"Hollow Name cadence at turn {State.Turn}.",
                reason: "The active identity curse erodes remembered bonds once per world-day cadence.",
                operation: "hollowNameBondDecay",
                maxDelta: 1,
                subjectSoulId: bond.SubjectSoulId)).Deltas);
        }

        if (deltas.Count > 0)
        {
            deltas.AddRange(ApplyConsequence(WorldConsequence.Message(
                "hollow_name",
                "Your name slips loose again; people who knew you remember one degree less.",
                targetEntityId: State.ControlledEntityId.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: $"Hollow Name cadence at turn {State.Turn}.",
                operation: "hollowNameCadence")).Deltas);
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyPendingSuspicionUpdates()
    {
        var deltas = new List<StateDelta>();
        foreach (var update in _perceptionSystem.PendingSuspicionUpdates())
        {
            var applied = ApplyConsequence(WorldConsequence.UpdateSuspicion(
                "perception",
                update.SuspicionId,
                update.Status,
                update.SuspectedSoulId,
                update.AttributedTurn,
                evidence: "Pending suspicion attribution checked at turn boundary.",
                operation: "updateSuspicion"));
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
    }

    public Entity? EntityAt(GridPoint point) =>
        State.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point);

    public Entity? FindNearestHostile() => _aiSystem.FindNearestHostile();

    public IReadOnlyList<StateDelta> AttackEntity(
        Entity attacker,
        Entity defender,
        string damageType = "physical",
        string source = "combat",
        string? evidence = null,
        string? reason = null,
        string operation = "attack",
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var attackerActor = attacker.Get<ActorComponent>();
        // Re-stamp both derived equipment caches at strike time so combat reads fresh effects
        // regardless of equip history (the damage/resistance site reads the defender's cache).
        var attackerEffect = EquipmentEffectService.Recompute(attacker, _itemCatalog);
        EquipmentEffectService.Recompute(defender, _itemCatalog);
        var effectiveAttack = Math.Max(0, attackerActor.Attack + attackerEffect.Attack);
        var consequenceDetails = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["attacker"] = attacker.Id.Value,
            ["attackKind"] = "melee",
        };
        if (details is not null)
        {
            foreach (var pair in details)
            {
                consequenceDetails[pair.Key] = pair.Value;
            }
        }

        var attackResult = ApplyConsequence(WorldConsequence.Damage(
            source,
            defender.Id.Value,
            effectiveAttack,
            damageType,
            sourceEntityId: attacker.Id.Value,
            evidence: evidence ?? $"{attacker.Name} attacked {defender.Name}.",
            reason: reason ?? "Melee attack.",
            operation: operation,
            details: consequenceDetails));
        if (!attackResult.Applied || attackResult.Deltas.Any(IsRejectedDelta))
        {
            return attackResult.Deltas;
        }

        var attackDelta = attackResult.Deltas.FirstOrDefault();
        if (attackDelta is null)
        {
            return Array.Empty<StateDelta>();
        }

        var hitAmount = attackDelta.Details.TryGetValue("amount", out var amount) ? Convert.ToInt32(amount) : 0;
        var deltas = new List<StateDelta>(attackResult.Deltas);
        deltas.AddRange(_persistentEffects.FireHook("on_strike", attacker, defender, hitAmount));
        deltas.AddRange(_persistentEffects.FireHook("on_hit", defender, attacker, hitAmount));
        return deltas;
    }

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    public StateValidationReport ValidateState() => StateValidator.Validate(State);

    public Entity? EntityById(string id) =>
        State.Entities.TryGetValue(EntityId.Create(id), out var entity) ? entity : null;

    public PerceptionSnapshot Perception() => _perceptionSystem.RefreshControlled();

    private IReadOnlyList<StateDelta> RecordControlledExploration(string source, string operation)
    {
        var snapshot = _perceptionSystem.SnapshotForControlled();
        if (snapshot.VisibleTiles.Count == 0)
        {
            return Array.Empty<StateDelta>();
        }

        var applied = ApplyConsequence(WorldConsequence.RecordExploration(
            source,
            snapshot.SoulId,
            snapshot.VisibleTiles
                .OrderBy(point => point.Y)
                .ThenBy(point => point.X)
                .ToArray(),
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: $"Controlled soul {snapshot.SoulId} perceived {snapshot.VisibleTiles.Count} tile(s).",
            reason: "Soul-bound exploration memory is recorded through the shared consequence lifecycle.",
            operation: operation,
            details: new Dictionary<string, object?>
            {
                ["controlledEntityId"] = State.ControlledEntityId.Value,
                ["currentZoneId"] = State.CurrentZoneId,
                ["regionId"] = State.RegionId,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
        return applied.Deltas;
    }

    public IReadOnlyList<Entity> WitnessesOf(GridPoint point, EntityId? exclude = null) =>
        _perceptionSystem.WitnessesOf(point, exclude);

    /// <summary>
    /// The one witness-classification policy (Phase 1.1): who saw the actor and/or the effect of a
    /// deed. Deed capture and suspicion attribution both project from this; debug observation
    /// surfaces it so the "who noticed and why" is inspectable.
    /// </summary>
    public IReadOnlyList<WitnessObservation> ClassifyEffectWitnesses(
        GridPoint actorOrigin, GridPoint? effectPoint, Entity actor) =>
        _perceptionSystem.ClassifyEffectWitnesses(actorOrigin, effectPoint, actor);

    public IReadOnlyList<SuspicionCapturePlan> PlanEffectSuspicion(
        GridPoint effectPoint,
        string kind,
        Entity? actor = null) =>
        _perceptionSystem.PlanEffectSuspicion(effectPoint, kind, actor);

    internal bool CanPerceiveSubject(Entity witness, Entity subject) =>
        _perceptionSystem.CanPerceiveSubject(witness, subject);

    internal DeedCaptureResult PlanDeedCapture(
        Entity actor,
        string kind,
        int magnitude,
        GridPoint origin,
        GridPoint? effectPoint,
        IEnumerable<string>? tags = null)
    {
        // One witness-classification pass feeds both witness sets, so deed capture and suspicion
        // share exactly one line-of-sight/range/concealment rule (Phase 1.1). Effect visibility
        // ignores the actor's concealment; actor visibility honors it.
        var observations = _perceptionSystem.ClassifyEffectWitnesses(origin, effectPoint, actor);
        IReadOnlyList<Entity> actorWitnesses = observations.Where(observation => observation.SawActor)
            .Select(observation => observation.Witness).ToArray();
        IReadOnlyList<Entity> effectWitnesses = observations.Where(observation => observation.SawEffect)
            .Select(observation => observation.Witness).ToArray();
        var suspicionDeltas = Array.Empty<StateDelta>();
        if (effectPoint is not null && actorWitnesses.Count == 0 && effectWitnesses.Count > 0)
        {
            var suspicion = ApplyConsequence(WorldConsequence.RecordSuspicion(
                "engine",
                kind,
                effectPoint.Value.X,
                effectPoint.Value.Y,
                actor.Id.Value,
                sourceEntityId: actor.Id.Value));
            suspicionDeltas = suspicion.Deltas.ToArray();
        }

        var plan = _worldReactions.PlanDeed(
            State,
            actor,
            kind,
            magnitude,
            origin,
            effectPoint,
            actorWitnesses,
            effectWitnesses,
            tags);
        return new DeedCaptureResult(plan, suspicionDeltas);
    }

    public Entity? ResolveEntity(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || target.Equals("player", StringComparison.OrdinalIgnoreCase)
            || target.Equals("self", StringComparison.OrdinalIgnoreCase))
        {
            return State.ControlledEntity;
        }

        if (target.Equals("nearest_enemy", StringComparison.OrdinalIgnoreCase)
            || target.Equals("nearest", StringComparison.OrdinalIgnoreCase)
            || target.Equals("enemy", StringComparison.OrdinalIgnoreCase))
        {
            return FindNearestHostile();
        }

        return EntityById(target);
    }

    public MagicContextView MagicContext(
        OperationIndex operations,
        IReadOnlyCollection<string>? requiredContext = null,
        string? resolverQuery = null) =>
        _viewBuilder.MagicContext(operations, requiredContext, resolverQuery);

    public GameView View() => _viewBuilder.View();

    public AgentObservation Observation(bool debug) => _viewBuilder.Observation(debug);

    public bool InBounds(GridPoint point) =>
        point.X >= 0 && point.Y >= 0 && point.X < State.Width && point.Y < State.Height;

    public Entity? BlockingEntityAt(GridPoint point) =>
        State.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point
            && entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement
            && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive));

    public bool IsHostile(Entity actor, Entity target) => _aiSystem.IsHostile(actor, target);

    public static int Distance(GridPoint a, GridPoint b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    /// <summary>The number of moves between two tiles under 8-directional movement (Chebyshev
    /// distance). This is the correct "reach": a diagonally adjacent tile is one step away, so
    /// interaction ranges (pick up, talk, examine, open, loot) use this rather than the Manhattan
    /// <see cref="Distance"/>, which would call a single diagonal step "2 tiles" and unreachable.</summary>
    public static int StepDistance(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static string PressureLabel(FactionRecord faction)
    {
        if (!faction.Role.Equals("empire_bloc", StringComparison.OrdinalIgnoreCase))
        {
            return "local";
        }

        faction.Resources.TryGetValue("heat", out var heat);
        return heat switch
        {
            <= 0 => "quiet",
            <= 2 => "watchful",
            <= 4 => "active",
            _ => "crackdown",
        };
    }

    private static string MoodLabel(FactionRecord faction)
    {
        if (faction.Standing.Count == 0)
        {
            return "steady";
        }

        var fear = faction.Standing.TryGetValue("fear", out var fearValue) ? fearValue : 0;
        var gratitude = faction.Standing.TryGetValue("gratitude", out var gratitudeValue) ? gratitudeValue : 0;
        var suspicion = faction.Standing.TryGetValue("suspicion", out var suspicionValue) ? suspicionValue : 0;
        var threat = faction.Standing.TryGetValue("imperial-threat", out var threatValue) ? threatValue : 0;
        if (gratitude > Math.Max(fear, suspicion))
        {
            return "grateful";
        }

        if (threat >= 5 || fear >= 5)
        {
            return "afraid";
        }

        if (suspicion > 0 || threat > 0)
        {
            return "alarmed";
        }

        return "watching";
    }

    private static string RankLabel(FactionRecord faction)
    {
        if (!faction.Role.Equals("empire_bloc", StringComparison.OrdinalIgnoreCase))
        {
            return faction.Standing.TryGetValue("gratitude", out var gratitude) && gratitude > 0
                ? "remembered"
                : "unknown";
        }

        var notoriety = faction.Standing.TryGetValue("notoriety", out var notorietyValue) ? notorietyValue : 0;
        var threat = faction.Standing.TryGetValue("imperial-threat", out var threatValue) ? threatValue : 0;
        return (notoriety + threat) switch
        {
            <= 0 => "unfiled",
            <= 3 => "unlicensed nuisance",
            <= 7 => "named offender",
            _ => "marble-priority fugitive",
        };
    }

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static bool IsFollowerBond(BondRecord bond) =>
        bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase)
        || bond.Loyalty >= 5;

    private string FollowerPosture(Entity entity, string playerSoulId) =>
        State.Bonds.TryGet(SoulIdFor(entity), playerSoulId, out var bond)
            ? bond.Posture
            : "nearby";
}
