using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed partial class InteractionSystem
{
    private readonly GameEngine _engine;
    private readonly ItemSystem _itemSystem;
    private readonly PromiseRealizationSystem _promiseRealizationSystem;
    private readonly TurnSystem _turnSystem;

    public InteractionSystem(GameEngine engine, ItemSystem itemSystem, TurnSystem turnSystem)
    {
        _engine = engine;
        _itemSystem = itemSystem;
        _promiseRealizationSystem = new PromiseRealizationSystem(engine.State, engine);
        _turnSystem = turnSystem;
    }

    private GameState State => _engine.State;

    public ActionResult Talk(string text)
    {
        var preparation = PrepareDialogue(text);
        if (preparation.ImmediateResult is not null)
        {
            return preparation.ImmediateResult;
        }

        var turn = preparation.Turn!;
        var target = State.Entities.TryGetValue(EntityId.Create(turn.SpeakerId), out var entity)
            ? entity
            : null;
        if (target is null)
        {
            var hint = OutOfReachHint(_engine, State, turn.SpeakerId, entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>());
            return ActionResult.Simple("talk", false, false, turn.TurnBefore, State.Turn, hint ?? "No one nearby is ready to talk.");
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        ResolveDialogueIntent(target, turn.PlayerText, messages, deltas);
        return CompleteDialogue(turn, messages, deltas, generated: false, provider: "deterministic", rawText: null, delivery: null, intent: null);
    }

    public DialoguePreparation PrepareDialogue(string text)
    {
        var turnBefore = State.Turn;
        Entity? target;
        if (string.IsNullOrWhiteSpace(text) && State.SelectedTarget is null)
        {
            var candidates = NearbyCandidates(
                entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
                range: 2);
            if (candidates.Count > 1)
            {
                var names = string.Join(", ", candidates.Select(entity => entity.Name));
                return new DialoguePreparation(
                    null,
                    ActionResult.Simple(
                        "talk",
                        false,
                        false,
                        turnBefore,
                        State.Turn,
                        $"Who do you want to talk to? Nearby: {names}. Try \"talk Lio\" or select someone first."));
            }

            target = candidates.FirstOrDefault();
        }
        else
        {
            target = ResolveNearbyEntity(
                text,
                entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
                range: 2);
        }

        target ??= ResolveNearbyActorMention(text);
        if (target is null)
        {
            var hint = OutOfReachHint(_engine, State, text, entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>());
            return new DialoguePreparation(
                null,
                ActionResult.Simple("talk", false, false, turnBefore, State.Turn, hint ?? "No one nearby is ready to talk."));
        }

        var actor = target.TryGet<ActorComponent>(out var actorComponent) ? actorComponent : null;
        var profile = target.TryGet<ProfileComponent>(out var profileComponent)
            ? $"{profileComponent.PublicName}: {profileComponent.Appearance}"
            : null;
        var bond = State.Bonds.TryGet(SoulIdFor(target), PlayerSoulId(), out var bondRecord)
            ? BondSummary(bondRecord)
            : null;
        var want = target.TryGet<WantComponent>(out var wantComponent)
            ? WantSummary(wantComponent)
            : null;
        return new DialoguePreparation(
            new PreparedDialogueTurn(
                turnBefore,
                NormalizeDialoguePlayerText(text, target),
                target.Id.Value,
                target.Name,
                TagsFor(target).ToArray(),
                PlayerSoulId(),
                _engine.IsHostile(target, State.ControlledEntity),
                profile,
                actor?.Faction,
                bond,
                want),
            null);
    }

    public ActionResult ApplyGeneratedDialogue(
        PreparedDialogueTurn turn,
        string spokenText,
        string provider,
        string? rawText,
        string? delivery,
        string? intent)
    {
        var target = State.Entities.TryGetValue(EntityId.Create(turn.SpeakerId), out var entity)
            ? entity
            : null;
        if (target is null)
        {
            return new ActionResult
            {
                Action = "talk",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turn.TurnBefore,
                TurnAfter = State.Turn,
                TechnicalFailure = true,
                Messages = new[] { "Dialogue target disappeared before the reply could resolve." },
                Deltas = new[]
                {
                    new StateDelta(
                        "dialogueProviderFailed",
                        turn.SpeakerId,
                        "Dialogue target disappeared before the reply could resolve.",
                        new Dictionary<string, object?>
                        {
                            ["provider"] = provider,
                        }),
                },
            };
        }

        return CompleteDialogue(
            turn,
            new List<string> { spokenText },
            new List<StateDelta>(),
            generated: true,
            provider,
            rawText,
            delivery,
            intent);
    }

    private ActionResult CompleteDialogue(
        PreparedDialogueTurn turn,
        List<string> messages,
        List<StateDelta> deltas,
        bool generated,
        string provider,
        string? rawText,
        string? delivery,
        string? intent)
    {
        var target = State.Entities[EntityId.Create(turn.SpeakerId)];
        var dialogueLines = messages.ToArray();
        deltas.Add(new StateDelta(
            "dialogue",
            turn.SpeakerId,
            dialogueLines.FirstOrDefault() ?? $"{target.Name} answers.",
            new Dictionary<string, object?>
            {
                ["speakerId"] = turn.SpeakerId,
                ["speakerName"] = turn.SpeakerName,
                ["speakerTags"] = turn.SpeakerTags.ToArray(),
                ["listenerSoulId"] = turn.ListenerSoulId,
                ["playerText"] = turn.PlayerText,
                ["lines"] = dialogueLines,
                ["generated"] = generated,
                ["provider"] = provider,
                ["rawText"] = rawText,
                ["delivery"] = delivery,
                ["intent"] = intent,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
        // Echo the player's own spoken line into the log before the reply, so a conversation reads
        // as a back-and-forth rather than only the NPC's half (message-log immersion pass).
        if (!string.IsNullOrWhiteSpace(turn.PlayerText))
        {
            var playerLine = _engine.ApplyConsequence(WorldConsequence.Message(
                "player_command",
                $"You say, \"{turn.PlayerText.Trim()}\"",
                targetEntityId: State.ControlledEntityId.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: turn.PlayerText,
                reason: "The player's spoken line, echoed into the log before the reply.",
                operation: "playerSpeech",
                details: new Dictionary<string, object?>
                {
                    ["playerVisible"] = true,
                    ["speakerId"] = State.ControlledEntityId.Value,
                }));
            deltas.AddRange(playerLine.Deltas);
        }

        for (var index = 0; index < dialogueLines.Length; index++)
        {
            var line = dialogueLines[index];
            var applied = _engine.ApplyConsequence(WorldConsequence.Message(
                generated ? $"dialogue:{provider}" : "dialogue",
                line,
                targetEntityId: turn.SpeakerId,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: turn.SpeakerId,
                evidence: turn.PlayerText,
                reason: generated ? "Generated dialogue produced a spoken line." : "Deterministic dialogue produced a spoken line.",
                operation: "dialogueMessage",
                details: new Dictionary<string, object?>
                {
                    ["speakerId"] = turn.SpeakerId,
                    ["speakerName"] = turn.SpeakerName,
                    ["speakerTags"] = turn.SpeakerTags.ToArray(),
                    ["listenerSoulId"] = turn.ListenerSoulId,
                    ["playerText"] = turn.PlayerText,
                    ["lineIndex"] = index,
                    ["generated"] = generated,
                    ["provider"] = provider,
                    ["delivery"] = delivery,
                    ["intent"] = intent,
                }));
            deltas.AddRange(applied.Deltas);
        }

        if (!generated && dialogueLines.Length > 0)
        {
            var spokenText = string.Join(" ", dialogueLines);
            var memoryText = $"{turn.SpeakerName} spoke with the sorcerer. Player: {turn.PlayerText} Reply: {spokenText}";
            var memory = _engine.ApplyConsequence(WorldConsequence.RecordMemory(
                "dialogue_exchange",
                target.Id.Value,
                memoryText,
                "conversation",
                2,
                shareable: false,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: turn.PlayerText,
                reason: "Deterministic dialogue exchange.",
                operation: "dialogueExchangeMemory",
                details: new Dictionary<string, object?>
                {
                    ["speakerId"] = turn.SpeakerId,
                    ["playerText"] = turn.PlayerText,
                    ["spokenText"] = spokenText,
                }));
            deltas.AddRange(memory.Deltas);
        }

        var returnedObjectives = CompleteReturnedObjectives(target);
        deltas.AddRange(returnedObjectives);
        messages.AddRange(ObjectiveMessages(returnedObjectives));
        var completedObjectives = CompleteContactObjectives(target);
        deltas.AddRange(completedObjectives);
        messages.AddRange(ObjectiveMessages(completedObjectives));
        var surfacedClaims = ApplyClaimSeeds(target, "talk");
        deltas.AddRange(surfacedClaims);
        messages.AddRange(ObjectiveMessages(surfacedClaims));
        var generatedHandoff = ApplyGeneratedObjectiveHandoff(target, "talk");
        if (generatedHandoff is not null)
        {
            deltas.AddRange(generatedHandoff.Deltas);
            messages.AddRange(generatedHandoff.Messages);
        }

        var alreadyPersistedMessages = messages.ToList();
        deltas.AddRange(_promiseRealizationSystem.RealizeAnchoredPromises(target, "talk", messages, alreadyPersistedMessages));
        deltas.AddRange(PersistUnwrittenMessages(
            messages,
            alreadyPersistedMessages,
            "dialogue",
            "dialogueFallbackMessage",
            target.Id.Value,
            new Dictionary<string, object?>
            {
                ["speakerId"] = turn.SpeakerId,
                ["speakerName"] = turn.SpeakerName,
                ["generated"] = generated,
                ["provider"] = provider,
            }));

        var turnDeltas = AdvanceTurn();
        return new ActionResult
        {
            Action = "talk",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turn.TurnBefore,
            TurnAfter = State.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? TagValue(IEnumerable<string>? tags, string prefix) =>
        tags?.FirstOrDefault(tag => tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();

    private string DialogueLine(Entity target, List<StateDelta> deltas)
    {
        if (_engine.IsHostile(target, State.ControlledEntity))
        {
            return $"{target.Name} answers with trained imperial silence.";
        }

        if (target.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("prisoner", StringComparer.OrdinalIgnoreCase))
        {
            var targetSoulId = SoulIdFor(target);
            var playerSoulId = PlayerSoulId();
            var bond = State.Bonds.TryGet(targetSoulId, playerSoulId, out var existingBond)
                ? existingBond
                : NeutralBond(targetSoulId, playerSoulId);
            var legend = PlayerLegendSummary();
            return string.IsNullOrWhiteSpace(legend)
                ? $"{target.Name} whispers, \"Get me out of here and you'll have friends in Hollowmere who pay their debts. I'll make sure they know your name.\" {BondMoodLine(target, bond)}"
                : $"{target.Name} whispers, \"People already trade stories about you - {legend}. Get me out, and I'll see the right ones in Hollowmere hear it from me.\" {BondMoodLine(target, bond)}";
        }

        if (target.TryGet<ProfileComponent>(out var profile))
        {
            return $"{profile.PublicName}: {profile.Appearance}";
        }

        return $"{target.Name} has nothing urgent to say.";
    }

    private WorldConsequenceApplyResult ApplyBondUpdate(
        Entity subject,
        string source,
        int loyalty = 0,
        int fear = 0,
        int admiration = 0,
        int resentment = 0,
        string? posture = null,
        string operation = "updateBond",
        int maxDelta = 2) =>
        _engine.ApplyConsequence(WorldConsequence.UpdateBond(
            source,
            subject.Id.Value,
            PlayerSoulId(),
            loyalty,
            fear,
            admiration,
            resentment,
            posture,
            sourceEntityId: State.ControlledEntityId.Value,
            evidence: source,
            operation: operation,
            maxDelta: maxDelta,
            details: new Dictionary<string, object?>
            {
                ["subjectSoulId"] = SoulIdFor(subject),
                ["targetSoulId"] = PlayerSoulId(),
                ["interaction"] = source,
            }));

    private string PlayerSoulId() => SoulIdFor(State.ControlledEntity);

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static string? FindInventoryKey(InventoryComponent inventory, string item) =>
        inventory.Items.Keys
            .OrderBy(key => key)
            .FirstOrDefault(key =>
                key.Equals(item.Trim(), StringComparison.OrdinalIgnoreCase)
                || key.Contains(item.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string BondMoodLine(Entity target, BondRecord bond) =>
        $"{target.Name}'s posture is {BondSummary(bond)}.";

    private static BondRecord NeutralBond(string subjectSoulId, string targetSoulId) =>
        new(subjectSoulId, targetSoulId, Loyalty: 0, Fear: 0, Admiration: 0, Resentment: 0, Posture: "neutral");

    private static string BondSummary(BondRecord bond)
    {
        if (bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase))
        {
            return "following";
        }

        if (bond.Loyalty + bond.Admiration >= 5)
        {
            return "warm enough to risk something";
        }

        if (bond.Fear > bond.Loyalty + bond.Admiration)
        {
            return "afraid";
        }

        if (bond.Resentment >= 5)
        {
            return "resentful";
        }

        return bond.Posture;
    }

    private static string? WantSummary(WantComponent want)
    {
        if (!want.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(want.Text))
        {
            return null;
        }

        var stakes = string.IsNullOrWhiteSpace(want.Stakes) ? "" : $" Stakes: {want.Stakes}";
        return $"{want.Text} (salience {want.Salience}).{stakes}";
    }

    private string PlayerLegendSummary()
    {
        var playerSoulId = PlayerSoulId();
        var tags = State.Legend.Tags
            .Where(tag => tag.ActorSoulId.Equals(playerSoulId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Tag)
            .Select(group => $"{group.Key} {group.Sum(tag => tag.Weight)}")
            .OrderBy(text => text)
            .ToArray();
        return tags.Length == 0 ? "" : string.Join(", ", tags);
    }

    private IReadOnlyList<string> DescribeEntity(Entity entity)
    {
        var lines = new List<string> { $"{entity.Name} ({entity.Id.Value})." };
        if (entity.TryGet<DescriptionComponent>(out var description))
        {
            lines.Add(description.Text);
        }

        foreach (var record in State.Canon.Records
            .Where(record => record.AttachedTo.Equals(entity.Id.Value, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.TurnCreated)
            .Take(2))
        {
            lines.Add($"Known detail: {record.Summary}");
        }

        if (entity.TryGet<PhysicalComponent>(out var physical))
        {
            lines.Add($"Material: {physical.Material}; blocks movement: {physical.BlocksMovement}.");
        }

        if (entity.TryGet<ActorComponent>(out var actor))
        {
            lines.Add($"HP {actor.HitPoints}/{actor.MaxHitPoints}; faction {actor.Faction}.");
            if (actor.Alive)
            {
                // Inspection teaches how to fight it (tactical-mastery pillar): its hitting power, its
                // guard, and the damage it shrugs off or folds under -- deterministic facts the player
                // can act on to offset wild magic's uncertainty.
                lines.Add($"In a fight: strikes for about {actor.Attack}, wards off {actor.Defense}.");
                if (entity.TryGet<ResistanceComponent>(out var resistance))
                {
                    var weakTo = resistance.Weaknesses
                        .Where(pair => pair.Value > 0)
                        .Select(pair => pair.Key)
                        .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var resists = resistance.Resistances
                        .Where(pair => pair.Value > 0)
                        .Select(pair => pair.Key)
                        .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (weakTo.Length > 0)
                    {
                        lines.Add($"Weak to {string.Join(", ", weakTo)}.");
                    }

                    if (resists.Length > 0)
                    {
                        lines.Add($"Shrugs off {string.Join(", ", resists)}.");
                    }
                }
            }
        }

        if (entity.TryGet<ReadableComponent>(out var readable))
        {
            lines.Add($"Readable: {readable.Title}.");
        }

        if (entity.TryGet<DoorComponent>(out var door))
        {
            lines.Add(door.IsOpen ? "It is open." : string.IsNullOrWhiteSpace(door.KeyId) ? "It is closed." : "It is locked.");
        }

        if (entity.TryGet<TagsComponent>(out var tags) && tags.Tags.Count > 0)
        {
            // Surface the charter-learning affordance as a legible curiosity hook rather than leaving
            // the player to decode a raw "teaches_charter:*" tag: reading it grows the reliable
            // deterministic magic that steadies a repertoire otherwise built on wild resolution.
            if (tags.Tags.Any(tag => tag.StartsWith("teaches_charter:", StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add("Reading it may teach you a charter form.");
            }

            lines.Add($"Tags: {string.Join(", ", tags.Tags)}.");
        }

        if (entity.TryGet<StatusContainerComponent>(out var statuses))
        {
            var active = statuses.Statuses.Where(IsStatusActive).Select(status => status.DisplayName).ToArray();
            if (active.Length > 0)
            {
                lines.Add($"Statuses: {string.Join(", ", active)}.");
            }
        }

        return lines;
    }

    private void ResolveDoorConsequences(
        Entity door,
        List<string> messages,
        List<StateDelta> deltas,
        List<string> alreadyPersistedMessages)
    {
        deltas.AddRange(_promiseRealizationSystem.RealizeAnchoredPromises(door, "open", messages, alreadyPersistedMessages));
    }

    private static IReadOnlyList<StateDelta> FailureDiagnostics(IReadOnlyList<StateDelta> deltas) =>
        deltas
            .Where(delta => delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void RemoveRangeFrom<T>(List<T> values, int start)
    {
        if (values.Count > start)
        {
            values.RemoveRange(start, values.Count - start);
        }
    }

    private static bool RemovePersistedMessage(List<string> alreadyPersistedMessages, string message)
    {
        var index = alreadyPersistedMessages.FindIndex(item => string.Equals(item, message, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        alreadyPersistedMessages.RemoveAt(index);
        return true;
    }

    private IReadOnlyList<StateDelta> PersistUnwrittenMessages(
        IEnumerable<string> messages,
        List<string> alreadyPersistedMessages,
        string source,
        string operation,
        string targetEntityId,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var deltas = new List<StateDelta>();
        foreach (var message in messages)
        {
            if (RemovePersistedMessage(alreadyPersistedMessages, message))
            {
                continue;
            }

            var payload = details is null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : details.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            payload["fallback"] = true;
            payload["playerVisible"] = true;

            var applied = _engine.ApplyConsequence(WorldConsequence.Message(
                source,
                message,
                targetEntityId: targetEntityId,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: message,
                reason: "An interaction system message was not already persisted by a narrower typed consequence.",
                operation: operation,
                details: payload));
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
    }

    private static IReadOnlyList<string> TagsFor(Entity entity)
    {
        var tags = new List<string>();
        if (entity.TryGet<TagsComponent>(out var tagComponent))
        {
            tags.AddRange(tagComponent.Tags);
        }

        if (entity.TryGet<ItemComponent>(out var item))
        {
            tags.AddRange(item.Tags);
            tags.Add(item.Material);
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            tags.AddRange(fixture.Tags);
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag).ToArray();
    }

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > State.Turn;

    private bool CanEnter(GridPoint point) =>
        _engine.InBounds(point)
        && !State.BlockingTerrain.Contains(point)
        && _engine.BlockingEntityAt(point) is null;

    private IReadOnlyList<StateDelta> AdvanceTurn() => _engine.AdvanceTurn();

    private static string NormalizeId(string value, string fallback)
    {
        var cleaned = string.Join(
            '_',
            value.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string NormalizeToken(string value, string fallback) =>
        NormalizeId(value, fallback);

    private static int InteractionDistance(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
