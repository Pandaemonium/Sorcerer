using Sorcerer.Core.Entities;

namespace Sorcerer.Core.Views;

public static class ContextEntityRouting
{
    private static readonly string[] HookTags =
    {
        "context_hook",
        "magic_anchor",
        "promise_anchor",
        "claim_source",
        "readable",
        "service",
        "merchant",
        "quest",
        "named_creation",
        "teaches_charter",
    };

    public static bool IsActor(Entity entity) => entity.Has<ActorComponent>();

    public static bool IsHookBearing(Entity entity)
    {
        if (entity.Has<ItemComponent>()
            || entity.Has<DoorComponent>()
            || entity.Has<ReadableComponent>()
            || entity.Has<ClaimSourceComponent>()
            || entity.Has<PromiseAnchorComponent>()
            || entity.Has<MerchantComponent>()
            || entity.Has<ServiceComponent>()
            || entity.Has<InteractableComponent>())
        {
            return true;
        }

        if (entity.TryGet<FixtureComponent>(out var fixture) && fixture.CanAnchorMagic)
        {
            return true;
        }

        return entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => HookTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    public static bool IsCompactScenery(Entity entity) =>
        entity.Has<FixtureComponent>()
        && !IsActor(entity)
        && !IsHookBearing(entity);
}
