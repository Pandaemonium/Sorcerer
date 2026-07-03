using System.Net;
using System.Text;
using System.Text.Json;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task SpellProviderPostsChatCompletionsAndParsesResolution()
    {
        var content = """
            {"accepted":true,"severity":"minor","outcomeText":"The brass lock softens.","effects":[{"type":"message","text":"The brass lock softens."}],"costs":[],"rejectedReason":null}
            """;
        var handler = new QueueHttpHandler(ChatResponse(content));
        var provider = new OpenAiCompatibleSpellProvider(
            "http://example.test/v1",
            "test-model",
            httpClient: new HttpClient(handler),
            apiKey: "test-key");
        var request = new SpellRequest(
            "ask the lock to remember rain",
            new { caster = new { id = "player" } },
            new[] { "message" });

        var result = await provider.ResolveAsync(request, CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.Equal("openai-compatible", result.Provider);
        Assert.Equal("message", Assert.Single(result.Resolution!.Effects).Type);
        Assert.Single(handler.Requests);
        Assert.Equal("http://example.test/v1/chat/completions", handler.Requests[0].Uri);
        Assert.Equal("Bearer", handler.Requests[0].AuthorizationScheme);
        Assert.Equal("test-key", handler.Requests[0].AuthorizationParameter);
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", handler.RequestBodies[0]);
        Assert.Contains("\"max_tokens\":1200", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task SpellProviderRepairsInvalidJsonOnce()
    {
        var repaired = """
            {"accepted":true,"severity":"minor","outcomeText":"The reeds hide you.","effects":[{"type":"addStatus","target":"player","status":"river_concealed","duration":4}],"costs":[],"rejectedReason":null}
            """;
        var handler = new QueueHttpHandler(ChatResponse("The reeds agree."), ChatResponse(repaired));
        var provider = new OpenAiCompatibleSpellProvider(
            "http://example.test/v1/chat/completions",
            "test-model",
            httpClient: new HttpClient(handler));

        var result = await provider.ResolveAsync(new SpellRequest(
            "hide me under river-color",
            new { caster = new { id = "player" } },
            new[] { "addStatus", "message" }), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.Equal("addStatus", Assert.Single(result.Resolution!.Effects).Type);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("Previous invalid answer", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task DialogueProviderUsesSharedResponseParser()
    {
        var rawDialogue = """
            {"spokenText":"Jimmer can sell you a fine blade if you reach the road-stall.","delivery":"hushed","intent":"inform","proposals":{"claims":[{"text":"Jimmer can sell you a fine blade.","category":"merchant_stock","subject":"fine blade","salience":4,"confidence":80,"playerVisible":true,"bindAsPromise":true,"realizationKind":"merchant_stock","triggerHint":"travel","itemName":"fine blade","tags":["merchant","blade"]}],"actions":["none"]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OpenAiCompatibleDialogueProvider(
            "http://example.test/v1",
            "test-model",
            httpClient: new HttpClient(handler));

        var result = await provider.ResolveAsync(DialogueRequest("Lio, do you know a blade seller?"), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.Equal("openai-compatible-dialogue", result.Provider);
        var claim = Assert.Single(result.Response!.Proposals!.Claims!);
        Assert.Equal("merchant_stock", claim.Category);
        Assert.True(claim.BindAsPromise);
        Assert.Equal("none", Assert.Single(result.Response.Proposals.Actions!).Type);
        Assert.Equal("http://example.test/v1/chat/completions", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task ClaimExtractorUsesRouterThenDetail()
    {
        var detail = """
            {"claims":[{"text":"There is a town south of here.","category":"town","subject":"southward town","salience":4,"confidence":70,"playerVisible":true,"bindAsPromise":true,"promiseKind":"rumor","realizationKind":"site","triggerHint":"travel","tags":["town","south"]}]}
            """;
        var handler = new QueueHttpHandler(
            ChatResponse("{\"hasClaim\":true,\"capabilities\":[\"promise\"],\"reason\":\"place claim\"}"),
            ChatResponse(detail));
        var extractor = new OpenAiCompatibleDialogueClaimExtractor(
            "http://example.test/v1",
            "test-model",
            httpClient: new HttpClient(handler),
            apiKey: "test-key");

        var result = await extractor.ExtractAsync(new DialogueClaimRequest(
            7,
            "hollowmere_margin",
            "containment_yard",
            "prisoner_1",
            "Lio",
            new[] { "prisoner" },
            "soul_player",
            "What waits south?",
            new[] { "There is a town south of here." },
            Array.Empty<WorldMemoryRecord>(),
            Array.Empty<ClaimRecord>()), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.Equal("openai-compatible-dialogue-claims", result.Provider);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("http://example.test/v1/chat/completions", request.Uri);
            Assert.Equal("Bearer", request.AuthorizationScheme);
            Assert.Equal("test-key", request.AuthorizationParameter);
        });
        var claim = Assert.Single(result.Claims);
        Assert.Equal("town", claim.Category);
        Assert.True(claim.BindAsPromise);
    }

    [Fact]
    public async Task ClaimExtractorParsesCanonFields()
    {
        var detail = """
            {"claims":[{"text":"Folk-magic practice is punishable by execution here.","category":"local_law","subject":"folk magic law","salience":2,"confidence":90,"bindAsCanon":true,"canonKind":"local_law","canonSummary":"Folk magic is a capital crime","tags":["folk_magic","vigovia"]}]}
            """;
        var handler = new QueueHttpHandler(
            ChatResponse("{\"hasClaim\":true,\"capabilities\":[\"canon\"],\"reason\":\"local law\"}"),
            ChatResponse(detail));
        var extractor = new OpenAiCompatibleDialogueClaimExtractor(
            "http://example.test/v1",
            "test-model",
            httpClient: new HttpClient(handler));

        var result = await extractor.ExtractAsync(new DialogueClaimRequest(
            7,
            "hollowmere_margin",
            "containment_yard",
            "clerk_1",
            "Test clerk",
            new[] { "functionary" },
            "soul_player",
            "What is the local law?",
            new[] { "Folk-magic practice is punishable by execution here." },
            Array.Empty<WorldMemoryRecord>(),
            Array.Empty<ClaimRecord>()), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        var claim = Assert.Single(result.Claims);
        Assert.True(claim.BindAsCanon);
        Assert.Equal("local_law", claim.CanonKind);
        Assert.Equal("Folk magic is a capital crime", claim.CanonSummary);
        Assert.False(claim.BindAsPromise);
    }

    private static DialogueRequest DialogueRequest(string playerText) =>
        new(
            0,
            playerText,
            new DialogueParticipantCard(
                "prisoner_1",
                "Lio of Hollowmere",
                new[] { "npc", "prisoner", "hollowmere" },
                Faction: "hollowmere"),
            new DialogueParticipantCard(
                "player",
                "You",
                new[] { "sorcerer", "wild_magic" },
                Faction: "player"),
            new DialogueSceneCard(
                "imperial_encounter",
                "0,0",
                new[] { "Lio of Hollowmere (prisoner_1) at 14,5, range 1, tags npc,prisoner,hollowmere" },
                Array.Empty<string>(),
                Array.Empty<string>()),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content,
                    },
                },
            },
        });

    private sealed class QueueHttpHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public QueueHttpHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<CapturedRequest> Requests { get; } = new();

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(new CapturedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter);
}
