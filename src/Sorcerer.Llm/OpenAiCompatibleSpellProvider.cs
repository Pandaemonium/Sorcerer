using System.Text.Json;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public sealed class OpenAiCompatibleSpellProvider : ISpellProvider
{
    private readonly OpenAiCompatibleChatClient _chat;
    private readonly OperationRegistry _registry;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleSpellProvider(
        string endpoint,
        string model,
        HttpClient? httpClient = null,
        OperationRegistry? registry = null,
        TimeSpan? timeout = null,
        string? apiKey = null)
    {
        _chat = new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey);
        _registry = registry ?? OperationRegistry.CreateDefault();
        _timeout = timeout ?? TimeSpan.FromSeconds(240);
    }

    public string Name => "openai-compatible";

    public async Task<SpellProviderResult> ResolveAsync(
        SpellRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var supported = string.Join(", ", request.SupportedOperations);
        var contextJson = JsonSerializer.Serialize(
            request.Context,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false,
            });
        var system = OllamaSpellProvider.BuildSystemPrompt(
            supported,
            request.CapabilityIndex,
            request.SelectedCapabilities);
        var user = $"Spell: {request.SpellText}\n\nCurrent magic context JSON:\n{contextJson}";

        var first = await _chat.ChatAsync(system, user, temperature: 0.2, maxTokens: 1200, timeout.Token);
        if (!first.Success)
        {
            return Failure(first.RawText, first.Error ?? "OpenAI-compatible spell provider failed.");
        }

        try
        {
            return Success(first.Content, SpellResolutionJson.Parse(first.Content, _registry));
        }
        catch (JsonException ex)
        {
            return await RetryAfterInvalidJsonAsync(
                request,
                supported,
                contextJson,
                first.Content,
                ex.Message,
                timeout.Token);
        }
    }

    private async Task<SpellProviderResult> RetryAfterInvalidJsonAsync(
        SpellRequest request,
        string supported,
        string contextJson,
        string invalidContent,
        string parseError,
        CancellationToken cancellationToken)
    {
        var repairSystem = OllamaSpellProvider.BuildSystemPrompt(
                supported,
                request.CapabilityIndex,
                request.SelectedCapabilities)
            + " This is a repair attempt after invalid output. Return JSON only; no prose before or after the object.";
        var previous = invalidContent.Length > 600
            ? invalidContent[..600]
            : invalidContent;
        var repairUser = "The previous resolver answer was not valid engine JSON. "
            + $"Parse error: {parseError}\n"
            + $"Previous invalid answer:\n{previous}\n\n"
            + "Convert the same spell into the required JSON object using supported operations. "
            + "Each effect must be a flat object with a type field; rewrite keyed or nested effects into separate flat effect objects. "
            + "For hiding, cover, protection, disguise, or attention-shifting requests, prefer addStatus on the caster/target, createTile/createTiles near the caster, addTrait on an entity, or message when those operations fit. "
            + $"Spell: {request.SpellText}\n\nCurrent magic context JSON:\n{contextJson}";

        var repair = await _chat.ChatAsync(repairSystem, repairUser, temperature: 0.1, maxTokens: 1200, cancellationToken);
        if (!repair.Success)
        {
            return Failure(repair.RawText, repair.Error ?? "OpenAI-compatible spell repair failed.");
        }

        try
        {
            return Success(repair.Content, SpellResolutionJson.Parse(repair.Content, _registry));
        }
        catch (JsonException ex)
        {
            return Failure(repair.Content, $"OpenAI-compatible endpoint returned invalid JSON, then repair failed: {ex.Message}");
        }
    }

    private SpellProviderResult Success(string raw, SpellResolution resolution) =>
        new(
            Name,
            RawText: raw,
            Resolution: resolution,
            TechnicalFailure: false,
            Error: null);

    private SpellProviderResult Failure(string raw, string error) =>
        new(
            Name,
            RawText: raw,
            Resolution: null,
            TechnicalFailure: true,
            Error: error);
}
