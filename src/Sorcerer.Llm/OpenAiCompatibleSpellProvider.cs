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

        var system = SpellPromptBuilder.System(request);
        var user = SpellPromptBuilder.User(request);

        var first = await _chat.ChatAsync(system, user, temperature: 0.2, maxTokens: 1200, timeout.Token, label: "wild");
        if (!first.Success)
        {
            return Failure(first.RawText, first.Error ?? "OpenAI-compatible spell provider failed.");
        }

        if (SpellResolutionJson.TryReadNeedsCapability(first.Content) is { } requestedCapability)
        {
            return new SpellProviderResult(Name, first.Content, Resolution: null, TechnicalFailure: false, Error: null, Stats: null, RequestedCapability: requestedCapability);
        }

        try
        {
            return Success(first.Content, SpellResolutionJson.Parse(first.Content, _registry));
        }
        catch (JsonException ex)
        {
            return await RetryAfterInvalidJsonAsync(
                request,
                first.Content,
                ex.Message,
                timeout.Token);
        }
    }

    private async Task<SpellProviderResult> RetryAfterInvalidJsonAsync(
        SpellRequest request,
        string invalidContent,
        string parseError,
        CancellationToken cancellationToken)
    {
        var repairSystem = SpellPromptBuilder.RepairSystem();
        var repairUser = SpellPromptBuilder.RepairUser(request, invalidContent, parseError);

        var repair = await _chat.ChatAsync(repairSystem, repairUser, temperature: 0.1, maxTokens: 1200, cancellationToken, label: "wild-repair");
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
