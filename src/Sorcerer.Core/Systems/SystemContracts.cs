using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;

namespace Sorcerer.Core.Systems;

public sealed record SystemResult(
    bool Success,
    IReadOnlyList<string> Messages,
    IReadOnlyList<StateDelta> Deltas)
{
    public static SystemResult Pending(string systemName) =>
        new(false, new[] { $"{systemName} is stubbed but not implemented yet." }, Array.Empty<StateDelta>());
}

public sealed record MovementIntent(string ActorId, Direction Direction);

public sealed record CombatIntent(string AttackerId, string DefenderId, string Method);

public sealed record InteractionIntent(string ActorId, string TargetId, string Verb);

public sealed record AiIntent(string ActorId, string PolicyId);

public sealed record BodySwapRequest(
    string PlayerSoulId,
    string FromBodyId,
    string ToBodyId,
    bool SoulSwap);

public sealed record WorldTickContext(int Turn, string RegionId, int Seed);

public interface IGameSystem<in TIntent>
{
    string Name { get; }

    SystemResult Preview(TIntent intent);
}

public sealed class MovementSystem : IGameSystem<MovementIntent>
{
    public string Name => "movement";

    public SystemResult Preview(MovementIntent intent) => SystemResult.Pending(Name);
}

public sealed class CombatSystem : IGameSystem<CombatIntent>
{
    public string Name => "combat";

    public SystemResult Preview(CombatIntent intent) => SystemResult.Pending(Name);
}

public sealed class InteractionSystem : IGameSystem<InteractionIntent>
{
    public string Name => "interaction";

    public SystemResult Preview(InteractionIntent intent) => SystemResult.Pending(Name);
}

public sealed class AiTurnSystem : IGameSystem<AiIntent>
{
    public string Name => "ai_turn";

    public SystemResult Preview(AiIntent intent) => SystemResult.Pending(Name);
}

public sealed class BodySwapSystem : IGameSystem<BodySwapRequest>
{
    public string Name => "body_swap";

    public SystemResult Preview(BodySwapRequest intent) => SystemResult.Pending(Name);
}

public sealed class WorldTickSystem : IGameSystem<WorldTickContext>
{
    public string Name => "world_tick";

    public SystemResult Preview(WorldTickContext intent) => SystemResult.Pending(Name);
}
