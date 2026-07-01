using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.References;
using Sorcerer.Core.World;

namespace Sorcerer.Magic.Resolution;

public static class MechanicalCurseValidator
{
    public static IReadOnlyList<SpellValidationIssue> Validate(GameEngine engine, SpellResolution resolution)
    {
        var curses = ActiveCurses(engine.State).ToArray();
        if (curses.Length == 0)
        {
            return Array.Empty<SpellValidationIssue>();
        }

        var issues = new List<SpellValidationIssue>();
        foreach (var curse in curses)
        {
            var template = CurseTemplate(curse);
            if (template.Length == 0)
            {
                continue;
            }

            foreach (var effect in resolution.Effects)
            {
                ValidateEffect(engine, curse, template, effect, issues);
            }
        }

        return issues;
    }

    private static IEnumerable<WorldPromise> ActiveCurses(GameState state) =>
        state.PromiseLedger.Promises
            .Where(promise => promise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase))
            .Where(promise => !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
                && !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase));

    private static void ValidateEffect(
        GameEngine engine,
        WorldPromise curse,
        string template,
        SpellEffect effect,
        List<SpellValidationIssue> issues)
    {
        if (IsSelfOnly(effect))
        {
            return;
        }

        var targetPosition = ResolveTargetPosition(engine, effect);
        var playerPosition = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var radius = ReadInt(effect.Fields, "radius", 0);
        switch (template)
        {
            case "close":
                if (targetPosition is { } closeTarget
                    && GameEngine.Distance(playerPosition, closeTarget) > 3)
                {
                    Reject(issues, curse, "Close curse: magic must stay within 3 tiles.");
                }

                break;
            case "far":
                if (targetPosition is { } farTarget
                    && GameEngine.Distance(playerPosition, farTarget) < 3)
                {
                    Reject(issues, curse, "Far curse: magic cannot take hold within 3 tiles.");
                }

                break;
            case "narrow":
                if (radius > 1
                    || effect.Type.Equals("areaDamage", StringComparison.OrdinalIgnoreCase)
                    || ReadText(effect.Fields, "affects", "").Contains("all", StringComparison.OrdinalIgnoreCase))
                {
                    Reject(issues, curse, "Narrow curse: magic cannot spread wider than a single target.");
                }

                break;
            case "straight-path":
            case "straight":
                if (targetPosition is { } straightTarget
                    && straightTarget.X != playerPosition.X
                    && straightTarget.Y != playerPosition.Y)
                {
                    Reject(issues, curse, "Straight-path curse: magic must travel along a rank or file.");
                }

                break;
            case "anchored":
                if (engine.State.SelectedTarget is null && !IsSelfOnly(effect))
                {
                    Reject(issues, curse, "Anchored curse: choose a target point before casting outward magic.");
                }

                break;
        }
    }

    private static GridPoint? ResolveTargetPosition(GameEngine engine, SpellEffect effect)
    {
        if (TryPoint(effect.Fields, out var point))
        {
            return point;
        }

        var rawTarget = effect.Fields.TryGetValue("target", out var target)
            ? target
            : DefaultTarget(effect.Type);
        if (rawTarget is null)
        {
            return null;
        }

        var bound = ReferenceBinder.Bind(engine, ReferenceBinder.Normalize(rawTarget));
        if (!bound.Success)
        {
            return null;
        }

        if (bound.Position is { } boundPoint)
        {
            return boundPoint;
        }

        return bound.Entity is not null && bound.Entity.TryGet<PositionComponent>(out var position)
            ? position.Position
            : null;
    }

    private static object? DefaultTarget(string effectType) =>
        effectType.Trim().ToLowerInvariant() switch
        {
            "damage" or "addstatus" or "push" or "pull" or "transformentity" or "changefaction" or "possess" or "addtrait" => "nearest_enemy",
            "heal" or "removestatus" => "self",
            _ => null,
        };

    private static bool IsSelfOnly(SpellEffect effect)
    {
        var target = ReadText(effect.Fields, "target", "");
        return target.Equals("self", StringComparison.OrdinalIgnoreCase)
            || target.Equals("player", StringComparison.OrdinalIgnoreCase)
            || effect.Type.Equals("heal", StringComparison.OrdinalIgnoreCase)
            || effect.Type.Equals("restoreMana", StringComparison.OrdinalIgnoreCase)
            || effect.Type.Equals("message", StringComparison.OrdinalIgnoreCase)
            || effect.Type.Equals("createPromise", StringComparison.OrdinalIgnoreCase)
            || effect.Type.Equals("addCurse", StringComparison.OrdinalIgnoreCase);
    }

    private static string CurseTemplate(WorldPromise curse)
    {
        var text = $"{curse.TriggerHint} {curse.Text}".ToLowerInvariant();
        if (text.Contains("straight", StringComparison.OrdinalIgnoreCase))
        {
            return "straight-path";
        }

        foreach (var template in new[] { "close", "far", "narrow", "anchored" })
        {
            if (text.Contains(template, StringComparison.OrdinalIgnoreCase))
            {
                return template;
            }
        }

        return "";
    }

    private static void Reject(List<SpellValidationIssue> issues, WorldPromise curse, string message)
    {
        if (issues.Any(issue => issue.Message.Equals(message, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        issues.Add(new SpellValidationIssue("curse_limit", $"{message} ({curse.Text})"));
    }

    private static bool TryPoint(IReadOnlyDictionary<string, object?> fields, out GridPoint point)
    {
        if (ReadInt(fields, "x", int.MinValue) is var x
            && x != int.MinValue
            && ReadInt(fields, "y", int.MinValue) is var y
            && y != int.MinValue)
        {
            point = new GridPoint(x, y);
            return true;
        }

        point = default;
        return false;
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> fields, string key, int fallback) =>
        fields.TryGetValue(key, out var value) && int.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : fallback;

    private static string ReadText(IReadOnlyDictionary<string, object?> fields, string key, string fallback) =>
        fields.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;
}
