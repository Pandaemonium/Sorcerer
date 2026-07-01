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
            model: "wild-model");

        Assert.Equal("ollama", updated.SettingsFor(LlmPurpose.Wild).Provider);
        Assert.Equal("http://wild", updated.SettingsFor(LlmPurpose.Wild).Host);
        Assert.Equal("wild-model", updated.SettingsFor(LlmPurpose.Wild).Model);
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
}
