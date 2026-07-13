using Sorcerer.Core.Telemetry;

namespace Sorcerer.Llm;

internal interface IJsonChatClient
{
    Task<JsonChatResult> ChatAsync(
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken,
        string label = "llm");
}

internal sealed record JsonChatResult(
    bool Success,
    string Content,
    string RawText,
    string? Error,
    ProviderCallStats? Stats = null);
