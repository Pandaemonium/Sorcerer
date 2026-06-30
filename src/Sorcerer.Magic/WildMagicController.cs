using Sorcerer.Core.Commands;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Magic.Auditing;
using Sorcerer.Magic.Costs;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic;

public sealed class WildMagicController : IWildMagicController
{
    private readonly ISpellAuditSink _audit;
    private readonly ISpellProvider _provider;
    private readonly OperationRegistry _registry;
    private readonly SpellValidator _validator = new();

    public WildMagicController(
        ISpellProvider provider,
        OperationRegistry? registry = null,
        ISpellAuditSink? audit = null)
    {
        _provider = provider;
        _registry = registry ?? OperationRegistry.CreateDefault();
        _audit = audit ?? NullSpellAuditSink.Instance;
    }

    public async Task<ActionResult> CastAsync(
        GameEngine engine,
        CastCommand command,
        CancellationToken cancellationToken)
    {
        var turnBefore = engine.State.Turn;
        var request = new SpellRequest(
            command.Text,
            engine.View(),
            _registry.Operations.Select(op => op.Name).OrderBy(name => name).ToArray());

        var providerResult = await _provider.ResolveAsync(request, cancellationToken);
        if (providerResult.TechnicalFailure || providerResult.Resolution is null)
        {
            var error = providerResult.Error ?? "Spell provider failed.";
            engine.AddMessage(error);
            var result = new ActionResult
            {
                Action = "cast",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = engine.State.Turn,
                Messages = new[] { error },
                TechnicalFailure = true,
                Magic = new MagicResolutionRecord(
                    providerResult.Provider,
                    Accepted: false,
                    TechnicalFailure: true,
                    EffectTypes: Array.Empty<string>(),
                    Error: error),
            };
            Audit(providerResult, command, request.Context, result, Array.Empty<string>());
            return result;
        }

        var resolution = providerResult.Resolution;
        if (!resolution.Accepted)
        {
            var reason = resolution.RejectedReason ?? "The spell refuses to become real.";
            engine.AddMessage(reason);
            engine.AdvanceTurn();
            var result = new ActionResult
            {
                Action = "cast",
                Success = false,
                ConsumedTurn = true,
                TurnBefore = turnBefore,
                TurnAfter = engine.State.Turn,
                Messages = new[] { reason },
                Magic = new MagicResolutionRecord(
                    providerResult.Provider,
                    Accepted: false,
                    TechnicalFailure: false,
                    EffectTypes: Array.Empty<string>(),
                    Error: reason),
            };
            Audit(providerResult, command, request.Context, result, Array.Empty<string>());
            return result;
        }

        var validation = _validator.Validate(engine, resolution, _registry);
        if (!validation.IsValid)
        {
            var error = string.Join("; ", validation.Issues.Select(issue => issue.Message));
            var result = TechnicalFailure(engine, providerResult.Provider, turnBefore, error);
            Audit(providerResult, command, request.Context, result, validation.Issues.Select(issue => issue.Code).ToArray());
            return result;
        }

        var transaction = GameTransaction.Begin(engine.State);
        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        try
        {
            if (!string.IsNullOrWhiteSpace(resolution.OutcomeText))
            {
                engine.AddMessage(resolution.OutcomeText);
                messages.Add(resolution.OutcomeText);
            }

            foreach (var effect in resolution.Effects)
            {
                deltas.AddRange(ApplyEffect(engine, effect));
            }

            deltas.AddRange(SpellCostApplier.Apply(engine, resolution.Costs));

            if (messages.Count == 0 && deltas.Count == 0)
            {
                var message = "The spell answers with a small blue spark.";
                engine.AddMessage(message);
                messages.Add(message);
            }

            engine.AdvanceTurn();
            var stateReport = engine.ValidateState();
            if (!stateReport.IsValid)
            {
                var error = string.Join("; ", stateReport.Issues.Select(issue => issue.Message));
                transaction.Rollback();
                var failure = TechnicalFailure(engine, providerResult.Provider, turnBefore, error);
                Audit(providerResult, command, request.Context, failure, stateReport.Issues.Select(issue => issue.Code).ToArray());
                return failure;
            }

            transaction.Commit();
            var result = new ActionResult
            {
                Action = "cast",
                Success = true,
                ConsumedTurn = true,
                TurnBefore = turnBefore,
                TurnAfter = engine.State.Turn,
                Messages = messages.Concat(deltas.Select(delta => delta.Summary)).ToArray(),
                Deltas = deltas,
                Magic = new MagicResolutionRecord(
                    providerResult.Provider,
                    Accepted: true,
                    TechnicalFailure: false,
                    EffectTypes: resolution.Effects.Select(effect => effect.Type).ToArray(),
                    Error: null),
            };
            Audit(providerResult, command, request.Context, result, Array.Empty<string>());
            return result;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            var result = TechnicalFailure(engine, providerResult.Provider, turnBefore, ex.Message);
            Audit(providerResult, command, request.Context, result, new[] { "application_exception" });
            return result;
        }
    }

