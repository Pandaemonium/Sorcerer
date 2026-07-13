using Sorcerer.Magic.Capabilities;

namespace Sorcerer.Llm;

/// <summary>
/// Capability router backed by a JSON chat client (OpenAI-compatible or native Anthropic). Same tiny contract as
/// <see cref="OllamaSpellRouter"/>: a short JSON reply naming the needed capabilities, with any
/// failure surfaced as a technical failure so the controller falls back to keyword routing.
/// </summary>
public sealed class OpenAiCompatibleSpellRouter : ISpellRouter
{
    private readonly IJsonChatClient _chat;
    private readonly string _name;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleSpellRouter(
        string endpoint,
        string model,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        string? apiKey = null)
        : this(
            new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey),
            "openai-compatible-router",
            timeout)
    {
    }

    internal OpenAiCompatibleSpellRouter(
        IJsonChatClient chat,
        string name,
        TimeSpan? timeout = null)
    {
        _chat = chat;
        _name = name;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name => _name;

    public async Task<SpellRouteResult> RouteAsync(
        string spellText,
        string capabilityIndex,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var result = await _chat.ChatAsync(
            SpellRouterPrompt.System(capabilityIndex),
            SpellRouterPrompt.User(spellText),
            temperature: 0.0,
            maxTokens: 128,
            timeout.Token,
            label: "wild-router");

        return result.Success
            ? SpellRouterPrompt.From(Name, result.RawText, result.Content)
            : SpellRouterPrompt.Failure(result.RawText, result.Error ?? $"{Name} failed.");
    }
}
