using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;
using Sorcerer.Magic.Resolution;
using static Sorcerer.Magic.Operations.OperationHelpers;

namespace Sorcerer.Magic.Operations;

/// <summary>
/// Group 4: the "can do anything" gap-closers surfaced by playtesting — animation of corpses
/// and objects, dispelling active magic, and engine-truth divination. Each routes through the
/// shared consequence grammar; none invents a new mutation path.
/// </summary>
public sealed class AnimateEntityOperation : OperationBase
{
    public AnimateEntityOperation()
        : base(
            "animateEntity",
            new[] { "animate", "animate_entity", "raiseDead", "raise_dead", "animateCorpse", "animate_corpse", "animateObject", "animate_object" },
            "Make an existing corpse, prop, statue, or floor object rise and act.",
            "Fields: target (a defeated actor or an inert fixture/prop/floor item already in the "
                + "world), faction (default 'player' so it serves the caster), hp (1-12), attack "
                + "(0-4), name (optional new name). Use conjureCreature to create something new "
                + "from nothing; use animateEntity to wake something that is already here. This "
                + "is major magic: pair it with a real cost.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect)
    {
        var resolved = ResolveTargetSet(context, effect, "selected_target");
        if (IsMalformedTarget(resolved))
        {
            return ValidationOutcome.Technical(resolved.Error ?? "Malformed target reference.");
        }

        return EligibleTargets(context, effect).Count > 0
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject("Animation needs a corpse or an inert object standing in the world.");
    }

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var faction = Text(effect, "faction", "player");
        var deltas = new List<StateDelta>();
        foreach (var target in EligibleTargets(context, effect))
        {
            deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.AnimateEntity(
                "wild_magic",
                target.Id.Value,
                faction,
                hp: effect.Fields.ContainsKey("hp") ? Int(effect, "hp", 6, min: 1, max: 12) : null,
                attack: effect.Fields.ContainsKey("attack") ? Int(effect, "attack", 2, min: 0, max: 4) : null,
                name: Text(effect, "name", "") is { Length: > 0 } rename ? rename : null,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: context.Caster.Id.Value)).Deltas);
        }

        if (deltas.Count == 0)
        {
            deltas.AddRange(Messages(context, "animateEntity", "", "Nothing here answers the call to rise."));
        }

        return deltas;
    }

    private static IReadOnlyList<Entity> EligibleTargets(EffectContext context, SpellEffect effect) =>
        ResolveTargets(context, effect, "selected_target")
            .Where(entity => entity.Id != context.Engine.State.ControlledEntityId
                && entity.Has<PositionComponent>()
                && (!entity.TryGet<ActorComponent>(out var actor) || !actor.Alive))
            .ToArray();
}

public sealed class DispelMagicOperation : OperationBase
{
    private static readonly string[] SystemStatuses = { "borrowed_body", "soul_swapped" };

