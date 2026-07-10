using System.Net;
using System.Text;
using System.Text.Json;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
using Sorcerer.Magic.Capabilities;
using Sorcerer.Magic.Operations;
using Xunit;

namespace Sorcerer.Tests;

public sealed class SpellRouterTests
{
    [Fact]
    public async Task OpenAiRouterParsesCapabilityObject()
    {
        var handler = new QueueHttpHandler(ChatResponse("{\"capabilities\":[\"memory_edit\",\"prophecy\"]}"));
        var router = new OpenAiCompatibleSpellRouter(
            "http://example.test/v1",
            "test-model",
            httpClient: new HttpClient(handler),
            apiKey: "test-key");

        var result = await router.RouteAsync("make the baron forget me", "menu", CancellationToken.None);

        Assert.False(result.TechnicalFailure);
        Assert.Equal(new[] { "memory_edit", "prophecy" }, result.CapabilityNames);
        Assert.Equal("http://example.test/v1/chat/completions", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task OpenAiRouterToleratesBareArrayAndGarbage()
    {
        var bareArray = new OpenAiCompatibleSpellRouter(
            "http://example.test/v1",
            "test-model",
            httpClient: new HttpClient(new QueueHttpHandler(ChatResponse("[\"summoning\"]"))));
        var garbage = new OpenAiCompatibleSpellRouter(
            "http://example.test/v1",
            "test-model",
            httpClient: new HttpClient(new QueueHttpHandler(ChatResponse("not json at all"))));

        var bare = await bareArray.RouteAsync("call a wolf", "menu", CancellationToken.None);
        var noise = await garbage.RouteAsync("call a wolf", "menu", CancellationToken.None);

        Assert.Equal(new[] { "summoning" }, bare.CapabilityNames);
        // Garbage content is not a router failure (the HTTP call succeeded); it just names nothing.
        Assert.False(noise.TechnicalFailure);
        Assert.Empty(noise.CapabilityNames);
    }

    [Fact]
    public async Task OpenAiRouterReportsHttpFailure()
    {
        var handler = new QueueHttpHandler((HttpStatusCode.InternalServerError, "boom"));
        var router = new OpenAiCompatibleSpellRouter(
            "http://example.test/v1",
            "test-model",
            httpClient: new HttpClient(handler));

        var result = await router.RouteAsync("call a wolf", "menu", CancellationToken.None);

        Assert.True(result.TechnicalFailure);
        Assert.Empty(result.CapabilityNames);
    }

    [Fact]
    public void RouterPicksUnionWithKeywordSelectionAndIgnoreUnknownNames()
    {
        var registry = CapabilityRegistry.CreateDefault();
        const string spell = "make the room a little warmer";

        var keywordOnly = registry.Select(spell).Select(card => card.Id).ToArray();
        var withRouter = registry.Select(spell, new[] { "memory_edit", "totally_bogus_card" })
            .Select(card => card.Id)
            .ToArray();

        Assert.DoesNotContain("memory_edit", keywordOnly);
        Assert.Contains("memory_edit", withRouter);
        Assert.DoesNotContain("totally_bogus_card", withRouter);
    }

    [Fact]
    public void EmptyRouterPicksLeaveKeywordSelectionUnchanged()
    {
        var registry = CapabilityRegistry.CreateDefault();
        const string spell = "a wall of fire that makes them forget I was here";

        var keywordOnly = registry.Select(spell).Select(card => card.Id).ToArray();
        var emptyRouter = registry.Select(spell, Array.Empty<string>()).Select(card => card.Id).ToArray();

        Assert.Equal(keywordOnly, emptyRouter);
    }

    [Fact]
    public void RoutedIndexTrimsUnselectedRoutableCardsAndSelectionRestoresThem()
    {
        var registry = OperationRegistry.CreateDefault();
        var capabilities = CapabilityRegistry.CreateDefault();
        var routable = capabilities.AllEffectTypes();

        bool IsLean(OperationCardView card) =>
            string.IsNullOrEmpty(card.PromptGuidance) && card.Fields.Count == 0 && card.Examples.Count == 0;

        // An unrouted cast gets only the small general palette. Exotic mechanics use routing or
        // the bounded needsCapability retry instead of taxing every ordinary cast.
        var unrouted = registry.ToRoutedIndex(Array.Empty<string>(), routable);
        Assert.Contains("heal", unrouted.Names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("addStatus", unrouted.Names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("teleport", unrouted.Names, StringComparer.OrdinalIgnoreCase);
        Assert.True(unrouted.Names.Count <= 8, $"unrouted cast advertises too many operations: {string.Join(", ", unrouted.Names)}");

        // A routed cast adds only the selected mechanic; unrelated operations remain absent.
        var routed = registry.ToRoutedIndex(new[] { "summon" }, routable);
        Assert.Contains("summon", routed.Names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("teleport", routed.Names, StringComparer.OrdinalIgnoreCase);

        // Selecting a capability restores its full operation card.
        var restored = registry.ToRoutedIndex(new[] { "teleport" }, routable);
        var restoredCard = restored.Cards.Single(card => card.Name.Equals("teleport", StringComparison.OrdinalIgnoreCase));
        Assert.False(IsLean(restoredCard));
    }

    [Theory]
    [InlineData("raise a wall of ice between us", false)]
    [InlineData("heal my wounds with green light", false)]
    [InlineData("make the fresco weep real tears", true)]
    [InlineData("summon a moth and make the room mourn", false)]
    public void SemanticRouterRunsOnlyWhenDeterministicRoutingIsInsufficient(string spell, bool expected)
    {
        var registry = CapabilityRegistry.CreateDefault();

        Assert.Equal(expected, registry.ShouldConsultRouter(spell));
    }

    [Fact]
    public void OnlyOffScreenCapableSpellsRequestHiddenEntityContext()
    {
        var registry = CapabilityRegistry.CreateDefault();

        var memory = registry.Select("make the captain forget my face");
        var memoryRequired = memory
            .SelectMany(card => card.RequiredContext)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("memory_edit", memory.Select(card => card.Id));
        Assert.Contains("hidden_entities", memoryRequired);

        var fireball = registry.Select("hurl a fireball at the nearest soldier");
        var fireballRequired = fireball
            .SelectMany(card => card.RequiredContext)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden_entities", fireballRequired);
    }

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content } },
            },
        });

    private sealed class QueueHttpHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public QueueHttpHandler(params string[] okBodies)
            : this(okBodies.Select(body => (HttpStatusCode.OK, body)).ToArray())
        {
        }

        public QueueHttpHandler(params (HttpStatusCode Status, string Body)[] responses)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri?.ToString() ?? string.Empty));
            var (status, body) = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record CapturedRequest(string Uri);
}
