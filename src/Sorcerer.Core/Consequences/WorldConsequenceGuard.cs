using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Validation;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

public static class WorldConsequenceGuard
{
    public static WorldConsequenceApplyResult Apply(
        GameState state,
        WorldConsequence consequence,
        Func<WorldConsequence, WorldConsequenceApplyResult> apply)
    {
        var beforeValidation = StateValidator.Validate(state);
        var snapshot = GameStateSnapshot.Capture(state);
        WorldConsequenceApplyResult applied;
        try
        {
            applied = apply(consequence);
        }
        catch (Exception ex)
        {
            snapshot.Restore(state);
            return ExceptionRollback(consequence, ex);
        }

        if (!applied.Applied)
        {
            snapshot.Restore(state);
            return EnsureRejectedDelta(consequence, applied);
        }

        var afterValidation = StateValidator.Validate(state);
        if (!beforeValidation.IsValid || afterValidation.IsValid)
        {
            return applied;
        }

        snapshot.Restore(state);
        return InvalidStateRollback(consequence, afterValidation);
    }

    public static WorldConsequenceApplyResult ApplyWithNewApplier(
        GameState state,
        WorldConsequence consequence) =>
        Apply(state, consequence, item => new WorldConsequenceApplier(state).Apply(item));

    private static WorldConsequenceApplyResult InvalidStateRollback(
        WorldConsequence consequence,
        StateValidationReport validation)
    {
        var normalizedType = WorldConsequenceTypes.Normalize(consequence.Type);
        var issues = validation.Issues
            .Select(issue => $"{issue.Code}:{issue.EntityId ?? "world"}:{issue.Message}")
            .ToArray();
        var issueCodes = validation.Issues
            .Select(issue => issue.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var summary = $"Consequence {normalizedType} was rolled back after invalid state: {issues.FirstOrDefault() ?? "unknown validation issue"}.";
        var details = new Dictionary<string, object?>
        {
            ["consequenceType"] = normalizedType,
            ["source"] = consequence.Source,
            ["sourceEntityId"] = consequence.SourceEntityId,
            ["targetEntityId"] = consequence.TargetEntityId,
            ["validationIssues"] = issues,
            ["issueCodes"] = issueCodes,
            ["rolledBack"] = true,
            ["auditOnly"] = true,
            ["playerVisible"] = false,
        };
        var delta = new StateDelta(
            "worldConsequenceRejected",
            consequence.TargetEntityId ?? consequence.SourceEntityId ?? consequence.Source,
            summary,
            details);
        return new WorldConsequenceApplyResult(
            false,
            consequence.TargetEntityId,
            summary,
            Array.Empty<string>(),
            new[] { delta },
            details);
    }

    private static WorldConsequenceApplyResult ExceptionRollback(
        WorldConsequence consequence,
        Exception exception)
    {
        var normalizedType = WorldConsequenceTypes.Normalize(consequence.Type);
        var error = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        var summary = $"Consequence {normalizedType} was rolled back after apply threw {exception.GetType().Name}: {error}";
        var details = new Dictionary<string, object?>
        {
            ["consequenceType"] = normalizedType,
            ["source"] = consequence.Source,
            ["sourceEntityId"] = consequence.SourceEntityId,
            ["targetEntityId"] = consequence.TargetEntityId,
            ["exceptionType"] = exception.GetType().Name,
            ["error"] = error,
            ["rolledBack"] = true,
            ["auditOnly"] = true,
            ["playerVisible"] = false,
        };
        var delta = new StateDelta(
            "worldConsequenceRejected",
            consequence.TargetEntityId ?? consequence.SourceEntityId ?? consequence.Source,
            summary,
            details);
        return new WorldConsequenceApplyResult(
            false,
            consequence.TargetEntityId,
            error,
            Array.Empty<string>(),
            new[] { delta },
            details);
    }

    private static WorldConsequenceApplyResult EnsureRejectedDelta(
        WorldConsequence consequence,
        WorldConsequenceApplyResult applied)
    {
        if (applied.Deltas.Any(delta =>
            delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase)))
        {
            return applied;
        }

        var normalizedType = WorldConsequenceTypes.Normalize(consequence.Type);
        var error = string.IsNullOrWhiteSpace(applied.Error)
            ? $"Consequence {normalizedType} returned without applying."
            : applied.Error!;
        var summary = $"Consequence {normalizedType} was rolled back after apply returned false: {error}";
        var details = new Dictionary<string, object?>(applied.Details, StringComparer.OrdinalIgnoreCase)
        {
            ["consequenceType"] = normalizedType,
            ["source"] = consequence.Source,
            ["sourceEntityId"] = consequence.SourceEntityId,
            ["targetEntityId"] = consequence.TargetEntityId,
            ["error"] = error,
            ["rolledBack"] = true,
            ["auditOnly"] = true,
            ["playerVisible"] = false,
        };
        var delta = new StateDelta(
            "worldConsequenceRejected",
            consequence.TargetEntityId ?? consequence.SourceEntityId ?? consequence.Source,
            summary,
            details);
        return new WorldConsequenceApplyResult(
            false,
            applied.TargetId ?? consequence.TargetEntityId,
            applied.Error ?? error,
            applied.Messages,
            applied.Deltas.Concat(new[] { delta }).ToArray(),
            details);
    }
}
