using System;
using System.Linq;
using Sorcerer.Core.Characters;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Magic;
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

    [Fact]
    public void OriginRosterIsCompleteAndCreationReady()
    {
        var catalog = OriginCatalog.LoadDefault();
        var spellbook = CharterSpellbook.Default;

        Assert.True(catalog.Origins.Count >= 8, $"expected at least 8 origins, found {catalog.Origins.Count}");

        foreach (var origin in catalog.Origins)
        {
            Assert.InRange(origin.BodyVigor, 2, 5);
            Assert.InRange(origin.SoulAttunement, 2, 5);
            Assert.InRange(origin.SoulComposure, 2, 5);
            Assert.False(string.IsNullOrWhiteSpace(origin.DisplayName), $"{origin.Id} has no display name");
            Assert.False(string.IsNullOrWhiteSpace(origin.Tradition), $"{origin.Id} has no tradition");
            Assert.False(string.IsNullOrWhiteSpace(origin.PublicName), $"{origin.Id} has no public name");
            Assert.False(string.IsNullOrWhiteSpace(origin.Appearance), $"{origin.Id} has no appearance");
            Assert.False(string.IsNullOrWhiteSpace(origin.MagicalSignature), $"{origin.Id} has no signature");
            Assert.False(string.IsNullOrWhiteSpace(origin.Backstory), $"{origin.Id} has no backstory");
            Assert.NotEmpty(origin.StartingItems);

            foreach (var spellId in origin.StartingCharterSpells ?? Array.Empty<string>())
            {
                Assert.True(
                    spellbook.Find(spellId) is not null,
                    $"{origin.Id} seeds unknown charter spell '{spellId}'");
            }
        }
    }
}
