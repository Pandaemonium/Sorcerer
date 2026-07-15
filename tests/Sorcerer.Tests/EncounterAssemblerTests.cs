using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class EncounterAssemblerTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(2, 0, 0, 0)]
    [InlineData(3, 0, 0, 1)]
    [InlineData(4, 0, 0, 1)]
    [InlineData(5, 0, 0, 2)]
    [InlineData(4, 50, 0, 2)]
    [InlineData(5, 50, 1, 3)]
    [InlineData(5, 90, 5, 3)]
    [InlineData(0, 90, 0, 1)]
    public void StakesTierScalesWithSalienceImperialPresenceAndPressure(
        int salience, int presence, int pressure, int expected)
    {
        Assert.Equal(expected, EncounterAssembler.StakesTier(salience, presence, pressure));
    }

    [Fact]
    public void TierZeroYieldsNoPlanSoLowStakesErrandsStaySimpleFinds()
    {
        var request = Request(salience: 1);

        Assert.Null(EncounterAssembler.Assemble(request, EncounterTemplateCatalog.CreateMinimal()));
    }

    [Fact]
    public void AssemblyIsDeterministicForTheSameRequest()
    {
        var request = Request(salience: 5);
        var catalog = EncounterTemplateCatalog.CreateMinimal();

        var first = EncounterAssembler.Assemble(request, catalog);
        var repeat = EncounterAssembler.Assemble(request, catalog);

        Assert.NotNull(first);
        Assert.Equal(Fingerprint(first!), Fingerprint(repeat!));
    }

    [Fact]
    public void DifferentDiscriminatorsProduceDifferentDraws()
    {
        var catalog = EncounterTemplateCatalog.CreateMinimal();
        var plans = Enumerable.Range(0, 10)
            .Select(index => EncounterAssembler.Assemble(
                Request(salience: 5) with { Discriminator = $"promise_{index}" },
                catalog))
            .Where(plan => plan is not null)
            .Select(plan => Fingerprint(plan!))
            .ToArray();

        Assert.True(plans.Distinct().Count() > 1);
    }

    [Fact]
    public void HigherTierMeansBiggerTougherCasts()
    {
        var catalog = new EncounterTemplateCatalog();
        catalog.Add(GuardArchetype());

        var low = EncounterAssembler.Assemble(Request(salience: 3), catalog)!;
        var high = EncounterAssembler.Assemble(
            Request(salience: 5) with { FactionPressure = 1 },
            catalog)!;

        Assert.Equal(1, low.Tier);
        Assert.Equal(3, high.Tier);
        Assert.True(high.Casts.Count > low.Casts.Count);
        Assert.True(high.Casts.Max(spec => spec.HitPoints) > low.Casts.Max(spec => spec.HitPoints));
    }

    [Fact]
    public void RestrictedSiteRequiresInteriorAvailability()
    {
        var catalog = new EncounterTemplateCatalog();
        catalog.Add(GuardArchetype() with
        {
            Id = "site_only",
            Kind = EncounterAssembler.KindRestrictedSite,
            RequiresInterior = true,
        });

        Assert.Null(EncounterAssembler.Assemble(
            Request(salience: 5) with { InteriorAvailable = false },
            catalog));
        Assert.NotNull(EncounterAssembler.Assemble(
            Request(salience: 5) with { InteriorAvailable = true },
            catalog));
    }

    [Fact]
    public void AmbientPurposeSkipsNonAmbientArchetypesAndExcludedKinds()
    {
        var catalog = new EncounterTemplateCatalog();
        catalog.Add(GuardArchetype() with { Id = "not_ambient", AmbientEligible = false });
        var ambientRequest = Request(salience: 5) with { Purpose = "ambient" };

        Assert.Null(EncounterAssembler.Assemble(ambientRequest, catalog));

        catalog.Add(GuardArchetype() with { Id = "ambient_ok", AmbientEligible = true });
        Assert.NotNull(EncounterAssembler.Assemble(ambientRequest, catalog));
        Assert.Null(EncounterAssembler.Assemble(
            ambientRequest with { ExcludedKinds = new[] { EncounterAssembler.KindGuardedCache } },
            catalog));
    }

    [Fact]
    public void FactionCastsAreGatedByImperialPresence()
    {
        var catalog = new EncounterTemplateCatalog();
        var archetype = GuardArchetype();
        catalog.Add(archetype with
        {
            Casts = new[]
            {
                archetype.Casts[0] with { FactionId = "empire", MinImperialPresence = 60 },
            },
        });

        Assert.Null(EncounterAssembler.Assemble(Request(salience: 5), catalog));
        var plan = EncounterAssembler.Assemble(
            Request(salience: 5) with { Region = ImperialRegion() },
            catalog);
        Assert.NotNull(plan);
        Assert.Equal("empire", plan!.FactionId);
    }

    [Fact]
    public void KeeperPlanMarksTheObjectiveHolder()
    {
        var catalog = new EncounterTemplateCatalog();
        var minimalKeeper = EncounterTemplateCatalog.CreateMinimal().Archetypes
            .First(item => item.Kind == EncounterAssembler.KindKeeper);
        catalog.Add(minimalKeeper);

        var plan = EncounterAssembler.Assemble(Request(salience: 5), catalog)!;

        Assert.Equal("keeper_inventory", plan.ObjectivePlacement);
        var keeper = Assert.Single(plan.Casts);
        Assert.True(keeper.HoldsObjective);
        Assert.Contains("witness parcel", keeper.WantText, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(keeper.WantStakes));
    }

    private static EncounterArchetypeDefinition GuardArchetype() =>
        new(
            "test_guarded_cache",
            EncounterAssembler.KindGuardedCache,
            MinTier: 1,
            MaxTier: 3,
            RequiresInterior: false,
            AmbientEligible: true,
            Formation: "ring",
            CanonPattern: "{item} under {faction} watch at {place}.",
            Weight: 1,
            Tags: new[] { "encounter" },
            Casts: new[]
            {
                new EncounterFactionCastDefinition(
                    "empire",
                    Weight: 1,
                    MinImperialPresence: 0,
                    Slots: new[]
                    {
                        new EncounterCastSlotDefinition(
                            "sentry",
                            "guard",
                            "waystation sentry",
                            'g',
                            "guard",
                            new[] { "objective_guard" },
                            new[] { "guard" },
                            "Keep {item} in custody.",
                            "Stands down for a stamped writ.",
                            MinHitPoints: 8,
                            MaxHitPoints: 8,
                            MinAttack: 1,
                            MaxAttack: 1,
                            CountByTier: new[] { 1, 2, 3 }),
                    }),
            });

    private static RegionDefinition Region() =>
        RegionCatalog.LoadDefault().Region("hollowmere_margin")!;

    private static RegionDefinition ImperialRegion() =>
        RegionCatalog.LoadDefault().Region("imperial_encounter")!;

    private static EncounterRequest Request(int salience) =>
        new(
            WorldSeed: 71,
            ZoneId: "3,0",
            Purpose: "promise",
            Discriminator: "promise_test",
            Region: Region(),
            ObjectiveName: "witness parcel",
            PromiseSalience: salience,
            FactionPressure: 0,
            InteriorAvailable: false);

    private static string Fingerprint(EncounterPlan plan) =>
        $"{plan.ArchetypeId}|{plan.Kind}|{plan.FactionId}|{plan.Tier}|{plan.ObjectivePlacement}|" +
        string.Join(";", plan.Casts.Select(spec =>
            $"{spec.Name}|{spec.HitPoints}|{spec.Attack}|{spec.Offset.X},{spec.Offset.Y}|{spec.HoldsObjective}"));
}
