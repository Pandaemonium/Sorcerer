using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Validation;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

public static class WorldConsequenceGuard
{
    // Reentrancy depth for EnterScope. Single-threaded per GameState (the engine has no
    // concurrent mutation model), so a thread-static counter is sufficient and avoids
    // threading a scope handle through every ApplyConsequence call site.
    [ThreadStatic]
    private static int _scopeDepth;

    /// <summary>
    /// Marks that the caller already owns a whole-batch snapshot (a <see cref="GameTransaction"/>
    /// or an explicit <see cref="GameStateSnapshot"/>) and will restore it if any consequence
    /// applied within the scope is rejected. Nested <see cref="Apply"/> calls then skip their own
    /// per-consequence snapshot and validation pass, since the outer scope's restore already
    /// covers them -- see WildMagicController.ApplyResolved, WorldTurnSystem.TryApplyWorldTurnTransaction,
    /// and RumorSystem.Propagate for the pattern. Without this, a batch of N consequences applied
    /// inside one already-guarded transaction pays for N+1 full deep-clones of the game state
    /// instead of one, which compounds badly over a long run as ledgers and the entity set grow.
    /// Callers that use this MUST revalidate the whole state once after the batch and roll back
    /// on invalidity themselves (WildMagicController already does; TryApplyWorldTurnTransaction and
    /// RumorSystem.Propagate were updated to do the same) -- the nested fast path below does not.
    /// </summary>
    public static IDisposable EnterScope() => new ScopeHandle();

    private sealed class ScopeHandle : IDisposable
    {
        private bool _disposed;

        public ScopeHandle() => _scopeDepth++;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _scopeDepth--;
            }
        }
    }

    public static WorldConsequenceApplyResult Apply(
        GameState state,
        WorldConsequence consequence,
        Func<WorldConsequence, WorldConsequenceApplyResult> apply)
    {
        if (_scopeDepth > 0)
        {
            return ApplyNested(consequence, apply);
        }

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

    private static WorldConsequenceApplyResult ApplyNested(
        WorldConsequence consequence,
        Func<WorldConsequence, WorldConsequenceApplyResult> apply)
    {
        WorldConsequenceApplyResult applied;
        try
        {
            applied = apply(consequence);
        }
        catch (Exception ex)
        {
            return ExceptionRollback(consequence, ex);
        }

        return applied.Applied ? applied : EnsureRejectedDelta(consequence, applied);
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
