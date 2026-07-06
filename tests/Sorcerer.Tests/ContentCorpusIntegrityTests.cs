using System;
using System.Linq;
using Sorcerer.Core.Lore;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 10 of the WildMagic import (content import order): imported content must have subject tags
/// and at least one consumer, never be purely decorative. This holds the whole lore corpus to that
/// bar — every card carries subjects, has consumable sections, and actually routes for its own
/// subject (a real consumer via LoreRouter).
/// </summary>
public sealed class ContentCorpusIntegrityTests
{
    [Fact]
    public void EveryImportedLoreCardHasSubjectsSectionsAndRoutes()
    {
        var catalog = LoreCatalog.LoadDefault();
        Assert.NotEmpty(catalog.Cards);

        foreach (var card in catalog.Cards)
        {
            Assert.False(card.Subjects.Count == 0, $"{card.Id} has no subject tags");
            Assert.False(card.Sections.Count == 0, $"{card.Id} has no consumable sections");

            var routed = LoreRouter.Select(catalog, new LoreQuery(
                new[] { card.Subjects[0] },
                Array.Empty<string>(),
                AccessLevel: 4,
                Limit: 40));
            Assert.Contains(routed, routedCard => routedCard.Id == card.Id);
        }
    }
}
