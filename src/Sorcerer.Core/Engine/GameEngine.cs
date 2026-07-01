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

public sealed class GameEngine
{
    private readonly AiSystem _aiSystem;
    private readonly BodySwapSystem _bodySwapSystem;
    private readonly CombatSystem _combatSystem;
    private readonly EffectSystem _effectSystem;
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

    public GameEngine(GameState state)
    {
        State = state;
        CharacterMath.EnsureCharacterState(State);
        _aiSystem = new AiSystem(this, _statusRegistry);
        _bodySwapSystem = new BodySwapSystem(this, _statusRegistry);
        _combatSystem = new CombatSystem(State);
        _effectSystem = new EffectSystem(State, _statusRegistry);
        _generationSystem = new GenerationSystem(State, _itemCatalog, _loreCatalog);
        _inventoryService = new InventoryService(_itemCatalog);
        _itemSystem = new ItemSystem(this, _itemCatalog, _inventoryService);
        _movementSystem = new MovementSystem(this, _statusRegistry);
        _perceptionSystem = new PerceptionSystem(State);
        _persistentEffects = new PersistentEffectSystem(this);
        _worldConsequences = new WorldConsequenceApplier(State);
        _turnSystem = new TurnSystem(this, State, _statusRegistry, _loreCatalog);
        _interactionSystem = new InteractionSystem(this, _itemSystem, _turnSystem);
        _viewBuilder = new EngineViewBuilder(this, _inventoryService, _statusRegistry, _perceptionSystem, _generationSystem, _loreCatalog);
        _perceptionSystem.RefreshControlled();
    }

    public GameState State { get; }

    public StatusRegistry Statuses => _statusRegistry;

    public WorldConsequenceApplyResult ApplyConsequence(WorldConsequence consequence) =>
        _worldConsequences.Apply(consequence);

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
            Messages = travelDeltas.Select(item => item.Summary).Concat(turnDeltas.Select(item => item.Summary)).ToArray(),
            Deltas = travelDeltas.Concat(turnDeltas).ToArray(),
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
                messages.Add($"{entity.Name} at {entityPosition.Position.X},{entityPosition.Position.Y}.");
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
        var messages = new List<string>();
        var visiblePromises = State.PromiseLedger.Promises
            .Where(promise => promise.PlayerVisible)
            .ToArray();
        var leads = visiblePromises
            .Where(IsLeadPromise)
            .Select(promise => $"Lead: {promise.Id} [{promise.Status}] {promise.Text}")
            .ToArray();
        var otherPromises = visiblePromises
            .Where(promise => !IsLeadPromise(promise))
            .Select(promise => $"Promise: {promise.Id} [{promise.Status}] {promise.Text}")
            .ToArray();
        if (leads.Length == 0 && otherPromises.Length == 0)
        {
            messages.Add("No promises are visible yet.");
        }
        else
        {
            messages.AddRange(leads);
            messages.AddRange(otherPromises);
        }

        var claims = State.Claims.Records
            .Where(claim => claim.PlayerVisible)
            .Where(claim => claim.Salience >= 3)
            .OrderBy(claim => claim.Id, StringComparer.OrdinalIgnoreCase)
            .Select(claim => $"{claim.Id} [{claim.Status}] {claim.Text}")
            .ToArray();
        if (claims.Length > 0)
        {
            messages.AddRange(claims.Select(claim => $"Claim: {claim}"));
        }

