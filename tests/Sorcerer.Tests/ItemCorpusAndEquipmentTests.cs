using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Primitives;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ItemCorpusAndEquipmentTests
{
    [Fact]
    public void DefaultCatalogLoadsTheLargeDataAuthoredCorpus()
    {
        var catalog = ItemCatalog.LoadDefault();

        Assert.True(catalog.Items.Count >= 72, $"expected the shipping floor of 72 items, found {catalog.Items.Count}");
        var components = catalog.Items.Count(item => item.Tags.Contains("reagent") || item.Kind is "component");
        var equipment = catalog.Items.Count(item => !string.IsNullOrWhiteSpace(item.EquipmentSlot));
        Assert.True(components >= 24, $"expected >=24 components, found {components}");
        Assert.True(equipment >= 20, $"expected >=20 equipment/foci, found {equipment}");

        // Cultural items from region packs and their region weighting resolved.
        Assert.NotNull(catalog.Find("eel_oil"));
        Assert.NotNull(catalog.Find("whalebone_corslet"));
        Assert.True(catalog.Find("censor_wax")!.RegionWeight("imperial_encounter") > 0);
        Assert.Equal(0, catalog.Find("censor_wax")!.RegionWeight("brall_whaleholds"));
    }

    [Fact]
    public void EmbeddedAndLooseCatalogsAgreeOnItemIds()
    {
        var loose = ItemCatalog.LoadDefault();
        var embedded = ItemCatalog.LoadEmbedded();

        var looseIds = loose.Items.Select(item => item.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        var embeddedIds = embedded.Items.Select(item => item.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(looseIds, embeddedIds);
    }

    [Fact]
    public void EquipmentEffectAggregatesModifiersWithoutMutatingBaseStats()
    {
        var catalog = ItemCatalog.LoadDefault();
        var entity = new Entity(EntityId.Create("subject"), "subject");
        entity.Set(new ActorComponent(20, 20, 10, 10, Attack: 3, Defense: 1, Faction: "neutral"));
        entity.Set(new EquipmentComponent(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hand"] = "oak_cudgel",
                ["body"] = "worn_leather_jerkin",
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        var effect = EquipmentEffectService.Recompute(entity, catalog);

        Assert.Equal(2, effect.Attack);
        Assert.Equal(2, effect.Defense);
        // Base stats are untouched; the effect is a separate derived component.
        Assert.Equal(3, entity.Get<ActorComponent>().Attack);
        Assert.Equal(1, entity.Get<ActorComponent>().Defense);
        Assert.True(entity.Has<EquipmentEffectComponent>());
    }

    [Fact]
    public void FocusBiasOnlyCountsWhenTheItemIsFocused()
    {
        var catalog = ItemCatalog.LoadDefault();
        var entity = new Entity(EntityId.Create("mage"), "mage");
        entity.Set(new EquipmentComponent(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["hand"] = "charter_chalk" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        var unfocused = EquipmentEffectService.Compute(entity, catalog);
        Assert.Empty(unfocused.FocusBias);

        entity.Set(new EquipmentComponent(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["hand"] = "charter_chalk" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hand" }));
        var focused = EquipmentEffectService.Compute(entity, catalog);
        Assert.NotEmpty(focused.FocusBias);
    }

    [Fact]
    public void EquippedArmourReducesIncomingMeleeDamage()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var engine = session.Engine;
        var player = engine.State.ControlledEntity;
        var baseline = player.Get<ActorComponent>();

        // Bare hit.
        var damage = WorldConsequence.Damage("test", player.Id.Value, 10, "physical", operation: "testDamage");
        engine.ApplyConsequence(damage);
        var afterBare = player.Get<ActorComponent>().HitPoints;
        var bareLoss = baseline.HitPoints - afterBare;

        // Heal back, equip armour, hit again.
        player.Set(player.Get<ActorComponent>() with { HitPoints = baseline.HitPoints });
        player.Set(new EquipmentComponent(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["body"] = "marble_ward_plate" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        EquipmentEffectService.Recompute(player, ItemCatalog.LoadDefault());
        engine.ApplyConsequence(WorldConsequence.Damage("test", player.Id.Value, 10, "physical", operation: "testDamage"));
        var armouredLoss = baseline.HitPoints - player.Get<ActorComponent>().HitPoints;

        Assert.True(armouredLoss < bareLoss, $"armour should reduce damage: bare {bareLoss}, armoured {armouredLoss}");
    }
}
