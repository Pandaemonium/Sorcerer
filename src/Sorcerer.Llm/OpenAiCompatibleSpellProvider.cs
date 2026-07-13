using System.Text.Json;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public sealed class OpenAiCompatibleSpellProvider : ISpellProvider
{
    private readonly IJsonChatClient _chat;
    private readonly string _name;
    private readonly OperationRegistry _registry;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleSpellProvider(
        string endpoint,
        string model,
        HttpClient? httpClient = null,
        OperationRegistry? registry = null,
        TimeSpan? timeout = null,
        string? apiKey = null)
        : this(
            new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey),
            "openai-compatible",
            registry,
            timeout)
    {
    }

    internal OpenAiCompatibleSpellProvider(
        IJsonChatClient chat,
        string name,
        OperationRegistry? registry = null,
        TimeSpan? timeout = null)
    {
        _chat = chat;
        _name = name;
        _registry = registry ?? OperationRegistry.CreateDefault();
        _timeout = timeout ?? TimeSpan.FromSeconds(240);
    }

    public string Name => _name;

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
            return Failure(first.RawText, first.Error ?? $"{Name} spell provider failed.", first.Stats);
        }

        if (SpellResolutionJson.TryReadNeedsCapability(first.Content) is { } requestedCapability)
        {
            return new SpellProviderResult(Name, first.Content, Resolution: null, TechnicalFailure: false, Error: null, Stats: first.Stats, RequestedCapability: requestedCapability);
        }

        try
        {
            return Success(first.Content, SpellResolutionJson.Parse(first.Content, _registry), first.Stats);
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
            return Failure(repair.RawText, repair.Error ?? $"{Name} spell repair failed.", repair.Stats);
        }

        try
        {
            return Success(repair.Content, SpellResolutionJson.Parse(repair.Content, _registry), repair.Stats);
        }
        catch (JsonException ex)
        {
            return Failure(repair.Content, $"{Name} returned invalid JSON, then repair failed: {ex.Message}", repair.Stats);
        }
    }

    private SpellProviderResult Success(
        string raw,
        SpellResolution resolution,
        Sorcerer.Core.Telemetry.ProviderCallStats? stats = null) =>
        new(
            Name,
            RawText: raw,
            Resolution: resolution,
            TechnicalFailure: false,
            Error: null,
            Stats: stats);

    private SpellProviderResult Failure(
        string raw,
        string error,
        Sorcerer.Core.Telemetry.ProviderCallStats? stats = null) =>
        new(
            Name,
            RawText: raw,
            Resolution: null,
            TechnicalFailure: true,
            Error: error,
            Stats: stats);
}
