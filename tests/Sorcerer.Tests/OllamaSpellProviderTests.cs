using System.Net;
using System.Text;
using System.Text.Json;
using Sorcerer.Llm;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

public sealed class OllamaSpellProviderTests
{
    [Fact]
    public async Task InvalidJsonContentGetsOneRepairAttempt()
    {
        var handler = new QueueHttpHandler(
            ChatResponse("The"),
            ChatResponse(
                "{\"accepted\":true,\"severity\":\"minor\",\"outcomeText\":\"The reeds take your outline softly.\",\"effects\":[{\"type\":\"addStatus\",\"target\":\"player\",\"status\":\"river_concealed\",\"duration\":4}],\"costs\":[],\"rejectedReason\":null}"));
        var provider = new OllamaSpellProvider(
            httpClient: new HttpClient(handler),
            model: "test-model");
        var request = new SpellRequest(
            "ask the reed shrine to hide me under river-color",
            new
            {
                caster = new { id = "player" },
                visible = new[] { new { id = "zone_prop_1", name = "reed-wrapped memory shrine" } },
            },
            new[] { "addStatus", "message" });

        var result = await provider.ResolveAsync(request, CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.NotNull(result.Resolution);
        Assert.Equal("addStatus", result.Resolution!.Effects.Single().Type);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("\"num_ctx\":8192", handler.RequestBodies[0]);
        Assert.Contains("Use only supported types and exact target ids", handler.RequestBodies[0]);
        Assert.Contains("Previous invalid answer", handler.RequestBodies[1]);
        Assert.Contains("hiding, cover, protection", handler.RequestBodies[1]);
    }

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new { message = new { content } });

    private sealed class QueueHttpHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public QueueHttpHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var body = _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
