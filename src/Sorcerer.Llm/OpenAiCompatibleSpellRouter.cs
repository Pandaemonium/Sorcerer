using Sorcerer.Magic.Capabilities;

namespace Sorcerer.Llm;

/// <summary>
/// Capability router backed by an OpenAI-compatible chat endpoint. Same tiny contract as
/// <see cref="OllamaSpellRouter"/>: a short JSON reply naming the needed capabilities, with any
/// failure surfaced as a technical failure so the controller falls back to keyword routing.
/// </summary>
public sealed class OpenAiCompatibleSpellRouter : ISpellRouter
{
    private readonly OpenAiCompatibleChatClient _chat;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleSpellRouter(
        string endpoint,
        string model,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        string? apiKey = null)
    {
        _chat = new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey);
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name => "openai-compatible-router";

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
            timeout.Token);

        return result.Success
            ? SpellRouterPrompt.From(Name, result.RawText, result.Content)
            : SpellRouterPrompt.Failure(result.RawText, result.Error ?? "OpenAI-compatible router failed.");
    }
}
