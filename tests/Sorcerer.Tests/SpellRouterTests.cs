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

        var full = registry.ToNarrowedIndex(Array.Empty<string>());
        var routed = registry.ToRoutedIndex(Array.Empty<string>(), routable);

        // Same operations are advertised; only the card detail differs.
        Assert.Equal(full.Names, routed.Names);

        var fullByName = full.Cards.ToDictionary(card => card.Name, StringComparer.OrdinalIgnoreCase);
        bool IsLean(OperationCardView card) =>
            string.IsNullOrEmpty(card.PromptGuidance) && card.Fields.Count == 0 && card.Examples.Count == 0;

        // At least one operation that carried real guidance/fields is trimmed to a lean card.
        var trimmed = routed.Cards
            .Where(card => IsLean(card)
                && fullByName.TryGetValue(card.Name, out var original)
                && !IsLean(original))
            .Select(card => card.Name)
            .ToArray();
        Assert.NotEmpty(trimmed);

        // Selecting a capability that unlocks that operation restores its full card.
        var restored = registry.ToRoutedIndex(new[] { trimmed[0] }, routable);
        var restoredCard = restored.Cards.Single(card => card.Name.Equals(trimmed[0], StringComparison.OrdinalIgnoreCase));
        Assert.False(IsLean(restoredCard));
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
