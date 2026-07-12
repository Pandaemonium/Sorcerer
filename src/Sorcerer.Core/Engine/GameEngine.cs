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
    private readonly ItemCatalog _itemCatalog = ItemCatalog.CreateMinimal();
    private readonly LoreCatalog _loreCatalog = LoreCatalog.LoadDefault();
    private readonly InventoryService _inventoryService;
    private readonly ItemSystem _itemSystem;
    private readonly MovementSystem _movementSystem;
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
        _perceptionSystem = new PerceptionSystem(State, _statusRegistry);
        _persistentEffects = new PersistentEffectSystem(this);
        _turnSystem = new TurnSystem(this, State, _statusRegistry, _loreCatalog, backgroundTextGenerator);
        _interactionSystem = new InteractionSystem(this, _itemSystem, _turnSystem);
        _viewBuilder = new EngineViewBuilder(this, _inventoryService, _statusRegistry, _perceptionSystem, _generationSystem, _loreCatalog);
        RecordControlledExploration("perception_init", "recordInitialExploration");
    }

    public GameState State { get; }

    public StatusRegistry Statuses => _statusRegistry;

    public WorldConsequenceApplyResult ApplyConsequence(WorldConsequence consequence) =>
        WorldConsequenceGuard.Apply(State, consequence, _worldConsequences.Apply);

    public ActionResult MoveControlled(Direction direction) => _movementSystem.MoveControlled(direction);

    public ActionResult Wait() => _movementSystem.Wait();

    public ActionResult Travel(Direction direction)
    {
        var turnBefore = State.Turn;
        var travelDeltas = _generationSystem.Travel(direction);
        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "travel",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = travelDeltas.PlayerMessages().Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = travelDeltas.Concat(turnDeltas).ToArray(),
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

        return ActionResult.Simple(
            "inspect",
            success: true,
            consumedTurn: false,
            State.Turn,
            State.Turn,
            messages.ToArray());
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
            attackerActor.Attack,
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

    internal IReadOnlyList<SuspicionCapturePlan> PlanEffectSuspicion(
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
        var actorWitnesses = _perceptionSystem.WitnessesOf(origin, actor.Id, subject: actor);
        var effectWitnesses = effectPoint is null
            ? Array.Empty<Entity>()
            : _perceptionSystem.WitnessesOf(effectPoint.Value, actor.Id);
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
