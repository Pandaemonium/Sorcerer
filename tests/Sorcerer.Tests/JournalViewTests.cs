using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Primitives;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class JournalViewTests
{
    [Fact]
    public async Task StructuredJournalCoversEveryVisiblePromiseWithStableIds()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        // Travel to accrue a bound objective (the Free Folk warning handoff etc.).
        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var view = session.View();
        var journal = view.StructuredJournal;
        Assert.NotNull(journal);

        var sectioned = journal!.Objectives
            .Concat(journal.Promises)
            .Concat(journal.Threads)
            .ToArray();
        var sectionedIds = sectioned.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var visiblePromiseIds = session.Engine.State.PromiseLedger.Promises
            .Where(p => p.PlayerVisible)
            .Select(p => p.Id)
            .ToArray();

        // Every visible promise is somewhere in the structured journal (the flat-journal truth,
        // preserved), each with its stable id.
        Assert.All(visiblePromiseIds, id => Assert.Contains(id, sectionedIds));
        // Entries carry provenance where known.
        Assert.All(sectioned, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Text)));
    }

    [Fact]
    public async Task ApproachingPressureSurfacesWithDeadlines()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        // Do something witnessed to accrue imperial pressure, then read the journal view.
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        for (var i = 0; i < 3; i++)
        {
            await session.ExecuteAsync(new WaitCommand());
        }

        var pressures = session.View().StructuredJournal!.Pressures;
        // If any imperial pressure is scheduled, each entry has a positive due turn and a message.
        Assert.All(pressures, p =>
        {
            Assert.True(p.DueTurn > 0);
            Assert.False(string.IsNullOrWhiteSpace(p.Text));
            Assert.StartsWith("empire_", p.Kind, StringComparison.OrdinalIgnoreCase);
        });
    }
}