    public DispelMagicOperation()
        : base(
            "dispelMagic",
            new[] { "dispel", "dispel_magic", "unravel", "counterspell", "breakEnchantment", "break_enchantment" },
            "Unravel active magic: statuses, wards, triggers, persistent enchantments, and tile flows.",
            "Fields: target (an entity whose magic should be stripped; default the caster), "
                + "x/y (a tile whose flows and anchored wards should end), scope "
                + "(all, statuses, triggers, persistent, flows; default all), radius (0-3 around "
                + "the tile). Use this to end an ongoing enchantment, cancel a ward or delayed "
                + "spell, calm enchanted ground, or strip a curse-adjacent status - not to undo "
                + "ordinary wounds or refill resources.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect)
    {
        var resolved = ResolveTargetSet(context, effect, "self");
        if (IsMalformedTarget(resolved))
        {
            return ValidationOutcome.Technical(resolved.Error ?? "Malformed target reference.");
        }

        var plan = BuildPlan(context, effect);
        return plan.IsEmpty
            ? ValidationOutcome.Reject("There is no active magic there to unravel.")
            : ValidationOutcome.Pass;
    }

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var plan = BuildPlan(context, effect);
        var deltas = new List<StateDelta>();
        foreach (var (entityId, statusId) in plan.Statuses)
        {
            deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.RemoveStatus(
                "wild_magic",
                entityId,
                statusId,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: context.Caster.Id.Value,
                reason: "dispelled",
                operation: "dispelMagic")).Deltas);
        }

        foreach (var triggerId in plan.TriggerIds)
        {
            deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.UpdateTrigger(
                "wild_magic",
                triggerId,
                "remove",
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: context.Caster.Id.Value,
                reason: "dispelled",
                operation: "dispelMagic")).Deltas);
        }

        foreach (var effectId in plan.PersistentEffectIds)
        {
            deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.UpdatePersistentEffect(
                "wild_magic",
                effectId,
                "remove",
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: context.Caster.Id.Value,
                reason: "dispelled",
                operation: "dispelMagic")).Deltas);
        }

        foreach (var point in plan.FlowPoints)
        {
            deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.UpdateFlow(
                "wild_magic",
                point.X,
                point.Y,
                "remove",
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: context.Caster.Id.Value,
                reason: "dispelled",
                operation: "dispelMagic")).Deltas);
        }

        return deltas;
    }

    private sealed record DispelPlan(
        IReadOnlyList<(string EntityId, string StatusId)> Statuses,
        IReadOnlyList<string> TriggerIds,
        IReadOnlyList<string> PersistentEffectIds,
        IReadOnlyList<GridPoint> FlowPoints)
    {
        public bool IsEmpty =>
            Statuses.Count == 0 && TriggerIds.Count == 0 && PersistentEffectIds.Count == 0 && FlowPoints.Count == 0;
    }

    private static DispelPlan BuildPlan(EffectContext context, SpellEffect effect)
    {
        var scope = NormalizeScope(Text(effect, "scope", "all"));
        var state = context.Engine.State;
        var targets = ResolveTargets(context, effect, "self")
            .Where(entity => entity.Has<PositionComponent>() || entity.Has<ActorComponent>())
            .ToArray();
        var targetIds = new HashSet<string>(targets.Select(target => target.Id.Value), StringComparer.OrdinalIgnoreCase);
        var origin = ResolveOrigin(context, effect, "self");
        var radius = Int(effect, "radius", 1, min: 0, max: 3);

        var statuses = new List<(string, string)>();
        if (scope is "all" or "statuses")
        {
            foreach (var target in targets)
            {
                if (!target.TryGet<StatusContainerComponent>(out var container))
                {
                    continue;
                }

                statuses.AddRange(container.Statuses
                    .Where(status => !SystemStatuses.Contains(status.Id, StringComparer.OrdinalIgnoreCase))
                    .Select(status => (target.Id.Value, status.Id)));
            }
        }

        var triggerIds = new List<string>();
        if (scope is "all" or "triggers")
        {
            triggerIds.AddRange(state.Triggers.Records
                .Where(record =>
                    (record.AnchorEntityId is { } anchorId && targetIds.Contains(anchorId))
                    || (record.AnchorPoint is { } anchorPoint && origin is { } from
                        && GameEngineDistance(anchorPoint, from) <= radius))
                .Select(record => record.Id));
        }

        var persistentIds = new List<string>();
        if (scope is "all" or "persistent")
        {
            persistentIds.AddRange(state.PersistentEffects.Records
                .Where(record => record.RemainingUses > 0 && targetIds.Contains(record.AnchorEntityId))
                .Select(record => record.Id));
        }

        var flowPoints = new List<GridPoint>();
        if (scope is "all" or "flows" && origin is { } center)
        {
            flowPoints.AddRange(state.TileFlows.Keys
                .Where(point => GameEngineDistance(point, center) <= radius));
        }

        return new DispelPlan(statuses, triggerIds, persistentIds, flowPoints);
    }

    private static string NormalizeScope(string scope) =>
        scope.Trim().ToLowerInvariant() switch
        {
            "status" or "statuses" or "condition" or "conditions" => "statuses",
            "trigger" or "triggers" or "ward" or "wards" or "aura" or "auras" => "triggers",
            "persistent" or "enchantment" or "enchantments" or "link" or "links" => "persistent",
            "flow" or "flows" or "ground" or "tiles" => "flows",
            _ => "all",
        };
}

