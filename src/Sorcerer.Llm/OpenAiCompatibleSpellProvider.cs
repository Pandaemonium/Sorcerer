using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public sealed class OpenAiCompatibleSpellProvider : ISpellProvider
{
    private readonly string _endpoint;
    private readonly string _model;

    public OpenAiCompatibleSpellProvider(string endpoint, string model)
    {
        _endpoint = endpoint;
        _model = model;
    }

    public string Name => "openai-compatible";

    public Task<SpellProviderResult> ResolveAsync(
        SpellRequest request,
        CancellationToken cancellationToken)
    {
        var message = $"Provider {Name} is configured for {_endpoint} with model {_model}, but the API-key adapter is only stubbed.";
        return Task.FromResult(new SpellProviderResult(
            Name,
            RawText: "",
            Resolution: null,
            TechnicalFailure: true,
            Error: message));
    }
}
