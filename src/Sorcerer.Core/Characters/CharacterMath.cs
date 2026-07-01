using Sorcerer.Core.Entities;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Characters;

public static class CharacterMath
{
    public static int MaxHitPointsFromVigor(int vigor) =>
        vigor switch
        {
            <= 0 => 8,
            1 => 10,
            2 => 16,
            3 => 20,
            4 => 24,
            _ => 24 + ((vigor - 4) * 4),
        };

    public static int AttackFromVigor(int vigor) =>
        vigor <= 1 ? 3 : Math.Max(1, vigor);

    public static int DefenseFromVigor(int vigor) =>
        vigor >= 4 ? 1 : 0;

    public static int MaxManaFromAttunement(int attunement) =>
        Math.Max(0, 6 + (Math.Max(0, attunement) * 2));

    public static ActorComponent CreateActor(BodyStatsComponent body, SoulRecord soul, string faction)
    {
        var maxHp = MaxHitPointsFromVigor(body.Vigor);
        return new ActorComponent(
            maxHp,
            maxHp,
            soul.Mana,
            soul.MaxMana,
            AttackFromVigor(body.Vigor),
            DefenseFromVigor(body.Vigor),
            faction);
    }

    public static void SyncActorFromBodyAndSoul(Entity entity, SoulRecord soul)
    {
        if (!entity.TryGet<ActorComponent>(out var actor)
            || !entity.TryGet<BodyStatsComponent>(out var body))
        {
            return;
        }

        var maxHp = MaxHitPointsFromVigor(body.Vigor);
        entity.Set(actor with
        {
            HitPoints = Math.Min(Math.Max(0, actor.HitPoints), maxHp),
            MaxHitPoints = maxHp,
            Mana = Math.Min(Math.Max(0, soul.Mana), soul.MaxMana),
            MaxMana = soul.MaxMana,
            Attack = AttackFromVigor(body.Vigor),
            Defense = DefenseFromVigor(body.Vigor),
        });
    }

    public static BodyStatsComponent InferBodyStats(ActorComponent actor)
    {
        var vigor = actor.MaxHitPoints switch
        {
            <= 8 => 0,
            <= 10 => 1,
            <= 16 => 2,
            <= 20 => 3,
            <= 24 => 4,
            _ => 5 + ((actor.MaxHitPoints - 28) / 4),
        };
        return new BodyStatsComponent(Math.Max(0, vigor));
    }

    public static SoulRecord SoulFromOrigin(string soulId, OriginDefinition origin)
    {
        var stats = new SoulStatsComponent(origin.SoulAttunement, origin.SoulComposure);
        var maxMana = MaxManaFromAttunement(stats.Attunement);
        return new SoulRecord(
            soulId,
            stats,
            maxMana,
            maxMana,
            origin.Id,
            origin.DisplayName,
            origin.Tradition,
            origin.MagicalSignature,
            origin.Backstory,
            origin.FactionFirstReactions);
    }

    public static SoulRecord EnsureSoulRecord(GameState state, Entity entity)
    {
        var soulId = entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;
        if (state.Souls.TryGet(soulId, out var existing))
        {
            return existing;
        }

        var actor = entity.TryGet<ActorComponent>(out var actorComponent)
            ? actorComponent
            : new ActorComponent(1, 1, 0, 0, 1, 0, "neutral");
        var profile = entity.TryGet<ProfileComponent>(out var profileComponent)
            ? profileComponent
            : new ProfileComponent(entity.Name, "");
        var stats = new SoulStatsComponent(
            actor.MaxMana <= 0 ? 0 : Math.Max(0, (actor.MaxMana - 6) / 2),
            3);
        var record = new SoulRecord(
            soulId,
            stats,
            actor.Mana,
            actor.MaxMana,
            profile.Origin,
            profile.Origin,
            "",
            profile.MagicalSignature,
            profile.Backstory,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        state.Souls.Set(record);
        return record;
    }

    public static void EnsureCharacterState(GameState state)
    {
        foreach (var entity in state.Entities.Values)
        {
            if (!entity.TryGet<ActorComponent>(out var actor))
            {
                continue;
            }

            if (!entity.Has<BodyStatsComponent>())
            {
                entity.Set(InferBodyStats(actor));
            }

            var soul = EnsureSoulRecord(state, entity);
            SyncActorFromBodyAndSoul(entity, soul);
        }
    }
}