        var soulId = State.ControlledEntity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : State.ControlledEntityId.Value;
        var legend = State.Legend.Tags
            .Where(tag => tag.ActorSoulId.Equals(soulId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Tag)
            .Select(group => $"{group.Key}:{group.Sum(tag => tag.Weight)}")
            .OrderBy(text => text)
            .ToArray();
        if (legend.Length > 0)
        {
            messages.Add($"Legend: {string.Join(", ", legend)}");
        }

        var warrants = State.ScheduledEvents.Events
            .Where(item => item.Kind.StartsWith("empire_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.DueTurn)
            .Select(item => item.Kind switch
            {
                "empire_warrant" => $"Warrant: a wanted poster is expected around turn {item.DueTurn}.",
                "empire_patrol" => $"Pressure: an imperial patrol is expected around turn {item.DueTurn}.",
                _ => $"Pressure: {item.Kind} is expected around turn {item.DueTurn}.",
            })
            .ToArray();
        messages.AddRange(warrants);

        return ActionResult.Simple(
            "journal",
            true,
            false,
            State.Turn,
            State.Turn,
            messages.ToArray());
    }

    private static bool IsLeadPromise(WorldPromise promise) =>
        promise.Salience >= 3
        && NormalizeJournalToken(promise.RealizationKind ?? promise.Kind) is
            "site" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade" or "quest" or "door_rule" or "escape_route" or "prophecy";

    private static string NormalizeJournalToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
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
        messages.Add($"Capital reach: thin-slice reachable; imperial defenses tracked at {capitalDefenses}.");

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
        var messages = new[]
        {
            $"{sheet.PublicName} ({sheet.OriginName})",
            $"Body: Vigor {sheet.Vigor}; HP {sheet.HitPoints}/{sheet.MaxHitPoints}; appearance: {sheet.Appearance}",
            $"Soul: Attunement {sheet.Attunement}, Composure {sheet.Composure}; MP {sheet.Mana}/{sheet.MaxMana}",
            $"Signature: {sheet.MagicalSignature}",
        };
        return ActionResult.Simple(
            "character",
            true,
            false,
            State.Turn,
            State.Turn,
            messages);
    }

    public ActionResult Unsupported(string action, bool free = true)
    {
        var turnBefore = State.Turn;
        var message = $"{action} is part of the Sorcerer architecture stub, but is not implemented yet.";
        State.AddMessage(message);
        if (!free)
        {
            AdvanceTurn();
        }

        return ActionResult.Simple(
            action,
            success: false,
            consumedTurn: !free,
            turnBefore,
            State.Turn,
            message);
    }

    public IReadOnlyList<StateDelta> AdvanceTurn()
    {
        var messageCount = State.Messages.Count;
        _turnSystem.AdvanceTurn();
        _perceptionSystem.RefreshControlled();
        return State.Messages
            .Skip(messageCount)
            .Select((message, index) => new StateDelta(
                "turnEvent",
                $"turn:{State.Turn}:{index}",
                message,
                new Dictionary<string, object?> { ["turn"] = State.Turn }))
            .ToArray();
    }

    public void AddMessage(string message) => State.AddMessage(message);

    public Entity? EntityAt(GridPoint point) =>
        State.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point);

    public Entity? FindNearestHostile() => _aiSystem.FindNearestHostile();

    public StateDelta DamageEntity(Entity target, int amount, string damageType) =>
        _combatSystem.DamageEntity(target, amount, damageType);

    public StateDelta? ReleaseDelayedDamage(Entity target) => _combatSystem.ReleaseDelayedDamage(target);

    public IReadOnlyList<StateDelta> AttackEntity(Entity attacker, Entity defender, string damageType = "physical")
    {
        var attackDelta = _combatSystem.AttackEntity(attacker, defender, damageType);
        var hitAmount = attackDelta.Details.TryGetValue("amount", out var amount) ? Convert.ToInt32(amount) : 0;
        var deltas = new List<StateDelta> { attackDelta };
        deltas.AddRange(_persistentEffects.FireHook("on_strike", attacker, defender, hitAmount));
        deltas.AddRange(_persistentEffects.FireHook("on_hit", defender, attacker, hitAmount));
        return deltas;
    }

    public StateDelta RestoreMana(Entity target, int amount) => _combatSystem.RestoreMana(target, amount);

    public StateDelta HealEntity(Entity target, int amount) => _combatSystem.HealEntity(target, amount);

    public StateDelta MoveEntity(Entity entity, GridPoint destination, string operation) =>
        _movementSystem.MoveEntity(entity, destination, operation);

    public StateDelta SetTerrain(GridPoint point, string terrain, int? duration = null) =>
        _effectSystem.SetTerrain(point, terrain, duration);

    public StateDelta ApplyStatus(Entity target, string status, int duration, string displayName = "") =>
        _effectSystem.ApplyStatus(target, status, duration, displayName);

