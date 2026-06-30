using Sorcerer.Core.Entities;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Validation;

public sealed record StateValidationIssue(string Code, string Message, string? EntityId = null);

public sealed record StateValidationReport(IReadOnlyList<StateValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public static class StateValidator
{
    public static StateValidationReport Validate(GameState state)
    {
        var issues = new List<StateValidationIssue>();
        if (!state.Entities.ContainsKey(state.ControlledEntityId))
        {
            issues.Add(new StateValidationIssue(
                "missing_controlled_entity",
                $"Controlled entity {state.ControlledEntityId} does not exist."));
        }

        var blockingPositions = new Dictionary<(int X, int Y), string>();
        foreach (var entity in state.Entities.Values)
        {
            if (!entity.TryGet<PositionComponent>(out var position))
            {
                continue;
            }

            if (position.Position.X < 0
                || position.Position.Y < 0
                || position.Position.X >= state.Width
                || position.Position.Y >= state.Height)
            {
                issues.Add(new StateValidationIssue(
                    "entity_out_of_bounds",
                    $"{entity.Name} is outside the map.",
                    entity.Id.Value));
            }

            if (!entity.TryGet<PhysicalComponent>(out var physical) || !physical.BlocksMovement)
            {
                continue;
            }

            var key = (position.Position.X, position.Position.Y);
            if (blockingPositions.TryGetValue(key, out var other))
            {
                issues.Add(new StateValidationIssue(
                    "blocking_overlap",
                    $"{entity.Id.Value} overlaps blocking entity {other}.",
                    entity.Id.Value));
            }
            else
            {
                blockingPositions[key] = entity.Id.Value;
            }
        }

        return new StateValidationReport(issues);
    }
}