    private void Audit(
        SpellProviderResult providerResult,
        CastCommand command,
        object context,
        ActionResult result,
        IReadOnlyList<string> validationErrors) =>
        _audit.Record(new SpellAuditEntry(
            DateTimeOffset.UtcNow,
            providerResult.Provider,
            command.Text,
            context,
            providerResult.RawText,
            providerResult.Resolution,
            result,
            validationErrors));

    private static ActionResult TechnicalFailure(
        GameEngine engine,
        string provider,
        int turnBefore,
        string error) =>
        new()
        {
            Action = "cast",
            Success = false,
            ConsumedTurn = false,
            TurnBefore = turnBefore,
            TurnAfter = engine.State.Turn,
            Messages = new[] { error },
            TechnicalFailure = true,
            Magic = new MagicResolutionRecord(
                provider,
                Accepted: false,
                TechnicalFailure: true,
                EffectTypes: Array.Empty<string>(),
                Error: error),
        };

    private static IEnumerable<StateDelta> ApplyEffect(GameEngine engine, SpellEffect effect)
    {
        switch (effect.Type)
        {
            case "damage":
            {
                var target = engine.ResolveEntity(ReadString(effect, "target", "nearest_enemy"));
                if (target is null)
                {
                    yield break;
                }

                yield return engine.DamageEntity(
                    target,
                    ReadInt(effect, "amount", 4),
                    ReadString(effect, "damageType", ReadString(effect, "damage_type", "arcane")));
                yield break;
            }

            case "heal":
            {
                var target = engine.ResolveEntity(ReadString(effect, "target", "player"));
                if (target is null)
                {
                    yield break;
                }

                yield return engine.HealEntity(target, ReadInt(effect, "amount", 4));
                yield break;
            }

            case "restoreMana":
            {
                var target = engine.ResolveEntity(ReadString(effect, "target", "player"));
                if (target is null)
                {
                    yield break;
                }

                var actor = target.Get<ActorComponent>();
                var amount = ReadInt(effect, "amount", 4);
                var restored = Math.Max(0, Math.Min(amount, actor.MaxMana - actor.Mana));
                target.Set(actor with { Mana = actor.Mana + restored });
                var message = $"{target.Name} regains {restored} mana.";
                engine.AddMessage(message);
                yield return new StateDelta(
                    "restoreMana",
                    target.Id.Value,
                    message,
                    new Dictionary<string, object?> { ["amount"] = restored });
                yield break;
            }

            case "message":
            {
                var text = ReadString(effect, "text", string.Empty);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    engine.AddMessage(text);
                    yield return new StateDelta(
                        "message",
                        "",
                        text,
                        new Dictionary<string, object?>());
                }

                yield break;
            }

            case "createPromise":
            {
                var text = ReadString(effect, "text", string.Empty);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return engine.AddPromise(ReadString(effect, "kind", "omen"), text);
                }

                yield break;
            }

            case "addCurse":
            {
                var text = ReadString(effect, "description", ReadString(effect, "name", "Wild Debt"));
                yield return engine.AddPromise("debt", text);
                yield break;
            }

            case "scheduleEvent":
            {
                var turns = Math.Max(1, ReadInt(effect, "turns", 3));
                var eventType = ReadString(effect, "eventType", ReadString(effect, "event_type", "wild_magic"));
                var scheduled = engine.State.ScheduledEvents.Schedule(
                    engine.State.Turn + turns,
                    eventType,
                    null,
                    effect.Fields);
                var summary = $"Something is scheduled for turn {scheduled.DueTurn}: {eventType}.";
                engine.AddMessage(summary);
                yield return new StateDelta(
                    "scheduleEvent",
                    scheduled.Id,
                    summary,
                    effect.Fields);
                yield break;
            }

            case "addStatus":
            {
                var target = engine.ResolveEntity(ReadString(effect, "target", "nearest_enemy"));
                if (target is null)
                {
                    yield break;
                }

                var status = ReadString(effect, "status", "marked");
                var duration = ReadInt(effect, "duration", 3);
                var current = target.TryGet<StatusContainerComponent>(out var container)
                    ? container.Statuses.ToList()
                    : new List<StatusInstance>();
                current.Add(new StatusInstance(status, status, engine.State.Turn + duration));
                target.Set(new StatusContainerComponent(current));
                var summary = $"{target.Name} is {status}.";
                engine.AddMessage(summary);
                yield return new StateDelta(
                    "addStatus",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["status"] = status, ["duration"] = duration });
                yield break;
            }

            case "push":
            case "pull":
            case "teleport":
            case "areaDamage":
            case "createTile":
            case "createTiles":
            case "summon":
            case "createEntity":
            case "transformEntity":
            case "transformItem":
            case "removeStatus":
            case "changeFaction":
            case "addTrait":
            case "createTrigger":
            {
                var summary = $"{effect.Type} is registered but not implemented yet.";
                engine.AddMessage(summary);
                yield return new StateDelta(
                    effect.Type,
                    ReadString(effect, "target", ""),
                    summary,
                    effect.Fields);
                yield break;
            }
        }
    }

    private static string ReadString(SpellEffect effect, string key, string fallback) =>
        effect.Fields.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    private static int ReadInt(SpellEffect effect, string key, int fallback) =>
        effect.Fields.TryGetValue(key, out var value) && int.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : fallback;
}