    public StateDelta RemoveStatus(Entity target, string status) => _effectSystem.RemoveStatus(target, status);

    public StateDelta CreateTrigger(
        string name,
        string kind,
        int delay,
        int interval,
        int uses,
        int? duration,
        EntityId? sourceEntityId,
        string? anchorEntityId,
        GridPoint? anchorPoint,
        int radius,
        string targetFilter,
        string effectType,
        IReadOnlyDictionary<string, object?> effectFields,
        string description,
        bool playerVisible)
    {
        var safeDelay = Math.Clamp(delay, 1, 99);
        var safeInterval = Math.Clamp(interval, 1, 99);
        var safeUses = Math.Clamp(uses, 1, 20);
        var createdTurn = State.Turn;
        var record = State.Triggers.Add(
            name,
            kind,
            createdTurn,
            createdTurn + safeDelay,
            safeInterval,
            safeUses,
            duration is null ? null : createdTurn + Math.Max(safeDelay, duration.Value),
            sourceEntityId,
            anchorEntityId,
            anchorPoint,
            radius,
            targetFilter,
            effectType,
            effectFields,
            description,
            playerVisible);
        var message = record.Kind.Equals("aura", StringComparison.OrdinalIgnoreCase)
            ? $"{record.Name} begins to pulse."
            : $"{record.Name} settles into a later turn.";
        State.AddMessage(message);
        return new StateDelta(
            "createTrigger",
            record.Id,
            message,
            new Dictionary<string, object?>
            {
                ["kind"] = record.Kind,
                ["nextTurn"] = record.NextTurn,
                ["effectType"] = record.EffectType,
            });
    }

    public Entity SpawnEntity(string prefix, string name, char glyph, GridPoint position, string faction, int hp, int attack, IReadOnlyList<string> tags) =>
        _effectSystem.SpawnEntity(prefix, name, glyph, position, faction, hp, attack, tags);

    public Entity SpawnItem(
        string prefix,
        string name,
        char glyph,
        GridPoint position,
        string itemType,
        string material,
        IReadOnlyList<string> tags,
        int quantity,
        int value = 1) =>
        _effectSystem.SpawnItem(prefix, name, glyph, position, itemType, material, tags, quantity, value);

    public StateDelta AddPromise(string kind, string text, Entity? anchor = null, string triggerHint = "", string source = "wild_magic") =>
        _interactionSystem.AddPromise(kind, text, anchor, triggerHint, source);

    public StateValidationReport ValidateState() => StateValidator.Validate(State);

    public Entity? EntityById(string id) =>
        State.Entities.TryGetValue(EntityId.Create(id), out var entity) ? entity : null;

    public PerceptionSnapshot Perception() => _perceptionSystem.RefreshControlled();

    public IReadOnlyList<Entity> WitnessesOf(GridPoint point, EntityId? exclude = null) =>
        _perceptionSystem.WitnessesOf(point, exclude);

    public IReadOnlyList<SuspicionRecord> RecordEffectSuspicion(
        GridPoint effectPoint,
        string kind,
        Entity? actor = null) =>
        _perceptionSystem.RecordEffectSuspicion(effectPoint, kind, actor);

    public DeedRecord RecordDeed(
        Entity actor,
        string kind,
        int magnitude,
        GridPoint origin,
        GridPoint? effectPoint,
        IEnumerable<string>? tags = null)
    {
        var actorWitnesses = _perceptionSystem.WitnessesOf(origin, actor.Id);
        var effectWitnesses = effectPoint is null
            ? Array.Empty<Entity>()
            : _perceptionSystem.WitnessesOf(effectPoint.Value, actor.Id);
        if (effectPoint is not null && actorWitnesses.Count == 0 && effectWitnesses.Count > 0)
        {
            _perceptionSystem.RecordEffectSuspicion(effectPoint.Value, kind, actor);
        }

        return _worldReactions.CaptureDeed(
            State,
            actor,
            kind,
            magnitude,
            origin,
            effectPoint,
            actorWitnesses,
            effectWitnesses,
            tags);
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

    public MagicContextView MagicContext(OperationIndex operations) => _viewBuilder.MagicContext(operations);

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