/// <summary>
/// Ends a durable curse through the same authoritative resolve_cost consequence used by charter
/// forms and mundane cleansing. A curse is promise-backed engine state; removing its status chip
/// alone is deliberately insufficient because runtime systems consult the promise.
/// </summary>
public sealed class ResolveCurseOperation : OperationBase
{
    public ResolveCurseOperation()
        : base(
            "resolveCurse",
            new[] { "resolve_curse", "cleanseCurse", "cleanse_curse", "liftCurse", "lift_curse" },
            "Resolve one active durable curse on a target.",
            "Fields: target (default player), profileId (the exact costProfileId shown on the "
                + "active curse promise; optional only when the target has one active curse), "
                + "curse/name/status (optional human-readable fallback). This clears both the "
                + "authoritative curse promise and its linked runtime status through resolve_cost. "
                + "Use removeStatus only for temporary conditions. Pair wild curse-breaking with "
                + "a meaningful cost or a concrete counterplay described by the spell.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["target"] = "Entity carrying the curse; defaults to player.",
                ["profileId"] = "Exact active promise costProfileId, such as curse_tide_debt_body.",
                ["curse"] = "Optional curse name when profileId is omitted.",
            },
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect)
    {
        var resolved = ResolveTargetSet(context, effect, "self");
        if (IsMalformedTarget(resolved))
        {
            return ValidationOutcome.Technical(resolved.Error ?? "Malformed target reference.");
        }

        return ResolveTargets(context, effect, "self").Any(target => ActiveCurse(context, target, effect) is not null)
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject("No matching active durable curse answers that working.");
    }

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var deltas = new List<StateDelta>();
        foreach (var target in ResolveTargets(context, effect, "self"))
        {
            var curse = ActiveCurse(context, target, effect);
            if (curse is null)
            {
                continue;
            }

            deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.ResolveCost(
                "wild_magic",
                target.Id.Value,
                curse.CostProfileId,
                category: "curse",
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: context.Caster.Id.Value,
                evidence: curse.Text,
                reason: "Validated wild magic resolved a durable curse through its shared authoritative record.",
                operation: "resolveCurse",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = curse.Id,
                    ["curseName"] = curse.Subject,
                })).Deltas);
        }

        return deltas;
    }

    private static WorldPromise? ActiveCurse(EffectContext context, Entity target, SpellEffect effect)
    {
        var reference = Text(
            effect,
            "profileId",
            Text(effect, "profile_id", Text(effect, "curse", Text(effect, "name", Text(effect, "status", "")))));
        var candidates = context.Engine.State.PromiseLedger.Promises
            .Where(promise => promise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase))
            .Where(promise => promise.Status is not "cleared" and not "fulfilled")
            .Where(promise => string.IsNullOrWhiteSpace(promise.BoundTargetId)
                || promise.BoundTargetId.Equals(target.Id.Value, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var normalized = NormalizeReference(reference);
            candidates = candidates.Where(promise =>
                promise.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)
                || promise.CostProfileId?.Equals(reference, StringComparison.OrdinalIgnoreCase) == true
                || NormalizeReference(promise.Subject).Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || NormalizeReference(promise.Text).Contains(normalized, StringComparison.OrdinalIgnoreCase));
        }

        return candidates
            .OrderByDescending(promise => promise.Salience)
            .ThenBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string NormalizeReference(string value) => string.Join(
        "_",
        new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray())
            .Split('_', StringSplitOptions.RemoveEmptyEntries));
}

public sealed class RevealTruthOperation : OperationBase
{
    private static readonly string[] Aspects = { "nature", "wants", "bond", "promises", "whereabouts", "threats" };

    public RevealTruthOperation()
        : base(
            "revealTruth",
            new[] { "reveal_truth", "divine", "scry", "augury", "readTarget", "read_target" },
            "Divination: the engine answers with what is actually true in the world.",
            "Fields: aspect (nature reads a target's condition, allegiance, and hostility and "
                + "marks it revealed; wants reads what a target desires; bond reads how a target "
                + "feels toward the caster; promises reads active promises touching the target; "
                + "whereabouts names the direction and distance of a named being - set subject to "
                + "its name; threats names the nearest hostile), target (for nature/wants/bond/"
                + "promises), subject (a name, for whereabouts). Every answer is engine truth, "
                + "never invention. Use addStatus for simple glowing/marking instead.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect)
    {
        var aspect = Aspect(effect);
        if (aspect == "whereabouts")
        {
            return string.IsNullOrWhiteSpace(Text(effect, "subject", Text(effect, "name", "")))
                ? ValidationOutcome.Reject("A whereabouts divination needs a subject name to seek.")
                : ValidationOutcome.Pass;
        }

        if (aspect == "threats")
        {
            return ValidationOutcome.Pass;
        }

        return RequireTargets(context, effect, "selected_target");
    }

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var aspect = Aspect(effect);
        return aspect switch
        {
            "whereabouts" => RevealWhereabouts(context, effect),
            "threats" => RevealThreats(context),
            "wants" => RevealForTargets(context, effect, RevealWants),
            "bond" => RevealForTargets(context, effect, RevealBond),
            "promises" => RevealForTargets(context, effect, RevealPromises),
            _ => RevealForTargets(context, effect, RevealNature),
        };
    }

