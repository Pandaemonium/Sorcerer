using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.Entities;

public sealed class Entity
{
    private readonly Dictionary<Type, IEntityComponent> _components = new();

    public Entity(EntityId id, string name)
    {
        Id = id;
        Name = name;
    }

    public EntityId Id { get; }

    public string Name { get; set; }

    public IReadOnlyCollection<IEntityComponent> Components => _components.Values;

    public Entity Set<TComponent>(TComponent component)
        where TComponent : IEntityComponent
    {
        _components[typeof(TComponent)] = component;
        return this;
    }

    public bool Has<TComponent>()
        where TComponent : IEntityComponent =>
        _components.ContainsKey(typeof(TComponent));

    public bool TryGet<TComponent>(out TComponent component)
        where TComponent : IEntityComponent
    {
        if (_components.TryGetValue(typeof(TComponent), out var value)
            && value is TComponent typed)
        {
            component = typed;
            return true;
        }

        component = default!;
        return false;
    }

    public TComponent Get<TComponent>()
        where TComponent : IEntityComponent
    {
        if (TryGet<TComponent>(out var component))
        {
            return component;
        }

        throw new InvalidOperationException(
            $"Entity {Id} does not have component {typeof(TComponent).Name}.");
    }
}

