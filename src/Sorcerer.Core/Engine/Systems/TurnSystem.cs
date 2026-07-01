using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Status;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class TurnSystem
{
    private readonly GameEngine _engine;
    private readonly TriggerSystem _triggers;
    private readonly TerrainReactionSystem _terrainReactions;
    private readonly LoreCatalog _loreCatalog;
    private readonly GameState _state;
    private readonly StatusRegistry _statusRegistry;
    private readonly WorldReactionSystem _worldReactions = new();

    public TurnSystem(GameEngine engine, GameState state, StatusRegistry statusRegistry, LoreCatalog loreCatalog)
    {
        _engine = engine;
        _triggers = new TriggerSystem(engine);
        _terrainReactions = new TerrainReactionSystem(engine);
        _loreCatalog = loreCatalog;
        _state = state;
        _statusRegistry = statusRegistry;
    }

    public void AdvanceTurn()
    {
        _state.Turn += 1;
        ExpireStatuses();
        ApplyTerrainReactions();
        ApplyStatusTicks();
        ReleaseDueDelayedDamage();
        ApplyTileFlows();
        ExpireTerrain();
        ResolveScheduledEvents();
        ResolveTriggers();
        ApplyWorldReactions();
        PumpBackgroundJobs();
    }

    private void ApplyTileFlows()
    {
        var expired = _state.TileFlows
            .Where(pair => pair.Value.ExpiresTurn is { } expiry && expiry <= _state.Turn)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var point in expired)
        {
            _state.TileFlows.Remove(point);
        }

        foreach (var entity in _state.Entities.Values
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out var position) && _state.TileFlows.ContainsKey(position.Position))
            .OrderBy(entity => entity.Id.Value)
            .ToArray())
        {
            var position = entity.Get<PositionComponent>().Position;
            var flow = _state.TileFlows[position];
            var destination = position.Translate(flow.Dx, flow.Dy);
            if (!_engine.InBounds(destination)
                || _state.BlockingTerrain.Contains(destination)
                || _engine.BlockingEntityAt(destination) is not null)
            {
                continue;
            }

            entity.Set(new PositionComponent(destination));
            _state.AddMessage($"{Subject(entity)} {Verb(entity, "slide", "slides")} across the flowing ground.");
        }
    }

    private void ReleaseDueDelayedDamage()
    {
        foreach (var entity in _state.Entities.Values
            .Where(entity => entity.TryGet<DelayedDamageComponent>(out var buffer) && buffer.ReleaseTurn <= _state.Turn)
            .OrderBy(entity => entity.Id.Value)
            .ToArray())
        {
            _engine.ReleaseDelayedDamage(entity);
        }
    }

    public void EnqueueBackgroundJob(string purpose, Entity target, int priority)
    {
        if (!_state.BackgroundSettings.Enabled)
        {
            return;
        }

        var activeCount = _state.BackgroundJobs.Jobs.Count(job =>
            job.State is BackgroundJobState.Queued or BackgroundJobState.Running or BackgroundJobState.Completed);
        if (activeCount >= _state.BackgroundSettings.MaxQueuedJobs
            || _state.BackgroundJobs.HasActiveJob(purpose, target.Id.Value)
            || _state.Canon.Records.Any(record =>
                record.AttachedTo.Equals(target.Id.Value, StringComparison.OrdinalIgnoreCase)
                && record.Kind.Equals(purpose, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var job = _state.BackgroundJobs.Enqueue(purpose, target.Id.Value, priority, _state.Turn);
        _state.AddMessage($"Background job queued: {job.Purpose} for {target.Name}.");
    }

    private void ExpireStatuses()
    {
        foreach (var entity in _state.Entities.Values)
        {
            if (!entity.TryGet<StatusContainerComponent>(out var container))
            {
                continue;
            }

            var active = container.Statuses.Where(IsStatusActive).ToArray();
            if (active.Length == container.Statuses.Count)
            {
                continue;
            }

            entity.Set(new StatusContainerComponent(active));
        }
    }

    private void ApplyStatusTicks()
    {
        foreach (var entity in _state.Entities.Values.OrderBy(entity => entity.Id.Value))
        {
            if (!entity.TryGet<ActorComponent>(out var actor)
                || !actor.Alive
                || !entity.TryGet<StatusContainerComponent>(out var container))
            {
                continue;
            }

            var active = container.Statuses.Where(IsStatusActive).ToArray();
            var damage = active.Sum(status => _statusRegistry.DamagePerTurn(status.Id) * Math.Max(1, status.Intensity));
            var healing = active.Sum(status => _statusRegistry.HealPerTurn(status.Id) * Math.Max(1, status.Intensity));
            if (damage > 0)
            {
                var updated = actor with { HitPoints = Math.Max(0, actor.HitPoints - damage) };
                entity.Set(updated);
                if (!updated.Alive)
                {
                    CombatSystem.MarkDefeated(entity);
                    _state.AddMessage($"{Subject(entity)} {Verb(entity, "fall", "falls")} to ongoing harm.");
                    continue;
                }

                _state.AddMessage($"{Subject(entity)} {Verb(entity, "take", "takes")} {damage} ongoing harm.");
                actor = updated;
            }

            if (healing <= 0 || !actor.Alive)
            {
                continue;
            }

            var healed = Math.Min(healing, actor.MaxHitPoints - actor.HitPoints);
            if (healed <= 0)
            {
                continue;
            }

            entity.Set(actor with { HitPoints = actor.HitPoints + healed });
            _state.AddMessage($"{Subject(entity)} {Verb(entity, "regenerate", "regenerates")} {healed} HP.");
        }
    }

    private void ExpireTerrain()
    {
        var expired = _state.TerrainExpirations
            .Where(pair => pair.Value <= _state.Turn)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var point in expired)
        {
            var terrain = _state.Terrain.TryGetValue(point, out var existing)
                ? existing
                : "terrain";
            _state.TerrainExpirations.Remove(point);
            _state.Terrain.Remove(point);
            if (!IsBoundaryWall(point))
            {
                _state.BlockingTerrain.Remove(point);
            }

            _state.AddMessage($"The {terrain.Replace('_', ' ')} at {point.X},{point.Y} fades.");
        }
    }

    private void ResolveScheduledEvents()
    {
        foreach (var scheduled in _state.ScheduledEvents.PopDue(_state.Turn))
        {
            if (scheduled.Kind.Equals("empire_patrol", StringComparison.OrdinalIgnoreCase))
            {
                ResolveEmpirePatrol(scheduled);
                continue;
            }

            var text = scheduled.Payload.TryGetValue("text", out var rawText)
                ? Convert.ToString(rawText)
                : null;
            var description = scheduled.Payload.TryGetValue("description", out var rawDescription)
                ? Convert.ToString(rawDescription)
                : null;
            var message = !string.IsNullOrWhiteSpace(text)
                ? text!
                : !string.IsNullOrWhiteSpace(description)
                    ? description!
                    : $"Delayed magic comes due: {scheduled.Kind}.";
            _state.AddMessage(message);
        }
    }

    private void ResolveEmpirePatrol(ScheduledEventRecord scheduled)
    {
        var text = scheduled.Payload.TryGetValue("text", out var rawText)
            ? Convert.ToString(rawText)
            : null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _state.AddMessage(text!);
        }

        var position = FindPatrolSpawnPoint();
        if (position is null)
        {
            _state.AddMessage("An imperial patrol loses the trail before it can enter the room.");
            return;
        }

        var id = _state.NextEntityId("imperial_patrol");
        var patrol = new Entity(id, "imperial patrol-censor")
            .Set(new PositionComponent(position.Value))
            .Set(new RenderableComponent('i', "imperial"))
            .Set(new TagsComponent(new[] { "imperial", "patrol", "censorate" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new BodyStatsComponent(1))
            .Set(new ActorComponent(8, 8, 0, 0, 2, 0, "empire"))
            .Set(new FactionComponent("empire", new[] { "empire", "censorate", "patrol" }))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("imperial_patrol"))
            .Set(StatusContainerComponent.Empty())
            .Set(new SoulComponent($"{id.Value}_soul"));
        _state.Entities[id] = patrol;
        _state.AddMessage("An imperial patrol-censor enters with a folder full of careful fear.");
    }

    private void ApplyTerrainReactions()
    {
        _terrainReactions.ApplyTurnReactions();
    }

    private void ResolveTriggers()
    {
        _triggers.ApplyDue();
    }

    private void PumpBackgroundJobs()
    {
        if (!_state.BackgroundSettings.Enabled)
        {
            return;
        }

        for (var count = 0; count < _state.BackgroundSettings.JobsPerTurn; count++)
        {
            var queued = _state.BackgroundJobs.NextQueued();
            if (queued is null)
            {
                return;
            }

            var running = queued with
            {
                State = BackgroundJobState.Running,
                StartedTurn = _state.Turn,
            };
            _state.BackgroundJobs.Replace(running);

            try
            {
                var text = GenerateBackgroundText(running);
                var completed = running with
                {
                    State = BackgroundJobState.Completed,
                    CompletedTurn = _state.Turn,
                    ResultText = text,
                };
                _state.BackgroundJobs.Replace(completed);
                ApplyBackgroundJob(completed);
            }
            catch (Exception ex)
            {
                _state.BackgroundJobs.Replace(running with
                {
                    State = BackgroundJobState.Failed,
                    Error = ex.Message,
                });
            }
        }
    }

    private void ApplyWorldReactions()
    {
        _worldReactions.ApplyPending(_state);
    }

    private string GenerateBackgroundText(BackgroundJob job)
    {
        var target = _state.Entities.Values.FirstOrDefault(entity =>
            entity.Id.Value.Equals(job.TargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return $"Background detail for missing target {job.TargetId}.";
        }

        var tags = TagsFor(target);
        var material = target.TryGet<PhysicalComponent>(out var physical) ? physical.Material : "unknown matter";
        var lore = LoreRouter.Select(
            _loreCatalog,
            new LoreQuery(
                tags.Append(material).Append(target.Name).Append(_state.RegionId).ToArray(),
                new[] { job.Purpose, "background", _state.RegionId },
                LoreAccessLevel(),
                Limit: 1))
            .FirstOrDefault();
        var loreLine = lore is null ? "" : $" {OneLine(lore.Body)}";
        return job.Purpose switch
        {
            "canon_detail" => $"{target.Name} gains a quiet margin note: {DescribeTradition(tags)}{loreLine}",
            "entity_detail" => $"{target.Name} is {material}, tagged by {DescribeTags(tags)}, and waiting for a spell to make that matter.{loreLine}",
            _ => $"{target.Name} gains background detail for {job.Purpose}.",
        };
    }

    private void ApplyBackgroundJob(BackgroundJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ResultText))
        {
            _state.BackgroundJobs.Replace(job with
            {
                State = BackgroundJobState.Failed,
                Error = "Background job produced no text.",
            });
            return;
        }

        _state.Canon.Add(
            job.Purpose,
            job.TargetId,
            job.ResultText,
            job.ResultText.Length <= 80 ? job.ResultText : $"{job.ResultText[..77]}...",
            Array.Empty<string>(),
            "background",
            _state.Turn);
        _state.BackgroundJobs.Replace(job with
        {
            State = BackgroundJobState.Applied,
            AppliedTurn = _state.Turn,
        });
        _state.AddMessage($"Background detail settles onto {job.TargetId}.");
    }

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > _state.Turn;

    private int LoreAccessLevel()
    {
        var canonDepth = _state.Canon.Records.Count(record =>
            record.Kind.Equals("readable", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("canon_detail", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("entity_detail", StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(1 + (canonDepth / 2), 1, 3);
    }

    private static string OneLine(string text)
    {
        var normalized = text.Replace('\n', ' ').Trim();
        return normalized.Length <= 160 ? normalized : $"{normalized[..157]}...";
    }

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;

    private bool IsBoundaryWall(GridPoint point) =>
        point.X == 0 || point.Y == 0 || point.X == _state.Width - 1 || point.Y == _state.Height - 1;

    private GridPoint? FindPatrolSpawnPoint()
    {
        var playerPosition = _state.ControlledEntity.TryGet<PositionComponent>(out var player)
            ? player.Position
            : new GridPoint(_state.Width / 2, _state.Height / 2);
        var candidates = new[]
        {
            playerPosition.Translate(5, 0),
            playerPosition.Translate(-5, 0),
            playerPosition.Translate(0, 4),
            playerPosition.Translate(0, -4),
            new GridPoint(1, 1),
            new GridPoint(_state.Width - 2, 1),
            new GridPoint(1, _state.Height - 2),
            new GridPoint(_state.Width - 2, _state.Height - 2),
        };

        foreach (var point in candidates)
        {
            if (CanSpawnAt(point))
            {
                return point;
            }
        }

        for (var y = 1; y < _state.Height - 1; y++)
        {
            for (var x = 1; x < _state.Width - 1; x++)
            {
                var point = new GridPoint(x, y);
                if (CanSpawnAt(point))
                {
                    return point;
                }
            }
        }

        return null;
    }

    private bool CanSpawnAt(GridPoint point) =>
        point.X > 0
        && point.Y > 0
        && point.X < _state.Width - 1
        && point.Y < _state.Height - 1
        && !_state.BlockingTerrain.Contains(point)
        && !_state.Entities.Values.Any(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point
            && entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement
            && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive));

    private static string DescribeTradition(IReadOnlyList<string> tags)
    {
        if (tags.Contains("law", StringComparer.OrdinalIgnoreCase)
            || tags.Contains("imperial", StringComparer.OrdinalIgnoreCase))
        {
            return "marble law tries to make the world hold still.";
        }

        if (tags.Contains("hollowmere", StringComparer.OrdinalIgnoreCase)
            || tags.Contains("water", StringComparer.OrdinalIgnoreCase))
        {
            return "water remembers names the empire tried to flatten.";
        }

        if (tags.Contains("fire", StringComparer.OrdinalIgnoreCase))
        {
            return "fire is treated here as witness, appetite, and warning.";
        }

        return "the world keeps a little color in reserve.";
    }

    private static string DescribeTags(IReadOnlyList<string> tags) =>
        tags.Count == 0 ? "no obvious tradition" : string.Join(", ", tags.Take(6));

    private static IReadOnlyList<string> TagsFor(Entity entity)
    {
        var tags = new List<string>();
        if (entity.TryGet<TagsComponent>(out var tagComponent))
        {
            tags.AddRange(tagComponent.Tags);
        }

        if (entity.TryGet<ItemComponent>(out var item))
        {
            tags.AddRange(item.Tags);
            tags.Add(item.Material);
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            tags.AddRange(fixture.Tags);
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag).ToArray();
    }
}