    private static string Aspect(SpellEffect effect)
    {
        var aspect = Text(effect, "aspect", Text(effect, "question", "nature")).Trim().ToLowerInvariant();
        return aspect switch
        {
            "want" or "wants" or "desire" or "desires" => "wants",
            "bond" or "bonds" or "feeling" or "feelings" or "heart" => "bond",
            "promise" or "promises" or "oaths" or "debts" or "fate" => "promises",
            "whereabouts" or "location" or "where" or "direction" or "find" => "whereabouts",
            "threat" or "threats" or "danger" or "dangers" => "threats",
            _ => Aspects.Contains(aspect) ? aspect : "nature",
        };
    }

    private static IReadOnlyList<StateDelta> RevealForTargets(
        EffectContext context,
        SpellEffect effect,
        Func<EffectContext, Entity, IReadOnlyList<StateDelta>> reveal)
    {
        var deltas = new List<StateDelta>();
        foreach (var target in ResolveTargets(context, effect, "selected_target"))
        {
            deltas.AddRange(reveal(context, target));
        }

        return deltas.Count > 0
            ? deltas
            : Messages(context, "revealTruth", "", "The divination opens on an empty page.");
    }

    private static IReadOnlyList<StateDelta> RevealNature(EffectContext context, Entity target)
    {
        var deltas = new List<StateDelta>();
        var parts = new List<string>();
        if (target.TryGet<ActorComponent>(out var actor))
        {
            parts.Add($"sworn to {actor.Faction}");
            parts.Add(!actor.Alive ? "dead" : HealthBand(actor));
            if (actor.Alive)
            {
                parts.Add(context.Engine.IsHostile(target, context.Caster)
                    ? "hostile to you"
                    : "not hostile to you");
            }
        }
        else
        {
            parts.Add("no living thing");
            if (target.TryGet<PhysicalComponent>(out var physical))
            {
                parts.Add($"made of {physical.Material}");
            }
        }

        if (target.TryGet<StatusContainerComponent>(out var container) && container.Statuses.Count > 0)
        {
            parts.Add("touched by " + string.Join(", ", container.Statuses.Select(status => status.DisplayName)));
        }

        deltas.AddRange(Messages(
            context,
            "revealTruth",
            target.Id.Value,
            $"The truth of {target.Name}: {string.Join("; ", parts)}."));
        deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
            "wild_magic",
            target.Id.Value,
            "revealed",
            duration: 5,
            displayName: "revealed",
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: context.Caster.Id.Value,
            operation: "revealTruth",
            emitMessage: false)).Deltas);
        return deltas;
    }

    private static IReadOnlyList<StateDelta> RevealWants(EffectContext context, Entity target)
    {
        if (!target.TryGet<WantComponent>(out var want) || string.IsNullOrWhiteSpace(want.Text))
        {
            return Messages(context, "revealTruth", target.Id.Value, $"{target.Name} wants nothing the magic can name.");
        }

        var stakes = string.IsNullOrWhiteSpace(want.Stakes) ? "" : $" At stake: {want.Stakes}.";
        return Messages(context, "revealTruth", target.Id.Value, $"{target.Name} wants this: {want.Text}.{stakes}");
    }

    private static IReadOnlyList<StateDelta> RevealBond(EffectContext context, Entity target)
    {
        if (!target.TryGet<SoulComponent>(out var targetSoul)
            || !context.Caster.TryGet<SoulComponent>(out var casterSoul)
            || !context.Engine.State.Bonds.TryGet(targetSoul.SoulId, casterSoul.SoulId, out var bond))
        {
            return Messages(context, "revealTruth", target.Id.Value, $"{target.Name} holds no shaped feeling toward you yet.");
        }

        var feelings = new List<string>();
        if (bond.Loyalty != 0)
        {
            feelings.Add(bond.Loyalty > 0 ? "loyalty" : "faithlessness");
        }

        if (bond.Fear > 0)
        {
            feelings.Add("fear");
        }

        if (bond.Admiration > 0)
        {
            feelings.Add("admiration");
        }

        if (bond.Resentment > 0)
        {
            feelings.Add("resentment");
        }

        var feelingText = feelings.Count == 0 ? "nothing strong" : string.Join(", ", feelings);
        return Messages(
            context,
            "revealTruth",
            target.Id.Value,
            $"Toward you, {target.Name} carries {feelingText}; their posture is {bond.Posture}.");
    }

    private static IReadOnlyList<StateDelta> RevealPromises(EffectContext context, Entity target)
    {
        var matches = context.Engine.State.PromiseLedger.Promises
            .Where(promise => promise.Status is "bound" or "unbound"
                && ((promise.BoundTargetId is { } bound && bound.Equals(target.Id.Value, StringComparison.OrdinalIgnoreCase))
                    || promise.Subject.Contains(target.Name, StringComparison.OrdinalIgnoreCase)
                    || promise.Text.Contains(target.Name, StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .ToArray();
        if (matches.Length == 0)
        {
            return Messages(context, "revealTruth", target.Id.Value, $"No promise in the world's ledger touches {target.Name}.");
        }

        return matches
            .SelectMany(promise => Messages(
                context,
                "revealTruth",
                target.Id.Value,
                $"A {promise.Kind} clings to {target.Name}: {promise.Text}"))
            .ToArray();
    }

    private static IReadOnlyList<StateDelta> RevealWhereabouts(EffectContext context, SpellEffect effect)
    {
        var subject = Text(effect, "subject", Text(effect, "name", "")).Trim();
        var from = context.Caster.Get<PositionComponent>().Position;
        var match = context.Engine.State.Entities.Values
            .Where(entity => entity.Id != context.Caster.Id
                && entity.Has<PositionComponent>()
                && entity.Name.Contains(subject, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => GameEngineDistance(entity.Get<PositionComponent>().Position, from))
            .FirstOrDefault();
        if (match is null)
        {
            return Messages(context, "revealTruth", "", $"The divination sweeps the land and finds no '{subject}' within its reach.");
        }

        var position = match.Get<PositionComponent>().Position;
        var distance = GameEngineDistance(position, from);
        return distance == 0
            ? Messages(context, "revealTruth", match.Id.Value, $"{match.Name} stands where you stand.")
            : Messages(
                context,
                "revealTruth",
                match.Id.Value,
                $"{match.Name} is {distance} tiles {Compass(from, position)} of you.");
    }

    private static IReadOnlyList<StateDelta> RevealThreats(EffectContext context)
    {
        var from = context.Caster.Get<PositionComponent>().Position;
        var threat = context.Engine.State.Entities.Values
            .Where(entity => entity.Id != context.Caster.Id
                && entity.Has<PositionComponent>()
                && entity.TryGet<ActorComponent>(out var actor)
                && actor.Alive
                && context.Engine.IsHostile(entity, context.Caster))
            .OrderBy(entity => GameEngineDistance(entity.Get<PositionComponent>().Position, from))
            .FirstOrDefault();
        if (threat is null)
        {
            return Messages(context, "revealTruth", "", "The divination finds no hostile will turned toward you here.");
        }

        var position = threat.Get<PositionComponent>().Position;
        return Messages(
            context,
            "revealTruth",
            threat.Id.Value,
            $"The nearest hostile will is {threat.Name}, {GameEngineDistance(position, from)} tiles {Compass(from, position)}.");
    }

    private static string HealthBand(ActorComponent actor)
    {
        var fraction = actor.MaxHitPoints <= 0 ? 0.0 : (double)actor.HitPoints / actor.MaxHitPoints;
        return fraction >= 0.999 ? "unharmed"
            : fraction >= 0.6 ? "lightly wounded"
            : fraction >= 0.3 ? "badly wounded"
            : "near death";
    }

    private static string Compass(GridPoint from, GridPoint to)
    {
        var ns = to.Y < from.Y ? "north" : to.Y > from.Y ? "south" : "";
        var ew = to.X < from.X ? "west" : to.X > from.X ? "east" : "";
        var direction = ns + ew;
        return direction.Length == 0 ? "away" : direction;
    }
}
