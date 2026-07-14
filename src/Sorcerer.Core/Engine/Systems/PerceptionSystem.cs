using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Status;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed record PerceptionSnapshot(
    string SoulId,
    IReadOnlySet<GridPoint> VisibleTiles,
    IReadOnlySet<GridPoint> ExploredTiles,
    IReadOnlySet<EntityId> VisibleEntityIds);

public sealed record SuspicionUpdateProposal(
    string SuspicionId,
    string Status,
    string? SuspectedSoulId,
    int? AttributedTurn);

public sealed record SuspicionCapturePlan(
    string WitnessSoulId,
    string Kind,
    GridPoint EffectPoint,
    string Status,
    string? SuspectedSoulId,
    int? AttributedTurn,
    int ExpiresTurn);

/// <summary>
/// One witness's structured classification of a single deed (Phase 1.1): did they see the actor
/// (subject to concealment), the effect, both, or neither. <see cref="WitnessEntityId"/> is the
/// body the witness actually observed — the seed the identity/attribution model keys on so a
/// body-swapped or concealed actor can attribute differently from a public one.
/// </summary>
public sealed record WitnessObservation(
    Entity Witness,
    string WitnessSoulId,
    string WitnessEntityId,
    bool SawActor,
    bool SawEffect)
{
    public bool SawBoth => SawActor && SawEffect;

    /// <summary>Compact label for debug state / evidence: both | actor | effect | neither.</summary>
    public string Classification =>
        SawActor && SawEffect ? "both"
        : SawActor ? "actor"
        : SawEffect ? "effect"
        : "neither";
}

public sealed class PerceptionSystem
{
    public const int DefaultSightRadius = 8;
    public const int SuspicionAttributionWindow = 20;

    // How close a witness must stand to notice/witness a bearer of a conceals-bearer status
    // (e.g. canonical "concealed") despite the concealment. Shared by AI targeting and deed
    // witnessing so there is exactly one concealment rule in the codebase.
    public const int ConcealedNoticeRadius = 2;

    private readonly GameState _state;
    private readonly StatusRegistry _statusRegistry;

    public PerceptionSystem(GameState state, StatusRegistry statusRegistry)
    {
        _state = state;
        _statusRegistry = statusRegistry;
    }

    public PerceptionSnapshot RefreshControlled()
    {
        return ComputeFor(_state.ControlledEntity, DefaultSightRadius);
    }

    public PerceptionSnapshot SnapshotForControlled() => RefreshControlled();

