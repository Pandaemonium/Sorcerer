using Sorcerer.Core.Commands;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic;

public sealed class WildMagicController : IWildMagicController
{
    private readonly ISpellProvider _provider;
    private readonly OperationRegistry _registry;

    public WildMagicController(ISpellProvider provider, OperationRegistry? registry = null)
    {
        _provider = provider;
        _registry = registry ?? OperationRegistry.CreateDefault();
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
            return new ActionResult
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
        }

        var resolution = providerResult.Resolution;
        if (!resolution.Accepted)
        {
            var reason = resolution.RejectedReason ?? "The spell refuses to become real.";
            engine.AddMessage(reason);
            engine.AdvanceTurn();
            return new ActionResult
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
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        if (!string.IsNullOrWhiteSpace(resolution.OutcomeText))
        {
            engine.AddMessage(resolution.OutcomeText);
            messages.Add(resolution.OutcomeText);
        }

        foreach (var effect in resolution.Effects)
        {
            if (!_registry.Supports(effect.Type))
            {
                var error = $"Unsupported spell operation: {effect.Type}";
                engine.AddMessage(error);
                return TechnicalFailure(engine, providerResult.Provider, turnBefore, error);
            }

            deltas.AddRange(ApplyEffect(engine, effect));
        }

        if (messages.Count == 0 && deltas.Count == 0)
        {
            var message = "The spell answers with a small blue spark.";
            engine.AddMessage(message);
            messages.Add(message);
        }

        engine.AdvanceTurn();
        return new ActionResult
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
    }

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

            case "addStatus":
            case "push":
            case "pull":
            case "teleport":
            case "createTiles":
            case "summon":
            case "transformEntity":
            case "transformItem":
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
