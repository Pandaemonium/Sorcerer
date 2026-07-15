using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class DialogueAddressingTests
{
    // Regression: "talk Nim, Lio sent me" must address Nim (the leading name), not Lio (a third
    // party the player names inside the message). The old resolver returned the first nearby
    // candidate whose name appeared anywhere in the sentence, ordered by id, so a mentioned sender
    // with a lower id hijacked the conversation.
    [Fact]
    public async Task TalkAddressesTheLeadingNameNotAThirdPartyNamedInTheMessage()
    {
        var provider = new CapturingDialogueProvider();
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }

        var origin = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        // Aaron is spawned first, so he sorts earliest by entity id — the exact ordering that made
        // the old resolver pick the mentioned sender instead of the addressee.
        SpawnNeighbor(session, "Aaron Firstsort", origin.Translate(1, 0));
        SpawnNeighbor(session, "Zed Lastsort", origin.Translate(0, 1));

        var talk = await session.ExecuteAsync(new TalkCommand("Zed, Aaron sent me with a warning."));

        Assert.True(talk.Success, string.Join(" | ", talk.Messages));
        Assert.NotNull(provider.LastRequest);
        Assert.Contains("Zed", provider.LastRequest!.Speaker.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Aaron", provider.LastRequest.Speaker.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BareNameStillAddressesThatNeighbor()
    {
        var provider = new CapturingDialogueProvider();
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }

        var origin = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        SpawnNeighbor(session, "Aaron Firstsort", origin.Translate(1, 0));
        SpawnNeighbor(session, "Zed Lastsort", origin.Translate(0, 1));

        await session.ExecuteAsync(new TalkCommand("Aaron"));

        Assert.NotNull(provider.LastRequest);
        Assert.Contains("Aaron", provider.LastRequest!.Speaker.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void SpawnNeighbor(GameSession session, string name, GridPoint at)
    {
        var applied = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            name,
            at.X,
            at.Y,
            prefix: "neighbor",
            glyph: 'p',
            faction: "neutral",
            hp: 6,
            attack: 0,
            tags: new[] { "npc", "resident" },
            material: "flesh",
            roles: new[] { "resident" },
            controllerKind: "ai",
            aiPolicyId: "resident",
            summoned: false,
            interactableVerbs: new[] { "talk" },
            includeMemory: true,
            emitMessage: false));
        Assert.True(applied.Applied, applied.Error);
    }

    private sealed class CapturingDialogueProvider : IDialogueProvider
    {
        public DialogueRequest? LastRequest { get; private set; }

        public string Name => "capturing-dialogue";

        public Task<DialogueProviderResult> ResolveAsync(DialogueRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new DialogueProviderResult(
                Name,
                RawText: "",
                TechnicalFailure: false,
                Error: null,
                Response: new DialogueResponse($"{request.Speaker.Name} nods.", Delivery: "flat", Intent: "inform")));
        }
    }
}
