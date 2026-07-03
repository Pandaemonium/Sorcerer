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

    [Fact]
    public async Task DialogueParserReadsWantProposal()
    {
        var rawDialogue = """
            {"spokenText":"If the door opens, I want a quiet road south.","proposals":{"claims":[],"memories":[],"bond":null,"want":{"entityId":"prisoner_1","text":"Reach a quiet road south.","salience":4,"status":"active","stakes":"The escape now depends on leaving quietly.","addTags":["road","south"],"removeTags":["cell"],"reason":"Lio changed his immediate desire."},"actions":[]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, what do you want now?"), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        var want = result.Response!.Proposals!.Want!;
        Assert.Equal("prisoner_1", want.EntityId);
        Assert.Contains("quiet road south", want.Text);
        Assert.Equal(4, want.Salience);
        Assert.Equal("active", want.Status);
        Assert.Contains("leaving quietly", want.Stakes);
        Assert.Contains("road", want.AddTags!);
        Assert.Contains("south", want.AddTags!);
        Assert.Contains("cell", want.RemoveTags!);
    }

    [Fact]
    public async Task DialogueParserReadsExpandedActionGrammarFields()
    {
        var rawDialogue = """
            {"spokenText":"I can follow, trade, whisper the lock loose, mark the board, tag myself helpful, and tell you the local law: folk-magic practice is punishable by execution here.","proposals":{"actions":[{"type":"follow_me","reason":"Lio agrees to follow."},{"type":"offer_trade","itemName":"silver pin","quantity":2,"gold":9},{"type":"reveal_service","name":"ward-breaking","description":"I can whisper a lock loose.","serviceId":"ward_breaking","effectKind":"open_or_unlock","targetHint":"cell door","itemCost":"grave salt","goldCost":3,"tags":["folk_magic","door"]},{"type":"mark_location","name":"loose floorboard","fixtureType":"marker","description":"A board with fresh nail-scars.","x":5,"y":6,"blocksMovement":false,"interactableVerbs":["examine"]},{"type":"consequence","consequenceType":"add_tags","consequenceTiming":"deferred","targetEntityId":"prisoner_1","consequencePayload":{"tags":["helpful"],"operation":"dialogueConsequence","delay":1}},{"type":"canonize_fact","canonKind":"local_law","canonText":"Folk-magic practice is punishable by execution here.","canonSummary":"Folk magic is a capital crime","tags":["folk_magic","vigovia"]}]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, what can you do?"), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        var actions = result.Response!.Proposals!.Actions!;
        Assert.Equal("follow_me", actions[0].Type);
        Assert.Equal("Lio agrees to follow.", actions[0].Reason);
        Assert.Equal("offer_trade", actions[1].Type);
        Assert.Equal("silver pin", actions[1].ItemName);
        Assert.Equal(2, actions[1].Quantity);
        Assert.Equal(9, actions[1].Gold);
        Assert.Equal("reveal_service", actions[2].Type);
        Assert.Equal("ward-breaking", actions[2].Name);
        Assert.Equal("ward_breaking", actions[2].ServiceId);
        Assert.Equal("open_or_unlock", actions[2].EffectKind);
        Assert.Equal("cell door", actions[2].TargetHint);
        Assert.Equal("grave salt", actions[2].ItemCost);
        Assert.Equal(3, actions[2].GoldCost);
        Assert.Contains("folk_magic", actions[2].Tags!);
        Assert.Equal("mark_location", actions[3].Type);
        Assert.Equal("marker", actions[3].FixtureType);
        Assert.Equal(5, actions[3].X);
        Assert.Equal(6, actions[3].Y);
        Assert.False(actions[3].BlocksMovement);
        Assert.Contains("examine", actions[3].InteractableVerbs!);
        Assert.Equal("consequence", actions[4].Type);
        Assert.Equal("add_tags", actions[4].ConsequenceType);
        Assert.Equal("deferred", actions[4].ConsequenceTiming);
        Assert.Equal("prisoner_1", actions[4].TargetEntityId);
        Assert.NotNull(actions[4].ConsequencePayload);
        var payload = actions[4].ConsequencePayload!;
        Assert.Equal("dialogueConsequence", payload["operation"]);
        Assert.Equal(1, payload["delay"]);
        var tags = Assert.IsType<object[]>(payload["tags"]);
        Assert.Contains("helpful", tags);
        Assert.Equal("canonize_fact", actions[5].Type);
        Assert.Equal("local_law", actions[5].CanonKind);
        Assert.Contains("punishable by execution", actions[5].CanonText);
        Assert.Equal("Folk magic is a capital crime", actions[5].CanonSummary);
        Assert.Contains("vigovia", actions[5].Tags!);
    }

    [Fact]
    public async Task DialogueParserReadsDirectConsequenceTopLevelFields()
    {
        var rawDialogue = """
            {"spokenText":"I can mark myself helpful and worry the lock open.","proposals":{"actions":[{"type":"add_tags","targetEntityId":"prisoner_1","tags":["helpful"]},{"type":"request_service","service":"ward-breaking"}]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, what can you do?"), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        var actions = result.Response!.Proposals!.Actions!;
        Assert.Equal("add_tags", actions[0].Type);
        Assert.Equal("prisoner_1", actions[0].TargetEntityId);
        Assert.Contains("helpful", actions[0].Tags!);
        Assert.Equal("request_service", actions[1].Type);
        Assert.Equal("ward-breaking", actions[1].ServiceId);
        Assert.NotNull(actions[1].ConsequencePayload);
        Assert.Equal("ward-breaking", actions[1].ConsequencePayload!["service"]);
    }

    [Fact]
    public async Task DialogueParserPreservesArbitraryTopLevelConsequenceFields()
    {
        var rawDialogue = """
            {"spokenText":"Hold still; I can mark you for two breaths.","proposals":{"actions":[{"type":"apply_status","targetEntityId":"prisoner_1","status":"oath-marked","duration":2,"displayName":"oath marked"}]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, can you mark yourself?"), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        var action = Assert.Single(result.Response!.Proposals!.Actions!);
        Assert.Equal("apply_status", action.Type);
        Assert.NotNull(action.ConsequencePayload);
        var payload = action.ConsequencePayload!;
        Assert.Equal("apply_status", payload["type"]);
        Assert.Equal("prisoner_1", payload["targetEntityId"]);
        Assert.Equal("oath-marked", payload["status"]);
        Assert.Equal(2, payload["duration"]);
        Assert.Equal("oath marked", payload["displayName"]);
    }

    [Fact]
    public async Task DialogueParserMergesNestedPayloadWithTopLevelConsequenceFields()
    {
        var rawDialogue = """
            {"spokenText":"Hold still; I can mark you for two breaths.","proposals":{"actions":[{"type":"apply_status","targetEntityId":"prisoner_1","status":"top-level-mark","duration":2,"consequencePayload":{"status":"nested-mark","operation":"dialogueNestedWins"}}]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, can you mark yourself?"), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        var action = Assert.Single(result.Response!.Proposals!.Actions!);
        Assert.Equal("apply_status", action.Type);
        Assert.NotNull(action.ConsequencePayload);
        var payload = action.ConsequencePayload!;
        Assert.Equal("apply_status", payload["type"]);
        Assert.Equal("prisoner_1", payload["targetEntityId"]);
        Assert.Equal("nested-mark", payload["status"]);
        Assert.Equal(2, payload["duration"]);
        Assert.Equal("dialogueNestedWins", payload["operation"]);
    }

    [Fact]
    public async Task DialoguePromptAdvertisesSharedConsequenceTiming()
    {
        var rawDialogue = """
            {"spokenText":"Give me a breath and I will mark the ledger.","proposals":{"actions":[]}}
            """;
        var handler = new QueueHttpHandler(ChatResponse(rawDialogue));
        var provider = new OllamaDialogueProvider(httpClient: new HttpClient(handler), model: "test-model");

        var result = await provider.ResolveAsync(Request("Lio, can you mark this later?"), CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        var requestBody = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(requestBody);
        var systemPrompt = document.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString();
        Assert.NotNull(systemPrompt);
        Assert.Contains("consequenceTiming immediate, after_turn, world_pump, or deferred", systemPrompt);
        Assert.Contains("non-immediate timing schedules one typed consequence", systemPrompt);
        Assert.Contains("known consequence id directly in type", systemPrompt);
        Assert.Contains("{\"type\":\"request_service\"", systemPrompt);
        Assert.Contains("{\"type\":\"transform_entity\"", systemPrompt);
        Assert.Contains("known typed consequence ids", systemPrompt);
        Assert.Contains("extra top-level fields on generic/typed consequence actions are preserved as payload fields", systemPrompt);
        Assert.DoesNotContain("use schedule_event rather than non-immediate timing", systemPrompt);
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
