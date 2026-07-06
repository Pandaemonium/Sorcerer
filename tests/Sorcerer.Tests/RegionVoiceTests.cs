using System;
using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;
using Sorcerer.Magic.Operations;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 4 of the WildMagic import: regions carry a prose voice summary and deterministic ambient
/// texture (adapted from WildMagic's regions.py), and that voice is fed into the magic resolver
/// lens so the same operation can read differently by place.
/// </summary>
public sealed class RegionVoiceTests
{
    [Fact]
    public void PriorityRegionsHaveDistinctVoiceAndAmbientText()
    {
        var registry = RegionRegistry.CreateMinimal();
        var hollowmere = registry.Region("hollowmere_margin")!;
        var vigovia = registry.Region("imperial_encounter")!;
        var wild = registry.Region("wild_border")!;

        Assert.False(string.IsNullOrWhiteSpace(hollowmere.VoiceSummary));
        Assert.False(string.IsNullOrWhiteSpace(vigovia.VoiceSummary));
        Assert.NotEqual(hollowmere.VoiceSummary, vigovia.VoiceSummary);
        Assert.Contains("frontier", hollowmere.VoiceSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("marble", vigovia.VoiceSummary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(wild.AmbientLines ?? Array.Empty<string>());
    }

    [Fact]
    public void RegionVoiceReachesMagicResolverContext()
    {
        var session = GameSession.CreateImperialEncounter();

        var context = session.Engine.MagicContext(new OperationIndex(
            Array.Empty<string>(),
            Array.Empty<OperationCardView>()));
        var json = JsonSerializer.Serialize(context);

        Assert.Contains("region voice:", json, StringComparison.OrdinalIgnoreCase);
    }
}
