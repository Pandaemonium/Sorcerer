using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class FreeFolkContentTests
{
    [Fact]
    public void HollowmereCarriesAVulnerableFreeFolkNetworkAndAProEmpireRefuser()
    {
        var region = RegionCatalog.LoadDefault().Region("hollowmere_margin")!;
        var archetypes = region.Population!.Archetypes;
        var ids = archetypes.Select(a => a.Id).ToArray();

        Assert.Contains("freefolk_lookout", ids);
        Assert.Contains("freefolk_shelterwright", ids);
        Assert.Contains("freefolk_kindled", ids); // the one disputatious radical
        Assert.Contains("hollowmere_loyalist", ids);

        // The watching cell disagrees about using the sorcerer: a cautious lookout, a bold runner.
        var lookout = archetypes.Single(a => a.Id == "freefolk_lookout");
        var runner = archetypes.Single(a => a.Id == "freefolk_runner");
        Assert.Contains(lookout.Tags, t => t.Equals("cautious", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(runner.Tags, t => t.Equals("bold", StringComparison.OrdinalIgnoreCase));

        // The safe-haven provider offers a real recovery service.
        var shelterwright = archetypes.Single(a => a.Id == "freefolk_shelterwright");
        Assert.Contains(shelterwright.Services, s => s.EffectKind.Equals("rest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SafeHavenRestServiceActuallyHeals()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var player = session.Engine.State.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;
        var actor = player.Get<ActorComponent>();
        player.Set(actor with { HitPoints = 4 });

        var host = new Entity(EntityId.Create("shelterwright_1"), "N-of-Reeds, shelterwright");
        host.Set(new PositionComponent(new GridPoint(origin.X + 1, origin.Y)));
        host.Set(new ActorComponent(8, 8, 0, 0, 0, 0, "free_folk"));
        host.Set(new TagsComponent(new[] { "free_folk", "refuge", "resident" }));
        host.Set(new ServiceComponent(new[]
        {
            new ServiceOffer("safe_rest", "shelter and rest", "A hidden floor to recover on.", "rest"),
        }));
        session.Engine.State.Entities[host.Id] = host;

        var result = await session.ExecuteAsync(new RequestServiceCommand("shelter and rest"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.True(session.Engine.State.ControlledEntity.Get<ActorComponent>().HitPoints > 4,
            "the safe-haven rest service should heal the sorcerer");
    }
}
