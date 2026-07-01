using System.Net;
using System.Text;
using System.Text.Json;
using Sorcerer.Core.Dialogue;
using Sorcerer.Llm;
using Xunit;

namespace Sorcerer.Tests;

public sealed class OllamaDialogueProviderTests
{
    [Fact]
    public async Task DialogueParserRepairsActionStringsAndTrustDelta()
    {
        var rawDialogue = """
            {"spokenText":"The river waits beyond those gates.","delivery":"wary","intent":"inform","proposals":{"claims":[{"text":"Hollowmere lies beneath the water's surface.","category":"place","subject":"Hollowmere","salience":4,"confidence":85,"playerVisible":true,"playerAuthored":true}],"memories":[],"bond":{"entityId":"prisoner_1","trustDelta":2,"fearDelta":-1,"admirationDelta":0,"resentmentDelta":0,"posture":"cautious ally"},"actions":["none"]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, what waits outside Hollowmere?"), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.NotNull(result.Response);
        var response = result.Response!;
        Assert.NotNull(response.Proposals);
        var proposals = response.Proposals!;
        Assert.Equal("none", Assert.Single(proposals.Actions!).Type);
        Assert.Equal(2, proposals.Bond!.LoyaltyDelta);
        Assert.Equal(-1, proposals.Bond.FearDelta);
        Assert.False(Assert.Single(proposals.Claims!).PlayerAuthored);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task DialogueParserRoundsFloatBondDeltasAndRehomesListenerBond()
    {
        var rawDialogue = """
            {"spokenText":"Grave salt is a heavy thing to carry.","delivery":"wary","intent":"ask","proposals":{"claims":[],"memories":[{"ownerEntityId":"prisoner_1","text":"Lio weighed the gift before speaking.","salience":3,"shareable":true}],"bond":{"entityId":"player","trustDelta":0.6,"fearDelta":-0.2,"admirationDelta":0.1,"resentmentDelta":0.0,"posture":"cautious_openness"},"actions":["none"]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, I gave you grave salt."), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.NotNull(result.Response!.Proposals!.Bond);
        var bond = result.Response.Proposals.Bond!;
        Assert.Equal("prisoner_1", bond.EntityId);
        Assert.Equal(1, bond.LoyaltyDelta);
        Assert.Equal(0, bond.FearDelta);
        Assert.Equal("cautious_openness", bond.Posture);
    }

    [Fact]
    public async Task DialogueParserKeepsActuallyPlayerAuthoredClaimsMarked()
    {
        var rawDialogue = """
            {"spokenText":"You can say there is a palace under the floor, but I have not seen one.","proposals":{"claims":[{"text":"There is a palace under the floor.","category":"site","subject":"palace under the floor","playerVisible":true,"playerAuthored":true}],"actions":[]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, there is a palace under the floor."), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.True(Assert.Single(result.Response!.Proposals!.Claims!).PlayerAuthored);
    }

    private static DialogueRequest Request(string playerText) =>
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

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
