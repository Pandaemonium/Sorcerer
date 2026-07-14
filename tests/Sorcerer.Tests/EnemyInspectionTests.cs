using System;
using System.Collections.Generic;
using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Tactical-mastery pillar (Q13): inspection teaches how to fight an enemy -- its hitting power, its
/// guard, and the damage it folds under or shrugs off -- deterministic facts the player can act on to
/// offset wild magic's uncertainty.
/// </summary>
public sealed class EnemyInspectionTests
{
    [Fact]
    public void ExaminingAnEnemyTeachesItsCombatProfileAndWeaknesses()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var playerPos = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var soldier = engine.State.Entities.Values
            .First(e => e.Name.Contains("containment soldier", StringComparison.OrdinalIgnoreCase));

        // Bring it into examine reach and give it a legible weakness/resistance profile.
        soldier.Set(new PositionComponent(new GridPoint(playerPos.X + 1, playerPos.Y)));
        soldier.Set(new ResistanceComponent(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["physical"] = 40 },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["fire"] = 100 }));

        var result = engine.Examine(soldier.Name);

        Assert.True(result.Success);
        Assert.Contains(result.Messages, m => m.Contains("strikes for about", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, m => m.Contains("Weak to fire", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, m => m.Contains("Shrugs off physical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExaminingACharterSourceHintsThatReadingItTeaches()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var playerPos = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var notice = engine.State.Entities.Values
            .First(e => e.Name.Contains("containment notice", StringComparison.OrdinalIgnoreCase));

        // Bring the charter-bearing notice into examine reach; the curiosity hook should appear
        // instead of a bare "teaches_charter:*" tag.
        notice.Set(new PositionComponent(new GridPoint(playerPos.X + 1, playerPos.Y)));

        var result = engine.Examine(notice.Name);

        Assert.True(result.Success);
        Assert.Contains(result.Messages, m => m.Contains("teach you a charter form", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExaminingAHealingItemHintsItMendsWounds()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var playerPos = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var tincture = engine.State.Entities.Values
            .First(e => e.Name.Contains("tincture", StringComparison.OrdinalIgnoreCase));

        tincture.Set(new PositionComponent(new GridPoint(playerPos.X + 1, playerPos.Y)));

        var result = engine.Examine(tincture.Name);

        Assert.True(result.Success);
        Assert.Contains(result.Messages, m => m.Contains("mend wounds", StringComparison.OrdinalIgnoreCase));
    }
}
