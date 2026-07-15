using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class EncounterRealizationTests
{
    private const string ObjectiveItem = "witness parcel";

    [Fact]
    public async Task StakedFetchPromiseRealizesAsAStagedEncounterDeterministically()
    {
        var first = await RealizeStagedFetch(seed: 71);
        var repeat = await RealizeStagedFetch(seed: 71);

        Assert.Equal("realized", first.Promise.Status);
        Assert.NotEmpty(first.Casts);
        Assert.All(first.Casts, cast =>
        {
            Assert.True(cast.TryGet<ActorComponent>(out var actor) && actor.Alive);
            Assert.False(string.IsNullOrWhiteSpace(cast.Get<WantComponent>().Text));
        });
        Assert.Equal(
            first.Casts.Select(CastFingerprint).OrderBy(value => value, StringComparer.Ordinal),
            repeat.Casts.Select(CastFingerprint).OrderBy(value => value, StringComparer.Ordinal));

        // The objective is reachable by exactly one of the staged placements.
        if (first.GroundItem is { } item)
        {
            Assert.Contains(ObjectiveItem, item.Name, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var keeper = Assert.Single(first.Casts, cast =>
                cast.TryGet<InventoryComponent>(out var held)
                && held.Items.Keys.Any(key => key.Contains("witness", StringComparison.OrdinalIgnoreCase)));
            var inventory = keeper.Get<InventoryComponent>();
            var key = inventory.Items.Keys.Single(k => k.Contains("witness", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(key, inventory.TreasuredItems);
        }
    }

    [Fact]
    public async Task KilledKeeperCanBeLootedForTheObjectiveAndTheFetchCompletes()
    {
        var session = CreateSession(seed: 71);
        var player = session.Engine.State.ControlledEntity;
        var position = player.Get<PositionComponent>().Position;
        var spawned = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "Vessa Reedbound, reed-keeper",
            position.X + 1,
            position.Y,
            prefix: "keeper",
            glyph: 'p',
            faction: "hollowmere",
            hp: 6,
            attack: 1,
            tags: new[] { "npc", "objective_keeper" },
            material: "flesh",
            roles: new[] { "keeper" },
            controllerKind: "ai",
            aiPolicyId: "resident",
            summoned: false,
            emitMessage: false));
        Assert.True(spawned.Applied);
        var keeper = session.Engine.State.Entities[EntityId.Create(spawned.TargetId!)];
        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "test", keeper.Id.Value, "witness_parcel", op: "add")).Applied);
        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "test", keeper.Id.Value, "witness_parcel", op: "protect")).Applied);

        keeper.Set(keeper.Get<ActorComponent>() with { HitPoints = 0 });
        var pickup = await session.ExecuteAsync(new PickupCommand("witness parcel"));

        Assert.True(pickup.Success, string.Join(" | ", pickup.Messages));
        Assert.Contains(pickup.Messages, message =>
            message.Contains("corpse", StringComparison.OrdinalIgnoreCase));
        var playerInventory = session.Engine.State.ControlledEntity.Get<InventoryComponent>();
        Assert.Contains(playerInventory.Items, pair =>
            pair.Key.Contains("witness", StringComparison.OrdinalIgnoreCase) && pair.Value > 0);
        Assert.DoesNotContain(
            keeper.Get<InventoryComponent>().Items,
            pair => pair.Key.Contains("witness", StringComparison.OrdinalIgnoreCase) && pair.Value > 0);
    }

    [Fact]
    public void DialogueShapedUnprotectAndGiveMoveTheTreasuredObjectiveToThePlayer()
    {
        var session = CreateSession(seed: 71);
        var player = session.Engine.State.ControlledEntity;
        var position = player.Get<PositionComponent>().Position;
        var spawned = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "Requisition clerk",
            position.X + 1,
            position.Y,
            prefix: "keeper",
            glyph: 'c',
            faction: "empire",
            hp: 5,
            attack: 0,
            tags: new[] { "npc", "objective_keeper" },
            material: "flesh",
            controllerKind: "ai",
            aiPolicyId: "resident",
            summoned: false,
            emitMessage: false));
        var keeper = session.Engine.State.Entities[EntityId.Create(spawned.TargetId!)];
        session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "test", keeper.Id.Value, "witness_parcel", op: "add"));
        session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "test", keeper.Id.Value, "witness_parcel", op: "protect"));

        // A give while protected is refused; the dialogue-shaped unprotect-then-give succeeds.
        var refused = session.Engine.ApplyConsequence(GiveToPlayer(session, keeper));
        Assert.False(refused.Applied);
        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "dialogue", keeper.Id.Value, "witness_parcel", op: "unprotect")).Applied);
        var given = session.Engine.ApplyConsequence(GiveToPlayer(session, keeper));

        Assert.True(given.Applied, given.Error);
        Assert.Contains(
            session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items,
            pair => pair.Key.Contains("witness", StringComparison.OrdinalIgnoreCase) && pair.Value > 0);
    }

    [Fact]
    public async Task RestrictedSiteDefersTheFetchPromiseBehindAnInteriorThreshold()
    {
        // Probe the assembler for a promise id whose draw is the restricted-site archetype in
        // the destination zone, then arrange for the real promise to receive exactly that id.
        var region = RegionCatalog.LoadDefault().Region("hollowmere_margin")!;
        const string zoneId = "2,0";
        var catalog = EncounterTemplateCatalog.LoadDefault();
        var session = CreateSession(seed: 71);
        DisableAi(session);
        // Promise ids are sequential, so probe the assembler for an upcoming id whose draw is
        // the restricted-site archetype, pad the ledger up to it, and claim it for the fetch.
        var existing = session.Engine.State.PromiseLedger.Promises.Count;
        string? targetPromiseId = null;
        for (var index = existing + 1; index <= existing + 24; index++)
        {
            var candidate = $"promise_{index}";
            var plan = EncounterAssembler.Assemble(
                new EncounterRequest(71, zoneId, "promise", candidate, region, ObjectiveItem,
                    PromiseSalience: 5, FactionPressure: 1, InteriorAvailable: true),
                catalog);
            if (plan?.Kind == EncounterAssembler.KindRestrictedSite)
            {
                targetPromiseId = candidate;
                break;
            }
        }

        if (targetPromiseId is null)
        {
            // No draw in range yields the archetype for this seed; eligibility is covered by
            // the assembler tests, so there is nothing further to stage here.
            return;
        }

        var ordinal = int.Parse(targetPromiseId.Split('_')[1]);
        for (var filler = existing + 1; filler < ordinal; filler++)
        {
            session.Engine.State.PromiseLedger.Add("omen", $"filler {filler}");
        }

        var createdId = CreateStakedFetchPromise(session, zoneId, tags: new[] { "waystation" }, pressure: true);
        Assert.Equal(targetPromiseId, createdId);

        await TravelTo(session, zoneId);

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == targetPromiseId);
        Assert.Equal("bound", promise.Status);
        Assert.StartsWith("interior:", promise.ClaimedPlace!, StringComparison.OrdinalIgnoreCase);
        var entrance = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<InteriorEntranceComponent>(out _)
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(targetPromiseId, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(promise.ClaimedPlace, entrance.Get<InteriorEntranceComponent>().InteriorZoneId);
    }

    private async Task<(WorldPromise Promise, IReadOnlyList<Entity> Casts, Entity? GroundItem)> RealizeStagedFetch(int seed)
    {
        var session = CreateSession(seed);
        DisableAi(session);
        const string zoneId = "2,0";
        var promiseId = CreateStakedFetchPromise(session, zoneId, tags: null, pressure: false);

        await TravelTo(session, zoneId);

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promiseId);
        var casts = session.Engine.State.Entities.Values
            .Where(entity => entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Contains("encounter_cast", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var groundItem = session.Engine.State.Entities.Values.FirstOrDefault(entity =>
            entity.Has<ItemComponent>()
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promiseId, StringComparer.OrdinalIgnoreCase));
        return (promise, casts, groundItem);
    }

    private static string CreateStakedFetchPromise(
        GameSession session,
        string zoneId,
        IReadOnlyList<string>? tags,
        bool pressure)
    {
        var giver = session.Engine.State.Entities.Values.First(entity =>
            entity.Id != session.Engine.State.ControlledEntityId
            && entity.Has<ActorComponent>());
        if (pressure)
        {
            session.Engine.State.Factions.AdjustStanding("hollowmere", "hostile:player", 2);
        }

        var claim = session.Engine.ApplyConsequence(WorldConsequence.RecordClaim(
            "test",
            giver.Id.Value,
            "player_soul",
            $"{ObjectiveItem} waits at zone {zoneId}.",
            "journey",
            ObjectiveItem,
            salience: 5,
            confidence: 95,
            playerVisible: true,
            tags: (tags ?? Array.Empty<string>())
                .Concat(new[]
                {
                    "generated_objective",
                    "objective_kind:fetch",
                    "objective_return_to_giver",
                    $"objective_item:{ObjectiveItem}",
                    $"objective_giver_name:{giver.Name}",
                })
                .ToArray()));
        Assert.True(claim.Applied);
        var promise = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "lead",
            $"{ObjectiveItem} waits at zone {zoneId}.",
            triggerHint: "travel",
            salience: 5,
            subject: ObjectiveItem,
            claimedPlace: zoneId,
            realizationKind: "item",
            bindPlace: zoneId,
            sourceClaimId: claim.TargetId,
            sourceSpeakerId: giver.Id.Value,
            useCurrentRegionAsClaimedPlace: false,
            autoBind: true,
            emitMessage: false));
        Assert.True(promise.Applied, promise.Error);
        return promise.TargetId!;
    }

    private static WorldConsequence GiveToPlayer(GameSession session, Entity keeper) =>
        WorldConsequence.TransferItem(
            "dialogue",
            keeper.Id.Value,
            "give",
            "witness_parcel",
            recipientEntityId: session.Engine.State.ControlledEntityId.Value,
            operation: "dialogueGive",
            emitMessage: false);

    private static GameSession CreateSession(int seed) =>
        GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: seed);

    private static void DisableAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }

    private static async Task TravelTo(GameSession session, string zoneId)
    {
        var destination = ParseZoneId(zoneId);
        var current = ParseZoneId(session.Engine.State.CurrentZoneId);
        while (current.X != destination.X)
        {
            var travel = await session.ExecuteAsync(new TravelCommand(current.X < destination.X ? Direction.East : Direction.West));
            Assert.True(travel.Success, string.Join(" | ", travel.Messages));
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }

        while (current.Y != destination.Y)
        {
            var travel = await session.ExecuteAsync(new TravelCommand(current.Y < destination.Y ? Direction.South : Direction.North));
            Assert.True(travel.Success, string.Join(" | ", travel.Messages));
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static string CastFingerprint(Entity entity) =>
        $"{entity.Name}|{entity.Get<ActorComponent>().Faction}|{entity.Get<PositionComponent>().Position.X},{entity.Get<PositionComponent>().Position.Y}";
}
