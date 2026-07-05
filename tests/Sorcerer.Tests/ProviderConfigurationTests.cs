using Sorcerer.Core.Runtime;
using Sorcerer.Llm;
using Sorcerer.Llm.Configuration;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ProviderConfigurationTests
{
    [Fact]
    public void PurposeOverrideKeepsOtherPurposeSettings()
    {
        var configuration = new LlmConfiguration(new Dictionary<LlmPurpose, LlmPurposeSettings>
        {
            [LlmPurpose.Wild] = new("mock", null, null, 30),
            [LlmPurpose.Background] = new("mock", "http://background", "small-model", 60, Enabled: false),
        });

        var updated = configuration.WithPurposeOverride(
            LlmPurpose.Wild,
            provider: "ollama",
            host: "http://wild",
            model: "wild-model",
            apiKey: "wild-key");

        Assert.Equal("ollama", updated.SettingsFor(LlmPurpose.Wild).Provider);
        Assert.Equal("http://wild", updated.SettingsFor(LlmPurpose.Wild).Host);
        Assert.Equal("wild-model", updated.SettingsFor(LlmPurpose.Wild).Model);
        Assert.Equal("wild-key", updated.SettingsFor(LlmPurpose.Wild).ApiKey);
        Assert.False(updated.SettingsFor(LlmPurpose.Background).Enabled);
        Assert.Equal("small-model", updated.SettingsFor(LlmPurpose.Background).Model);
    }

    [Fact]
    public void ProviderFactoryUsesPurposeSettingsAndDisabledPurposesFallBackToMock()
    {
        var live = SpellProviderFactory.Create(new LlmPurposeSettings(
            "ollama",
            "http://127.0.0.1:11434",
            "test-model",
            30));
        var disabled = SpellProviderFactory.Create(new LlmPurposeSettings(
            "ollama",
            "http://127.0.0.1:11434",
            "test-model",
            30,
            Enabled: false));

        Assert.Equal("ollama", live.Name);
        Assert.Equal("mock", disabled.Name);
    }

    [Fact]
    public void BackgroundTextGeneratorFactoryHonorsPurposeEnabledFlag()
    {
        var live = BackgroundTextGeneratorFactory.Create(new LlmPurposeSettings(
            "mock",
            null,
            null,
            30,
            Enabled: true));
        var disabled = BackgroundTextGeneratorFactory.Create(new LlmPurposeSettings(
            "mock",
            null,
            null,
            30,
            Enabled: false));

        Assert.NotNull(live);
        Assert.Equal("mock-background", live!.Name);
        Assert.Null(disabled);
    }

    [Fact]
    public void DialogueParserPurposeDefaultsToCpuOllamaAndReadsUnderscoredEnvironment()
    {
        var previousProvider = Environment.GetEnvironmentVariable("SORCERER_PROVIDER");
        var previousParserProvider = Environment.GetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_PROVIDER");
        var previousGpu = Environment.GetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_NUM_GPU");
        try
        {
            Environment.SetEnvironmentVariable("SORCERER_PROVIDER", "ollama");
            Environment.SetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_PROVIDER", null);
            Environment.SetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_NUM_GPU", "2");

            var settings = LlmConfiguration.FromEnvironment().SettingsFor(LlmPurpose.DialogueParser);

            Assert.Equal("ollama", settings.Provider);
            Assert.Equal(2, settings.OllamaNumGpu);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SORCERER_PROVIDER", previousProvider);
            Environment.SetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_PROVIDER", previousParserProvider);
            Environment.SetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_NUM_GPU", previousGpu);
        }
    }

    [Fact]
    public void DialogueParserRouterPurposeDefaultsToCpuOllamaAndReadsUnderscoredEnvironment()
    {
        var previousProvider = Environment.GetEnvironmentVariable("SORCERER_PROVIDER");
        var previousParserRouterProvider = Environment.GetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_ROUTER_PROVIDER");
        var previousGpu = Environment.GetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_ROUTER_NUM_GPU");
        try
        {
            Environment.SetEnvironmentVariable("SORCERER_PROVIDER", "ollama");
            Environment.SetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_ROUTER_PROVIDER", null);
            Environment.SetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_ROUTER_NUM_GPU", "1");

            var settings = LlmConfiguration.FromEnvironment().SettingsFor(LlmPurpose.DialogueParserRouter);

            Assert.Equal("ollama", settings.Provider);
            Assert.Equal(1, settings.OllamaNumGpu);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SORCERER_PROVIDER", previousProvider);
            Environment.SetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_ROUTER_PROVIDER", previousParserRouterProvider);
            Environment.SetEnvironmentVariable("SORCERER_DIALOGUE_PARSER_ROUTER_NUM_GPU", previousGpu);
        }
    }

    [Fact]
    public void BackgroundTextGeneratorWritesAuditRecords()
    {
        var audit = new CapturingBackgroundTextAuditSink();
        var generator = BackgroundTextGeneratorFactory.Create(
            new LlmPurposeSettings("mock", null, null, 30, Enabled: true),
            audit)!;
        var request = new BackgroundTextRequest(
            "job_test",
            "entity_detail",
            "brazier_1",
            Priority: 2,
            Turn: 4,
            RegionId: "hollowmere_margin",
            TargetKind: "entity",
            TargetName: "brass brazier",
            TargetMaterial: "brass",
            TargetTags: new[] { "fire", "hollowmere" });

        var result = generator.Generate(request);
        var entry = Assert.Single(audit.Entries);

        Assert.False(result.TechnicalFailure);
        Assert.Equal("mock-background", entry.Provider);
        Assert.Equal("mock", entry.Model);
        Assert.Equal(request, entry.Request);
        Assert.Equal(result.Text, entry.ParsedText);
        Assert.False(entry.TechnicalFailure);
    }

    private sealed class CapturingBackgroundTextAuditSink : IBackgroundTextAuditSink
    {
        private readonly List<BackgroundTextAuditEntry> _entries = new();

        public IReadOnlyList<BackgroundTextAuditEntry> Entries => _entries;

        public void Record(BackgroundTextAuditEntry entry) => _entries.Add(entry);
    }
}