    public IReadOnlyList<Entity> WitnessesOf(GridPoint point, EntityId? exclude = null, Entity? subject = null)
    {
        if (!InBounds(point))
        {
            return Array.Empty<Entity>();
        }

        return _state.Entities.Values
            .Where(entity => exclude is null || entity.Id != exclude.Value)
            .Where(IsLivingActor)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && IsWithinSightRadius(position.Position, point, DefaultSightRadius)
                && HasLineOfSight(position.Position, point))
            .Where(entity => subject is null || CanPerceiveSubject(entity, subject))
            .OrderBy(entity => entity.Id.Value)
            .ToArray();
    }

    // A witness can perceive a subject unless the subject bears an active conceals-bearer
    // status (canonical "concealed" and aliases) with no active "revealed" status countering it,
    // and the witness is farther than ConcealedNoticeRadius away. This is the one shared
    // concealment rule: it gates both deed/witness capture (WitnessesOf) and AI targeting
    // (AiSystem.CanNoticeTarget delegates here).
    public bool CanPerceiveSubject(Entity witness, Entity subject)
    {
        if (!IsConcealed(subject))
        {
            return true;
        }

        if (!witness.TryGet<PositionComponent>(out var witnessPosition)
            || !subject.TryGet<PositionComponent>(out var subjectPosition))
        {
            return true;
        }

        return GameEngine.Distance(witnessPosition.Position, subjectPosition.Position) <= ConcealedNoticeRadius;
    }

    private bool IsConcealed(Entity subject)
    {
        if (!subject.TryGet<StatusContainerComponent>(out var statuses))
        {
            return false;
        }

        var hasActiveConceal = statuses.Statuses.Any(status =>
            IsStatusActive(status) && _statusRegistry.ConcealsBearer(status.Id));
        if (!hasActiveConceal)
        {
            return false;
        }

        var hasActiveReveal = statuses.Statuses.Any(status =>
            IsStatusActive(status)
            && _statusRegistry.Canonicalize(status.Id).Equals("revealed", StringComparison.OrdinalIgnoreCase));
        return !hasActiveReveal;
    }

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > _state.Turn;

    /// <summary>
    /// The one witness-classification policy (Phase 1.1): a single pass over living actors that
    /// classifies, for one deed, whether each witness saw the actor (at <paramref name="actorOrigin"/>,
    /// subject to the shared concealment rule) and/or the effect (at <paramref name="effectPoint"/>),
    /// using the same line-of-sight, range, and concealment rules everywhere. Deed capture
    /// (<see cref="GameEngine.PlanDeedCapture"/>) and suspicion attribution
    /// (<see cref="PlanEffectSuspicion"/>) both project from this, so there is exactly one
    /// actor-vs-effect visibility rule in the codebase. Witnesses who saw neither are omitted; the
    /// actor itself is never a witness. Effect visibility is not gated by the actor's concealment
    /// (the effect is out in the world); actor visibility is.
    /// </summary>
    public IReadOnlyList<WitnessObservation> ClassifyEffectWitnesses(
        GridPoint? actorOrigin,
        GridPoint? effectPoint,
        Entity? actor)
    {
        var observations = new List<WitnessObservation>();
        foreach (var entity in _state.Entities.Values)
        {
            if ((actor is not null && entity.Id == actor.Id) || !IsLivingActor(entity))
            {
                continue;
            }

            var sawActor = actor is not null
                && actorOrigin is not null
                && Sees(entity, actorOrigin.Value)
                && CanPerceiveSubject(entity, actor);
            var sawEffect = effectPoint is not null && Sees(entity, effectPoint.Value);
            if (sawActor || sawEffect)
            {
                observations.Add(new WitnessObservation(
                    entity, SoulIdFor(entity), entity.Id.Value, sawActor, sawEffect));
            }
        }

        return observations
            .OrderBy(observation => observation.Witness.Id.Value)
            .ToArray();
    }

    private bool Sees(Entity witness, GridPoint point) =>
        InBounds(point)
        && witness.TryGet<PositionComponent>(out var position)
        && IsWithinSightRadius(position.Position, point, DefaultSightRadius)
        && HasLineOfSight(position.Position, point);

    public IReadOnlyList<SuspicionCapturePlan> PlanEffectSuspicion(
        GridPoint effectPoint,
        string kind,
        Entity? actor = null)
    {
        var actorSoulId = actor is null ? null : SoulIdFor(actor);
        var actorOrigin = actor?.TryGet<PositionComponent>(out var position) == true
            ? position.Position
            : (GridPoint?)null;
        var plans = new List<SuspicionCapturePlan>();
        foreach (var observation in ClassifyEffectWitnesses(actorOrigin, effectPoint, actor))
        {
            if (!observation.SawEffect)
            {
                continue;
            }

            var seesActor = observation.SawActor;
            plans.Add(new SuspicionCapturePlan(
                observation.WitnessSoulId,
                kind,
                effectPoint,
                seesActor ? "attributed" : "pending",
                seesActor ? actorSoulId : null,
                seesActor ? _state.Turn : null,
                _state.Turn + SuspicionAttributionWindow));
        }

        return plans;
    }

    public IReadOnlyList<SuspicionUpdateProposal> PendingSuspicionUpdates()
    {
        var player = _state.ControlledEntity;
        if (!player.TryGet<PositionComponent>(out var playerPosition))
        {
            return Array.Empty<SuspicionUpdateProposal>();
        }

        var playerSoulId = SoulIdFor(player);
        var updates = new List<SuspicionUpdateProposal>();
        foreach (var suspicion in _state.Suspicions.Records
            .Where(record => record.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            if (suspicion.ExpiresTurn < _state.Turn)
            {
                updates.Add(new SuspicionUpdateProposal(suspicion.Id, "expired", suspicion.SuspectedSoulId, suspicion.AttributedTurn));
                continue;
            }

            var witness = _state.Entities.Values.FirstOrDefault(entity =>
                SoulIdFor(entity).Equals(suspicion.WitnessSoulId, StringComparison.OrdinalIgnoreCase));
            if (witness is null
                || !witness.TryGet<PositionComponent>(out var witnessPosition)
                || !IsWithinSightRadius(witnessPosition.Position, playerPosition.Position, DefaultSightRadius)
                || !HasLineOfSight(witnessPosition.Position, playerPosition.Position)
                || !CanPerceiveSubject(witness, player))
            {
                continue;
            }

            updates.Add(new SuspicionUpdateProposal(suspicion.Id, "attributed", playerSoulId, _state.Turn));
        }

        return updates;
    }

    public string RelationToPlayer(GridPoint point, PerceptionSnapshot snapshot)
    {
        if (snapshot.VisibleTiles.Contains(point))
        {
            return "visible";
        }

        return snapshot.ExploredTiles.Contains(point) ? "explored" : "hidden_from_player";
    }

    public bool BlocksSight(GridPoint point)
    {
        if (_state.BlockingTerrain.Contains(point))
        {
            return true;
        }

        return _state.Entities.Values.Any(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point
            && entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksSight);
    }

    public bool HasLineOfSight(GridPoint from, GridPoint to)
    {
        if (!InBounds(from) || !InBounds(to))
        {
            return false;
        }

        if (from == to)
        {
            return true;
        }

        foreach (var point in Line(from, to).Skip(1))
        {
            if (point == to)
            {
                return true;
            }

            if (BlocksSight(point))
            {
                return false;
            }
        }

        return true;
    }

    private PerceptionSnapshot ComputeFor(Entity viewer, int radius)
    {
        if (!viewer.TryGet<PositionComponent>(out var viewerPosition))
        {
            return new PerceptionSnapshot(
                SoulIdFor(viewer),
                new HashSet<GridPoint>(),
                ExploredSnapshotForSoul(SoulIdFor(viewer)),
                new HashSet<EntityId>());
        }

        var origin = viewerPosition.Position;
        var visibleTiles = new HashSet<GridPoint>();
        for (var y = origin.Y - radius; y <= origin.Y + radius; y++)
        {
            for (var x = origin.X - radius; x <= origin.X + radius; x++)
            {
                var point = new GridPoint(x, y);
                if (!InBounds(point) || !IsWithinSightRadius(origin, point, radius))
                {
                    continue;
                }

                if (HasLineOfSight(origin, point))
                {
                    visibleTiles.Add(point);
                }
            }
        }

        var visibleEntities = _state.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && visibleTiles.Contains(position.Position))
            .Select(entity => entity.Id)
            .ToHashSet();
        var soulId = SoulIdFor(viewer);
        return new PerceptionSnapshot(
            soulId,
            visibleTiles,
            ExploredSnapshotForSoul(soulId),
            visibleEntities);
    }

    private IReadOnlySet<GridPoint> ExploredSnapshotForSoul(string soulId)
    {
        if (!_state.ExploredBySoulId.TryGetValue(soulId, out var explored))
        {
            return new HashSet<GridPoint>();
        }

        return new HashSet<GridPoint>(explored);
    }

    private bool InBounds(GridPoint point) =>
        point.X >= 0 && point.Y >= 0 && point.X < _state.Width && point.Y < _state.Height;

    private static bool IsLivingActor(Entity entity) =>
        entity.TryGet<ActorComponent>(out var actor) && actor.Alive;

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static bool IsWithinSightRadius(GridPoint a, GridPoint b, int radius)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy) <= (radius * radius) + radius;
    }

    private static IEnumerable<GridPoint> Line(GridPoint from, GridPoint to)
    {
        var x0 = from.X;
        var y0 = from.Y;
        var x1 = to.X;
        var y1 = to.Y;
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            yield return new GridPoint(x0, y0);
            if (x0 == x1 && y0 == y1)
            {
                yield break;
            }

            var e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }
}
