using System.Text.Json;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Scenarios;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

public sealed class GameSession
{
    private const int VisibleClaimSalience = 3;
    private const int DialogueBondDeltaLimit = 2;

    private readonly IWildMagicController _magic;
    private readonly IDialogueProvider _dialogueProvider;
    private readonly IDialogueAuditSink _dialogueAudit;
    private readonly IDialogueClaimExtractor _claimExtractor;
    private readonly List<PendingClaimExtraction> _pendingClaimExtractions = new();
    private PendingCast? _pendingCast;
    private int _pendingCastSerial;

    public GameSession(
        GameState state,
        IWildMagicController? magic = null,
        IDialogueClaimExtractor? claimExtractor = null,
        IDialogueProvider? dialogueProvider = null,
        IDialogueAuditSink? dialogueAudit = null)
    {
        Engine = new GameEngine(state);
        _magic = magic ?? NullWildMagicController.Instance;
        _claimExtractor = claimExtractor ?? NullDialogueClaimExtractor.Instance;
        _dialogueProvider = dialogueProvider ?? NullDialogueProvider.Instance;
        _dialogueAudit = dialogueAudit ?? NullDialogueAuditSink.Instance;
    }

    public GameEngine Engine { get; private set; }

    public static GameSession CreateImperialEncounter(
        IWildMagicController? magic = null,
        string? originId = null,
        int seed = 7,
        IReadOnlyList<RunChronicleRecord>? memorials = null,
        IDialogueClaimExtractor? claimExtractor = null,
        IDialogueProvider? dialogueProvider = null,
        IDialogueAuditSink? dialogueAudit = null)
    {
        var state = TestScenarios.ImperialEncounter(originId, memorials);
        state.Seed = Math.Max(1, seed);
        state.Rng = new DeterministicRng(state.Seed);
        return new GameSession(state, magic, claimExtractor, dialogueProvider, dialogueAudit);
    }

    public async Task<ActionResult> ExecuteAsync(
        GameCommand command,
        CancellationToken cancellationToken = default)
    {
        if (_pendingCast is not null && !CanExecuteDuringPendingCast(command))
        {
            return ActionResult.Simple(
                "pending_cast",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                "A spell is waiting to resolve; use await_cast or cancel_cast.");
        }

        var pendingClaimCountBeforeCommand = _pendingClaimExtractions.Count;
        var result = command switch
        {
            MoveCommand move => Engine.MoveControlled(move.Direction),
            WaitCommand => Engine.Wait(),
            InspectCommand => Engine.Inspect(),
            CastCommand cast => await _magic.CastAsync(Engine, cast, cancellationToken),
            BeginCastCommand cast => BeginCast(cast),
            AwaitCastCommand => await AwaitCast(cancellationToken),
            CancelCastCommand => CancelCast(),
            TargetCommand target => SetTarget(target),
            ClearTargetCommand => ClearTarget(),
            MapCommand => Engine.Inspect(),
            TravelCommand travel => Engine.Travel(travel.Direction),
            AtlasCommand => Engine.Atlas(),
            PickupCommand pickup => Engine.Pickup(pickup.Target),
            DropCommand drop => Engine.DropItem(drop.Item),
            UseItemCommand use => Engine.UseItem(use.Item),
            EquipCommand equip => Engine.EquipItem(equip.Item),
            UnequipCommand unequip => Engine.UnequipItem(unequip.SlotOrItem),
            FocusCommand focus => Engine.FocusItem(focus.SlotOrItem),
            UnfocusCommand unfocus => Engine.UnfocusItem(unfocus.SlotOrItem),
            ProtectItemCommand protect => ProtectItem(protect.Item, protectedState: true),
            UnprotectItemCommand unprotect => ProtectItem(unprotect.Item, protectedState: false),
            ReagentsCommand => Engine.Reagents(),
            WaresCommand wares => Engine.Wares(wares.Target),
            BuyCommand buy => Engine.Buy(buy.Item, buy.Target),
            SellCommand sell => Engine.Sell(sell.Item, sell.Target),
            JournalCommand => Engine.Journal(),
            CharacterCommand => Engine.CharacterSheet(),
            TalkCommand talk => await TalkAsync(talk, cancellationToken),
            GiveCommand give => Engine.Give(give.Item, give.Target),
            RecruitCommand recruit => Engine.Recruit(recruit.Target),
            BondsCommand bonds => Engine.Bonds(bonds.Target),
            ReadCommand read => Engine.Read(read.Target),
            ExamineCommand examine => Engine.Examine(examine.Target),
            OpenCommand open => Engine.Open(open.Target),
            PossessCommand possess => Engine.Possess(possess.Target),
            StandingCommand => Engine.Standing(),
            FollowersCommand => Engine.Followers(),
            JobsCommand => Engine.Jobs(),
            SaveCommand save => await SaveGameAsync(save.Path),
            LoadCommand load => LoadGame(load.Path),
            HelpCommand => Help(),
            QuitCommand => Quit(),
            UnknownCommand unknown => ActionResult.Simple(
                "unknown",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                $"Unknown command: {unknown.Text}"),
            _ => ActionResult.Simple(
                "unknown",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                "Unknown command."),
        };

        if (command is TalkCommand spokenDialogue && result.Success)
        {
            QueueDialogueClaimExtraction(spokenDialogue, result);
        }

        result = ApplyCompletedClaimExtractions(result, pendingClaimCountBeforeCommand);
        var completed = CompleteRunIfNeeded(result);
        return completed.ShouldQuit ? completed : CompleteRunIfNeeded(AddActorTurns(completed));
    }

    public GameView View() => Engine.View();

    public AgentObservation Observation(bool debug = false)
    {
        var observation = Engine.Observation(debug);
        return observation with
        {
            PendingCast = _pendingCast is null
                ? null
                : new PendingCastView(_pendingCast.Id, _pendingCast.Command.Text, "waiting"),
        };
    }

    private ActionResult SetTarget(TargetCommand command)
    {
        var turn = Engine.State.Turn;
        if (!Engine.InBounds(command.Position))
        {
            return ActionResult.Simple(
                "target",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "Target is outside the encounter.");
        }

        Engine.State.SelectedTarget = command.Position;
        var message = $"Target set to {command.Position.X},{command.Position.Y}.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            "target",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private ActionResult ClearTarget()
    {
        var turn = Engine.State.Turn;
        Engine.State.SelectedTarget = null;
        var message = "Target cleared.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            "target",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private ActionResult BeginCast(BeginCastCommand command)
    {
        var turn = Engine.State.Turn;
        if (string.IsNullOrWhiteSpace(command.Text))
        {
            return ActionResult.Simple(
                "begin_cast",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "No spell was spoken.");
        }

        var id = $"cast_{++_pendingCastSerial}";
        _pendingCast = new PendingCast(
            id,
            new CastCommand(command.Text, command.Performance ?? CastPerformance.Neutral));
        var message = $"Pending cast {id} is waiting to resolve.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            "begin_cast",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private async Task<ActionResult> AwaitCast(CancellationToken cancellationToken)
    {
        var pending = _pendingCast;
        if (pending is null)
        {
            return ActionResult.Simple(
                "await_cast",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                "No spell is waiting to resolve.");
        }

        _pendingCast = null;
        return await _magic.CastAsync(Engine, pending.Command, cancellationToken);
    }

    private ActionResult CancelCast()
    {
        var turn = Engine.State.Turn;
        if (_pendingCast is null)
        {
            return ActionResult.Simple(
                "cancel_cast",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "No spell is waiting to cancel.");
        }

        var id = _pendingCast.Id;
        _pendingCast = null;
        var message = $"Pending cast {id} dissipates.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            "cancel_cast",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private ActionResult ProtectItem(string item, bool protectedState)
    {
        var turn = Engine.State.Turn;
        var actor = Engine.State.ControlledEntity;
        if (!actor.TryGet<InventoryComponent>(out var inventory)
            || !inventory.Items.ContainsKey(item))
        {
            return ActionResult.Simple(
                protectedState ? "protect" : "unprotect",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                $"You are not carrying {item}.");
        }

        if (protectedState)
        {
            inventory.TreasuredItems.Add(item);
        }
        else
        {
            inventory.TreasuredItems.Remove(item);
        }

        var message = protectedState
            ? $"{item} is protected from wild magic costs."
            : $"{item} is available as ordinary spell fuel.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            protectedState ? "protect" : "unprotect",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private ActionResult Help() =>
        ActionResult.Simple(
            "help",
            success: true,
            consumedTurn: false,
            Engine.State.Turn,
            Engine.State.Turn,
                "Commands: inspect, map, travel, atlas, move, wait, target, pickup, drop, use, equip, focus, open, read, examine, talk, give, recruit, bonds, possess, cast, begin_cast, await_cast, cancel_cast, protect, unprotect, reagents, wares, buy, sell, journal, character, standing, followers, jobs, save, load, quit.");

    private async Task<ActionResult> SaveGameAsync(string path)
    {
        var turn = Engine.State.Turn;
        var result = await FlushPendingClaimExtractionsAsync(new ActionResult
        {
            Action = "save",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
        });

        try
        {
            GameSaveService.Save(path, Engine.State, PendingCastToSave(), _pendingCastSerial);
            return result with
            {
                Messages = result.Messages.Concat(new[] { $"Saved run to {path}." }).ToArray(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return result with
            {
                Success = false,
                TechnicalFailure = true,
                Messages = result.Messages.Concat(new[] { $"Save failed: {ex.Message}" }).ToArray(),
            };
        }
    }

    private ActionResult LoadGame(string path)
    {
        var turnBefore = Engine.State.Turn;
        try
        {
            var loaded = GameSaveService.Load(path);
            Engine = new GameEngine(loaded.State);
            _pendingCastSerial = loaded.PendingCastSerial;
            _pendingCast = loaded.PendingCast is null
                ? null
                : new PendingCast(
                    loaded.PendingCast.Id,
                    new CastCommand(loaded.PendingCast.Text, loaded.PendingCast.Performance ?? CastPerformance.Neutral));
            _pendingClaimExtractions.Clear();
            return ActionResult.Simple(
                "load",
                success: true,
                consumedTurn: false,
                turnBefore,
                Engine.State.Turn,
                $"Loaded run from {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or JsonException)
        {
            return new ActionResult
            {
                Action = "load",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = Engine.State.Turn,
                TechnicalFailure = true,
                Messages = new[] { $"Load failed: {ex.Message}" },
            };
        }
    }

    private ActionResult AddActorTurns(ActionResult result)
    {
        if (!result.ConsumedTurn || result.ShouldQuit)
        {
            return result;
        }

        var deltas = Engine.RunActorTurns();
        if (deltas.Count == 0)
        {
            return result;
        }

        return result with
        {
            Messages = result.Messages.Concat(deltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = result.Deltas.Concat(deltas).ToArray(),
        };
    }

    private async Task<ActionResult> TalkAsync(
        TalkCommand command,
        CancellationToken cancellationToken)
    {
        if (_dialogueProvider is NullDialogueProvider)
        {
            return Engine.Talk(command.Text);
        }

        var preparation = Engine.PrepareDialogue(command.Text);
        if (preparation.ImmediateResult is not null || preparation.Turn is null)
        {
            return preparation.ImmediateResult
                ?? ActionResult.Simple(
                    "talk",
                    success: false,
                    consumedTurn: false,
                    Engine.State.Turn,
                    Engine.State.Turn,
                    "No one nearby is ready to talk.");
        }

        var request = BuildDialogueRequest(preparation.Turn);
        DialogueProviderResult providerResult;
        try
        {
            providerResult = await _dialogueProvider.ResolveAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            providerResult = new DialogueProviderResult(
                _dialogueProvider.Name,
                "",
                TechnicalFailure: true,
                Error: ex.Message,
                Response: null);
            var failure = DialogueTechnicalFailure(
                preparation.Turn,
                _dialogueProvider.Name,
                "",
                ex.Message);
            RecordDialogueAudit(request, providerResult, failure, new[] { ex.Message });
            return failure;
        }

        if (providerResult.TechnicalFailure || providerResult.Response is null)
        {
            var failure = DialogueTechnicalFailure(
                preparation.Turn,
                providerResult.Provider,
                providerResult.RawText,
                providerResult.Error ?? "Dialogue provider failed.");
            RecordDialogueAudit(request, providerResult, failure, new[] { providerResult.Error ?? "Dialogue provider failed." });
            return failure;
        }

        var normalized = NormalizeDialogueResponse(request, providerResult.Response, out var validationError);
        if (normalized is null)
        {
            var failure = DialogueTechnicalFailure(
                preparation.Turn,
                providerResult.Provider,
                providerResult.RawText,
                validationError ?? "Dialogue provider returned invalid speech.");
            RecordDialogueAudit(request, providerResult, failure, new[] { validationError ?? "invalid_dialogue" });
            return failure;
        }

        var result = Engine.ApplyGeneratedDialogue(
            preparation.Turn,
            normalized.SpokenText,
            providerResult.Provider,
            providerResult.RawText,
            normalized.Delivery,
            normalized.Intent);
        result = ApplyGeneratedDialogueProposals(
            result,
            preparation.Turn,
            request,
            providerResult.Provider,
            normalized);
        RecordDialogueAudit(request, providerResult, result, Array.Empty<string>());
        return result;
    }

    private DialogueRequest BuildDialogueRequest(PreparedDialogueTurn turn)
    {
        var speaker = Engine.EntityById(turn.SpeakerId);
        var listener = Engine.State.ControlledEntity;
        return new DialogueRequest(
            turn.TurnBefore,
            turn.PlayerText,
            ParticipantCard(speaker, turn.BondSummary),
            ParticipantCard(listener, null),
            new DialogueSceneCard(
                Engine.State.RegionId,
                Engine.State.CurrentZoneId,
                VisibleEntityLines().ToArray(),
                NearbyItemLines().ToArray(),
                Engine.State.Messages.TakeLast(6).ToArray()),
            RecentDialogueMemoryLines(turn.SpeakerId).ToArray(),
            Engine.State.Claims.Records
                .TakeLast(8)
                .Select(claim => $"{claim.Subject} [{claim.Category}/{claim.Status}]: {claim.Text}")
                .ToArray(),
            DialogueCapabilityCards());
    }

    private DialogueParticipantCard ParticipantCard(Entity? entity, string? bondSummary)
    {
        if (entity is null)
        {
            return new DialogueParticipantCard(
                "missing",
                "missing",
                Array.Empty<string>(),
                BondSummary: bondSummary);
        }

        var tags = TagsFor(entity).ToArray();
        var faction = entity.TryGet<ActorComponent>(out var actor) ? actor.Faction : null;
        var profile = entity.TryGet<ProfileComponent>(out var profileComponent)
            ? string.Join(
                " ",
                new[]
                {
                    profileComponent.PublicName,
                    profileComponent.Appearance,
                    profileComponent.Origin,
                    profileComponent.MagicalSignature,
                    profileComponent.Backstory,
                }.Where(text => !string.IsNullOrWhiteSpace(text)))
            : null;
        var description = entity.TryGet<DescriptionComponent>(out var descriptionComponent)
            ? descriptionComponent.Text
            : null;
        var inventory = entity.TryGet<InventoryComponent>(out var inventoryComponent)
            ? inventoryComponent.Items
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key} x{pair.Value}")
                .ToArray()
            : Array.Empty<string>();
        var wares = entity.TryGet<MerchantComponent>(out var merchant)
            ? merchant.Wares
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key} x{pair.Value}")
                .ToArray()
            : Array.Empty<string>();
        return new DialogueParticipantCard(
            entity.Id.Value,
            entity.Name,
            tags,
            faction,
            profile,
            description,
            bondSummary,
            inventory,
            wares);
    }

    private IEnumerable<string> VisibleEntityLines()
    {
        var controlled = Engine.State.ControlledEntity;
        var origin = controlled.TryGet<PositionComponent>(out var controlledPosition)
            ? controlledPosition.Position
            : new GridPoint(0, 0);
        return Engine.State.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Select(entity =>
            {
                var position = entity.Get<PositionComponent>().Position;
                var tags = TagsFor(entity);
                var distance = Math.Max(Math.Abs(position.X - origin.X), Math.Abs(position.Y - origin.Y));
                return $"{entity.Name} ({entity.Id.Value}) at {position.X},{position.Y}, range {distance}, tags {string.Join(",", tags)}";
            });
    }

    private IEnumerable<string> NearbyItemLines()
    {
        var controlled = Engine.State.ControlledEntity;
        if (!controlled.TryGet<PositionComponent>(out var controlledPosition))
        {
            return Array.Empty<string>();
        }

        return Engine.State.Entities.Values
            .Where(entity => entity.Has<ItemComponent>() && entity.TryGet<PositionComponent>(out _))
            .Select(entity => new
            {
                Entity = entity,
                Position = entity.Get<PositionComponent>().Position,
            })
            .Where(item => Math.Max(
                Math.Abs(item.Position.X - controlledPosition.Position.X),
                Math.Abs(item.Position.Y - controlledPosition.Position.Y)) <= 4)
            .OrderBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(item => $"{item.Entity.Name} ({item.Entity.Id.Value}) at {item.Position.X},{item.Position.Y}");
    }

    private IEnumerable<string> RecentDialogueMemoryLines(string speakerId)
    {
        foreach (var memory in RecentMemoriesFor(speakerId))
        {
            yield return $"{memory.SubjectId} [{memory.Provenance}, salience {memory.Salience}]: {memory.Text}";
        }

        var speaker = Engine.EntityById(speakerId);
        if (speaker is null || !speaker.TryGet<MemoryComponent>(out var entityMemory))
        {
            yield break;
        }

        foreach (var memory in entityMemory.Records.TakeLast(8))
        {
            yield return $"{speakerId} [{memory.Provenance}, salience {memory.Salience}]: {memory.Text}";
        }
    }

    private static IReadOnlyList<string> DialogueCapabilityCards() =>
        new[]
        {
            "spokenText: 1-4 short sentences spoken by the NPC only; no narration or markdown.",
            "claims: NPC-authored reported claims about places, people, items, threats, landmarks, stock, or events. Player-spoken claims are not binding.",
            "promise binding: bind major actionable NPC claims as promises when they name a useful later place, person, landmark, item location, merchant stock, service, threat, escape route, or prophecy.",
            "always bind concrete useful NPC claims about named/role-specific people, route landmarks, hidden exits, item locations, direct services, concrete trades, future patrols, door rules, and omens with triggers.",
            "bind actionable salience 3+ categories by default: site, town, landmark, person, item, merchant_stock, service, trade, threat, escape_route, prophecy, and door_rule.",
            "claims must be plainly supported by spokenText; do not invent useful places, people, items, or threats the NPC did not actually say.",
            "do not bind denials, jokes, child-invented monsters, tiny ambience, ordinary weather, vague mood, insults, impossible boasts, or claims only the player authored.",
            "memories: durable memories for the speaker or listener when this exchange should be remembered later.",
            "bond: null or an object with entityId, integer loyaltyDelta, fearDelta, admirationDelta, resentmentDelta, posture, and reason.",
            "actions: array of objects like {\"type\":\"none\"}, {\"type\":\"step_aside\"}, {\"type\":\"give_item\",\"itemName\":\"brass key\"}, or {\"type\":\"open_door\",\"targetEntityId\":\"cell_door_1\"}.",
        };

    private ActionResult ApplyGeneratedDialogueProposals(
        ActionResult result,
        PreparedDialogueTurn turn,
        DialogueRequest request,
        string provider,
        DialogueResponse response)
    {
        var proposals = response.Proposals;
        if (proposals is null)
        {
            AddDialogueExchangeMemory(turn, response.SpokenText);
            return result;
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        var claimRequest = new DialogueClaimRequest(
            Engine.State.Turn,
            Engine.State.RegionId,
            Engine.State.CurrentZoneId,
            turn.SpeakerId,
            turn.SpeakerName,
            turn.SpeakerTags,
            turn.ListenerSoulId,
            turn.PlayerText,
            new[] { response.SpokenText },
            RecentMemoriesFor(turn.SpeakerId),
            Engine.State.Claims.Records.TakeLast(8).ToArray());

        foreach (var claim in proposals.Claims ?? Array.Empty<DialogueClaimProposal>())
        {
            ApplyClaimProposal(claimRequest, provider, claim, messages, deltas);
        }

        foreach (var memory in proposals.Memories ?? Array.Empty<DialogueMemoryProposal>())
        {
            ApplyDialogueMemoryProposal(provider, memory, messages, deltas);
        }

        if (proposals.Bond is not null)
        {
            ApplyDialogueBondProposal(provider, proposals.Bond, messages, deltas);
        }

        foreach (var action in proposals.Actions ?? Array.Empty<DialogueActionProposal>())
        {
            ApplyDialogueActionProposal(provider, turn, response.Intent, action, messages, deltas);
        }

        AddDialogueExchangeMemory(turn, response.SpokenText);
        if (messages.Count == 0 && deltas.Count == 0)
        {
            return result;
        }

        return result with
        {
            Messages = result.Messages.Concat(messages).ToArray(),
            Deltas = result.Deltas.Concat(deltas).ToArray(),
        };
    }

    private void ApplyDialogueMemoryProposal(
        string provider,
        DialogueMemoryProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        if (string.IsNullOrWhiteSpace(proposal.Text))
        {
            return;
        }

        var ownerId = string.IsNullOrWhiteSpace(proposal.OwnerEntityId)
            ? Engine.State.ControlledEntityId.Value
            : proposal.OwnerEntityId.Trim();
        var salience = Math.Clamp(proposal.Salience, 1, 5);
        Engine.State.Memories.Append(
            ownerId,
            proposal.Text.Trim(),
            string.IsNullOrWhiteSpace(proposal.Provenance) ? $"dialogue:{provider}" : proposal.Provenance.Trim(),
            salience,
            proposal.Shareable);
        var owner = Engine.EntityById(ownerId);
        if (owner is not null)
        {
            var memories = owner.TryGet<MemoryComponent>(out var existing)
                ? existing.Records.ToList()
                : new List<EntityMemoryRecord>();
            memories.Add(new EntityMemoryRecord(
                $"dialogue_memory_{Engine.State.Turn}_{memories.Count + 1}",
                proposal.Text.Trim(),
                $"dialogue:{provider}",
                string.IsNullOrWhiteSpace(proposal.Provenance) ? "conversation" : proposal.Provenance.Trim(),
                salience,
                proposal.Shareable));
            owner.Set(new MemoryComponent(memories));
        }

        deltas.Add(new StateDelta(
            "dialogueMemory",
            ownerId,
            $"Dialogue memory recorded: {proposal.Text.Trim()}",
            new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["salience"] = salience,
                ["provenance"] = proposal.Provenance,
            }));
    }

    private void ApplyDialogueBondProposal(
        string provider,
        DialogueBondProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        ApplyDialogueBondShift(
            provider,
            "dialogueBondShift",
            proposal.EntityId,
            SoulIdFor(Engine.State.ControlledEntity),
            proposal.LoyaltyDelta,
            proposal.FearDelta,
            proposal.AdmirationDelta,
            proposal.ResentmentDelta,
            proposal.Posture,
            proposal.Reason,
            playerVisible: true,
            messages,
            deltas);
    }

    private void ApplyDialogueActionProposal(
        string provider,
        PreparedDialogueTurn turn,
        string? intent,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var type = NormalizeToken(proposal.Type, "none");
        if (type == "none")
        {
            return;
        }

        if (IsRefusalIntent(intent) && IsCooperativeDialogueAction(type))
        {
            RejectDialogueAction(provider, proposal, "The NPC refused, so cooperative action was not applied.", deltas);
            return;
        }

        switch (type)
        {
            case "step_aside":
                ApplyDialogueMoveAction(provider, turn.SpeakerId, flee: false, messages, deltas);
                return;
            case "flee":
                ApplyDialogueMoveAction(provider, turn.SpeakerId, flee: true, messages, deltas);
                return;
            case "open_door":
                ApplyDialogueOpenDoor(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "give_item":
                ApplyDialogueGiveItem(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "call_help":
                ApplyDialogueCallHelp(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
        }

        RejectDialogueAction(provider, proposal, "Dialogue action handlers are not implemented for this action type yet.", deltas);
    }

    private BondRecord? ApplyDialogueBondShift(
        string provider,
        string operation,
        string entityId,
        string targetSoulId,
        int loyaltyDelta,
        int fearDelta,
        int admirationDelta,
        int resentmentDelta,
        string? posture,
        string? reason,
        bool playerVisible,
        List<string> messages,
        List<StateDelta> deltas,
        IReadOnlyDictionary<string, object?>? extraDetails = null)
    {
        var entity = Engine.EntityById(entityId);
        if (entity is null)
        {
            var skippedDetails = new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "bond",
                ["operation"] = operation,
            };
            if (extraDetails is not null)
            {
                foreach (var detail in extraDetails)
                {
                    skippedDetails[detail.Key] = detail.Value;
                }
            }

            deltas.Add(new StateDelta(
                "dialogueProposalSkipped",
                entityId,
                "Dialogue bond proposal skipped because the entity no longer exists.",
                skippedDetails));
            return null;
        }

        var bond = Engine.State.Bonds.Adjust(
            SoulIdFor(entity),
            targetSoulId,
            ClampDialogueBondDelta(loyaltyDelta),
            ClampDialogueBondDelta(fearDelta),
            ClampDialogueBondDelta(admirationDelta),
            ClampDialogueBondDelta(resentmentDelta),
            string.IsNullOrWhiteSpace(posture) ? null : posture.Trim());
        var message = $"{entity.Name}'s posture shifts: {BondSummary(bond)}.";
        if (playerVisible)
        {
            AddVisibleClaimMessage(message, messages);
        }

        var details = new Dictionary<string, object?>
        {
            ["provider"] = provider,
            ["loyalty"] = bond.Loyalty,
            ["fear"] = bond.Fear,
            ["admiration"] = bond.Admiration,
            ["resentment"] = bond.Resentment,
            ["posture"] = bond.Posture,
            ["reason"] = reason,
        };
        if (extraDetails is not null)
        {
            foreach (var detail in extraDetails)
            {
                details[detail.Key] = detail.Value;
            }
        }

        deltas.Add(new StateDelta(
            operation,
            entity.Id.Value,
            message,
            details));
        return bond;
    }

    private static int ClampDialogueBondDelta(int delta) =>
        Math.Clamp(delta, -DialogueBondDeltaLimit, DialogueBondDeltaLimit);

    private static bool IsRefusalIntent(string? intent) =>
        NormalizeToken(intent ?? "", "").Equals("refuse", StringComparison.OrdinalIgnoreCase);

    private static bool IsCooperativeDialogueAction(string type) =>
        type is "step_aside" or "give_item" or "open_door";

    private void ApplyDialogueMoveAction(
        string provider,
        string actorId,
        bool flee,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var actor = Engine.EntityById(actorId);
        if (actor is null || !actor.TryGet<PositionComponent>(out var position))
        {
            deltas.Add(new StateDelta(
                "dialogueActionSkipped",
                actorId,
                "Dialogue move action skipped because the actor is missing or has no position.",
                new Dictionary<string, object?>
                {
                    ["provider"] = provider,
                    ["type"] = flee ? "flee" : "step_aside",
                }));
            return;
        }

        var playerPosition = Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var destination = DialogueMoveDestination(position.Position, playerPosition, flee);
        if (destination is null)
        {
            RejectDialogueAction(
                provider,
                new DialogueActionProposal(flee ? "flee" : "step_aside", actorId),
                "No open adjacent tile was available.",
                deltas);
            return;
        }

        var delta = Engine.MoveEntity(actor, destination.Value, flee ? "dialogueFlee" : "dialogueStepAside");
        messages.Add(delta.Summary);
        deltas.Add(delta);
    }

    private GridPoint? DialogueMoveDestination(GridPoint origin, GridPoint awayFrom, bool flee)
    {
        var currentDistance = GameEngine.Distance(origin, awayFrom);
        var candidates = AdjacentOffsets()
            .Select(offset => origin.Translate(offset.X, offset.Y))
            .Where(point => Engine.InBounds(point)
                && !Engine.State.BlockingTerrain.Contains(point)
                && Engine.BlockingEntityAt(point) is null)
            .Select(point => new
            {
                Point = point,
                Distance = GameEngine.Distance(point, awayFrom),
            })
            .OrderByDescending(item => item.Distance)
            .ThenBy(item => item.Point.X)
            .ThenBy(item => item.Point.Y)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        if (flee)
        {
            return candidates[0].Point;
        }

        return candidates.FirstOrDefault(item => item.Distance > currentDistance)?.Point
            ?? candidates[0].Point;
    }

    private void ApplyDialogueOpenDoor(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var actor = Engine.EntityById(actorId);
        var door = ResolveDialogueDoor(actor, proposal.TargetEntityId);
        if (actor is null || door is null)
        {
            RejectDialogueAction(provider, proposal, "No reachable door was found.", deltas);
            return;
        }

        var result = Engine.OpenDoor(
            actor,
            door,
            WorldActionContext.Dialogue(provider, "dialogue_action", "dialogueOpenDoor"));
        if (!result.Success)
        {
            RejectDialogueAction(provider, proposal, string.Join(" ", result.Messages), deltas);
            return;
        }

        messages.AddRange(result.Messages);
        deltas.AddRange(result.Deltas);
    }

    private Entity? ResolveDialogueDoor(Entity? actor, string? targetEntityId)
    {
        if (!string.IsNullOrWhiteSpace(targetEntityId)
            && Engine.EntityById(targetEntityId) is { } target)
        {
            return target;
        }

        if (actor is null || !actor.TryGet<PositionComponent>(out var actorPosition))
        {
            return null;
        }

        return Engine.State.Entities.Values
            .Where(entity => entity.Has<DoorComponent>() && entity.TryGet<PositionComponent>(out _))
            .Where(entity => GameEngine.Distance(actorPosition.Position, entity.Get<PositionComponent>().Position) <= 1)
            .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private void ApplyDialogueGiveItem(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var giver = Engine.EntityById(actorId);
        if (giver is null
            || !giver.TryGet<InventoryComponent>(out var giverInventory))
        {
            RejectDialogueAction(provider, proposal, "The speaker has no inventory to give from.", deltas);
            return;
        }

        var item = FindInventoryKey(giverInventory, proposal.ItemName ?? "");
        if (item is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker does not have the proposed item.", deltas);
            return;
        }

        var receiver = string.IsNullOrWhiteSpace(proposal.TargetEntityId)
            ? Engine.State.ControlledEntity
            : Engine.EntityById(proposal.TargetEntityId) ?? Engine.State.ControlledEntity;
        var receiverInventory = receiver.TryGet<InventoryComponent>(out var existingReceiverInventory)
            ? existingReceiverInventory
            : InventoryComponent.Empty();
        receiver.Set(receiverInventory);
        ChangeInventory(giverInventory, item, -1);
        ChangeInventory(receiverInventory, item, 1);

        var message = $"{giver.Name} gives {item} to {receiver.Name}.";
        messages.Add(message);
        Engine.AddMessage(message);
        deltas.Add(new StateDelta(
            "dialogueGiveItem",
            receiver.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["speakerId"] = giver.Id.Value,
                ["item"] = item,
                ["receiverId"] = receiver.Id.Value,
            }));
    }

    private void ApplyDialogueCallHelp(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var actor = Engine.EntityById(actorId);
        if (actor is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        var imperial = actor.TryGet<ActorComponent>(out var actorComponent)
            && actorComponent.Faction.Equals("empire", StringComparison.OrdinalIgnoreCase)
            || TagsFor(actor).Any(tag => tag.Equals("imperial", StringComparison.OrdinalIgnoreCase));
        var kind = imperial ? "empire_patrol" : "dialogue_help_call";
        var text = imperial
            ? $"{actor.Name}'s call for help reaches an imperial ear."
            : $"{actor.Name}'s call for help starts looking for someone to answer.";
        var scheduled = Engine.State.ScheduledEvents.Schedule(
            Engine.State.Turn + 2,
            kind,
            actor.Id,
            new Dictionary<string, object?>
            {
                ["text"] = text,
                ["source"] = "dialogue",
                ["provider"] = provider,
                ["reason"] = proposal.Reason,
            });
        var message = $"{actor.Name} calls for help.";
        messages.Add(message);
        Engine.AddMessage(message);
        deltas.Add(new StateDelta(
            "dialogueCallHelp",
            actor.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["eventId"] = scheduled.Id,
                ["dueTurn"] = scheduled.DueTurn,
                ["kind"] = scheduled.Kind,
            }));
    }

    private static void ChangeInventory(InventoryComponent inventory, string item, int delta)
    {
        inventory.Items.TryGetValue(item, out var current);
        var next = current + delta;
        if (next <= 0)
        {
            inventory.Items.Remove(item);
            inventory.TreasuredItems.Remove(item);
            return;
        }

        inventory.Items[item] = next;
    }

    private static string? FindInventoryKey(InventoryComponent inventory, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        return inventory.Items.Keys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(key =>
                key.Equals(item.Trim(), StringComparison.OrdinalIgnoreCase)
                || key.Contains(item.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private void RejectDialogueAction(
        string provider,
        DialogueActionProposal proposal,
        string reason,
        List<StateDelta> deltas)
    {
        var type = NormalizeToken(proposal.Type, "none");
        deltas.Add(new StateDelta(
            "dialogueActionRejected",
            proposal.TargetEntityId ?? "",
            $"Dialogue action proposal rejected: {type}.",
            new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["type"] = type,
                ["itemName"] = proposal.ItemName,
                ["reason"] = reason,
                ["providerReason"] = proposal.Reason,
            }));
    }

    private static IEnumerable<GridPoint> AdjacentOffsets() =>
        new[]
        {
            new GridPoint(-1, -1),
            new GridPoint(0, -1),
            new GridPoint(1, -1),
            new GridPoint(-1, 0),
            new GridPoint(1, 0),
            new GridPoint(-1, 1),
            new GridPoint(0, 1),
            new GridPoint(1, 1),
        };

    private void AddDialogueExchangeMemory(PreparedDialogueTurn turn, string spokenText)
    {
        var speaker = Engine.EntityById(turn.SpeakerId);
        if (speaker is null)
        {
            return;
        }

        var text = $"{turn.SpeakerName} spoke with the sorcerer. Player: {turn.PlayerText} Reply: {spokenText}";
        var memories = speaker.TryGet<MemoryComponent>(out var existing)
            ? existing.Records.ToList()
            : new List<EntityMemoryRecord>();
        memories.Add(new EntityMemoryRecord(
            $"dialogue_exchange_{Engine.State.Turn}_{memories.Count + 1}",
            text,
            "dialogue_exchange",
            "conversation",
            2,
            Shareable: false));
        speaker.Set(new MemoryComponent(memories.TakeLast(24).ToArray()));
    }

    private DialogueResponse? NormalizeDialogueResponse(
        DialogueRequest request,
        DialogueResponse response,
        out string? error)
    {
        error = null;
        var spoken = (response.SpokenText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(spoken))
        {
            error = "Dialogue provider returned empty spokenText.";
            return null;
        }

        if (spoken.Length > 700)
        {
            spoken = spoken[..700].TrimEnd();
        }

        if (LooksLikeJson(spoken))
        {
            error = "Dialogue provider leaked JSON into spokenText.";
            return null;
        }

        if (IsDegenerateEcho(spoken, request.PlayerText))
        {
            error = "Dialogue provider echoed the player instead of answering.";
            return null;
        }

        return response with
        {
            SpokenText = spoken,
            Delivery = string.IsNullOrWhiteSpace(response.Delivery) ? null : response.Delivery.Trim(),
            Intent = string.IsNullOrWhiteSpace(response.Intent) ? null : response.Intent.Trim(),
        };
    }

    private void RecordDialogueAudit(
        DialogueRequest request,
        DialogueProviderResult providerResult,
        ActionResult result,
        IReadOnlyList<string> validationIssues)
    {
        _dialogueAudit.Record(new DialogueAuditEntry(
            DateTimeOffset.UtcNow,
            providerResult.Provider,
            request.Speaker.EntityId,
            request.Speaker.Name,
            request.PlayerText,
            request,
            providerResult.RawText,
            providerResult.Response,
            providerResult.TechnicalFailure || result.TechnicalFailure,
            providerResult.Error,
            result.Action,
            result.Success,
            result.ConsumedTurn,
            validationIssues,
            result.Deltas.Select(delta => delta.Operation).ToArray()));
    }

    private ActionResult DialogueTechnicalFailure(
        PreparedDialogueTurn turn,
        string provider,
        string rawText,
        string error)
    {
        var message = $"Dialogue provider failed: {error}";
        return new ActionResult
        {
            Action = "talk",
            Success = false,
            ConsumedTurn = false,
            TurnBefore = turn.TurnBefore,
            TurnAfter = Engine.State.Turn,
            TechnicalFailure = true,
            Messages = new[] { message },
            Deltas = new[]
            {
                new StateDelta(
                    "dialogueProviderFailed",
                    turn.SpeakerId,
                    message,
                    new Dictionary<string, object?>
                    {
                        ["provider"] = provider,
                        ["error"] = error,
                        ["rawText"] = rawText,
                    }),
            },
        };
    }

    private static bool LooksLikeJson(string text) =>
        text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal);

    private static bool IsDegenerateEcho(string reply, string playerText)
    {
        var normalizedReply = NormalizeDialogueComparison(reply);
        var normalizedPlayer = NormalizeDialogueComparison(playerText);
        return normalizedReply.Length > 0
            && normalizedPlayer.Length > 0
            && (normalizedReply.Equals(normalizedPlayer, StringComparison.OrdinalIgnoreCase)
                || (normalizedReply.Length >= 24
                    && normalizedPlayer.Contains(normalizedReply, StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeDialogueComparison(string text) =>
        new(text
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private void QueueDialogueClaimExtraction(TalkCommand command, ActionResult result)
    {
        if (_claimExtractor is NullDialogueClaimExtractor)
        {
            return;
        }

        var dialogue = result.Deltas.LastOrDefault(delta =>
            delta.Operation.Equals("dialogue", StringComparison.OrdinalIgnoreCase));
        if (dialogue is null)
        {
            return;
        }

        if (dialogue.Details.TryGetValue("generated", out var generated)
            && generated is bool generatedBool
            && generatedBool)
        {
            return;
        }

        var speaker = Engine.EntityById(dialogue.Target);
        var request = new DialogueClaimRequest(
            Engine.State.Turn,
            Engine.State.RegionId,
            Engine.State.CurrentZoneId,
            ReadString(dialogue.Details, "speakerId") ?? dialogue.Target,
            ReadString(dialogue.Details, "speakerName") ?? speaker?.Name ?? dialogue.Target,
            ReadStringList(dialogue.Details, "speakerTags")
                ?? (speaker is null ? Array.Empty<string>() : TagsFor(speaker)),
            ReadString(dialogue.Details, "listenerSoulId") ?? SoulIdFor(Engine.State.ControlledEntity),
            ReadString(dialogue.Details, "playerText") ?? command.Text,
            ReadStringList(dialogue.Details, "lines") ?? result.Messages.ToArray(),
            RecentMemoriesFor(dialogue.Target),
            Engine.State.Claims.Records.TakeLast(8).ToArray());

        var task = _claimExtractor.ExtractAsync(request, CancellationToken.None);
        _pendingClaimExtractions.Add(new PendingClaimExtraction(request, task));
    }

    private async Task<ActionResult> FlushPendingClaimExtractionsAsync(ActionResult result)
    {
        if (_pendingClaimExtractions.Count == 0)
        {
            return result;
        }

        var pendingCount = _pendingClaimExtractions.Count;
        try
        {
            await Task.WhenAll(_pendingClaimExtractions.Take(pendingCount).Select(pending => pending.Task));
        }
        catch
        {
            // Faulted and canceled extraction tasks are converted to explicit deltas below.
        }

        return ApplyCompletedClaimExtractions(result, pendingCount);
    }

    private ActionResult ApplyCompletedClaimExtractions(ActionResult result, int maxExclusive)
    {
        if (maxExclusive <= 0 || _pendingClaimExtractions.Count == 0)
        {
            return result;
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        var completed = _pendingClaimExtractions
            .Take(maxExclusive)
            .Where(pending => pending.Task.IsCompleted)
            .ToArray();
        foreach (var pending in completed)
        {
            _pendingClaimExtractions.Remove(pending);
            if (pending.Task.IsFaulted)
            {
                var error = pending.Task.Exception?.GetBaseException().Message ?? "unknown claim extraction error";
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    pending.Request.SpeakerId,
                    $"Dialogue claim extraction failed: {error}",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = _claimExtractor.Name,
                        ["error"] = error,
                    }));
                continue;
            }

            if (pending.Task.IsCanceled)
            {
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    pending.Request.SpeakerId,
                    "Dialogue claim extraction was canceled.",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = _claimExtractor.Name,
                        ["error"] = "canceled",
                    }));
                continue;
            }

            var extraction = pending.Task.Result;
            if (extraction.TechnicalFailure)
            {
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    pending.Request.SpeakerId,
                    $"Dialogue claim extraction failed: {extraction.Error ?? "unknown error"}",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = extraction.Provider,
                        ["error"] = extraction.Error,
                    }));
                continue;
            }

            foreach (var claim in extraction.Claims)
            {
                ApplyClaimProposal(pending.Request, extraction.Provider, claim, messages, deltas);
            }
        }

        if (messages.Count == 0 && deltas.Count == 0)
        {
            return result;
        }

        return result with
        {
            Messages = result.Messages.Concat(messages).ToArray(),
            Deltas = result.Deltas.Concat(deltas).ToArray(),
        };
    }

    private void ApplyClaimProposal(
        DialogueClaimRequest request,
        string provider,
        DialogueClaimProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        if (proposal.PlayerAuthored || string.IsNullOrWhiteSpace(proposal.Text))
        {
            return;
        }

        var salience = Math.Clamp(proposal.Salience, 1, 5);
        var confidence = Math.Clamp(proposal.Confidence, 0, 100);
        var category = NormalizeToken(proposal.Category, "memory");
        var subject = string.IsNullOrWhiteSpace(proposal.Subject)
            ? proposal.Text.Trim()
            : proposal.Subject.Trim();
        var playerVisible = salience >= VisibleClaimSalience;
        var tags = (proposal.Tags ?? Array.Empty<string>())
            .Concat(new[] { "dialogue", category })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var record = Engine.State.Claims.Append(
            Engine.State.Turn,
            $"dialogue:{provider}",
            request.SpeakerId,
            request.ListenerSoulId,
            proposal.Text,
            category,
            subject,
            salience,
            confidence,
            playerVisible,
            tags);
        deltas.Add(new StateDelta(
            "claimRecorded",
            record.Id,
            $"A reported claim is recorded: {record.Text}",
            new Dictionary<string, object?>
            {
                ["claimId"] = record.Id,
                ["speakerId"] = record.SpeakerId,
                ["category"] = record.Category,
                ["salience"] = record.Salience,
                ["confidence"] = record.Confidence,
                ["playerVisible"] = record.PlayerVisible,
            }));

        AddClaimMemory(request, record);

        if (proposal.UpdateBond)
        {
            ApplyBondClaim(request, provider, proposal, record, messages, deltas);
        }

        if (category.Equals("merchant_stock", StringComparison.OrdinalIgnoreCase))
        {
            ApplyMerchantStockClaim(request, proposal, record, messages, deltas);
        }

        if (proposal.BindAsPromise)
        {
            BindClaimAsPromise(request, proposal, record, messages, deltas);
        }
        else if (playerVisible)
        {
            AddVisibleClaimMessage($"A claim settles into your journal: {record.Text}", messages);
        }
    }

    private void ApplyBondClaim(
        DialogueClaimRequest request,
        string provider,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var bond = ApplyDialogueBondShift(
            provider,
            "claimBondShift",
            request.SpeakerId,
            request.ListenerSoulId,
            proposal.LoyaltyDelta,
            proposal.FearDelta,
            proposal.AdmirationDelta,
            proposal.ResentmentDelta,
            proposal.BondPosture,
            null,
            record.PlayerVisible,
            messages,
            deltas,
            new Dictionary<string, object?>
            {
                ["claimId"] = record.Id,
            });
        if (bond is null)
        {
            return;
        }

        Engine.State.Claims.Update(record.Id, status: "applied", appliedTo: request.SpeakerId);
    }

    private void ApplyMerchantStockClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var merchant = ResolveMerchantForClaim(request, proposal);
        if (merchant is null)
        {
            return;
        }

        var itemName = FirstNonBlank(proposal.ItemName, proposal.Subject, record.Subject);
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return;
        }

        var wares = merchant.Get<MerchantComponent>().Wares;
        wares.TryGetValue(itemName, out var current);
        wares[itemName] = current + 1;
        var updated = Engine.State.Claims.Update(record.Id, status: "applied", appliedTo: merchant.Id.Value) ?? record;
        var message = $"{merchant.Name}'s stock now includes {itemName}.";
        deltas.Add(new StateDelta(
            "claimMerchantStock",
            merchant.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["claimId"] = updated.Id,
                ["item"] = itemName,
                ["quantity"] = wares[itemName],
            }));
        if (updated.PlayerVisible)
        {
            AddVisibleClaimMessage(message, messages);
        }
    }

    private void BindClaimAsPromise(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var triggerHint = string.IsNullOrWhiteSpace(proposal.TriggerHint) ? "travel" : proposal.TriggerHint.Trim();
        var realizationKind = NormalizeRealizationKind(proposal.RealizationKind, proposal.Category);
        var existing = MatchingActivePromise(record, proposal, triggerHint, realizationKind);
        if (existing is not null)
        {
            var duplicate = Engine.State.Claims.Update(record.Id, status: "promised", boundPromiseId: existing.Id) ?? record;
            deltas.Add(new StateDelta(
                "claimPromiseLinked",
                existing.Id,
                $"A repeated claim points back to an existing promise: {existing.Text}",
                new Dictionary<string, object?>
                {
                    ["claimId"] = duplicate.Id,
                    ["promiseId"] = existing.Id,
                    ["status"] = existing.Status,
                    ["triggerHint"] = existing.TriggerHint,
                    ["realizationKind"] = existing.RealizationKind,
                }));
            return;
        }

        var promise = Engine.State.PromiseLedger.Add(
            string.IsNullOrWhiteSpace(proposal.PromiseKind) ? "rumor" : proposal.PromiseKind.Trim(),
            record.Text,
            playerVisible: record.PlayerVisible,
            source: $"dialogue_claim:{record.Id}",
            salience: record.Salience,
            subject: record.Subject,
            claimedPlace: string.IsNullOrWhiteSpace(proposal.ClaimedPlace) ? Engine.State.RegionId : proposal.ClaimedPlace,
            triggerHint: triggerHint,
            realizationKind: realizationKind);
        var bound = ShouldBindToRegion(triggerHint, realizationKind)
            ? Engine.State.PromiseLedger.Bind(promise.Id, Engine.State.RegionId, null, triggerHint, realizationKind) ?? promise
            : promise;
        var updated = Engine.State.Claims.Update(record.Id, status: "promised", boundPromiseId: bound.Id) ?? record;
        var message = bound.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            ? $"A claim becomes a bound promise: {bound.Text}"
            : $"A claim becomes a promise: {bound.Text}";
        deltas.Add(new StateDelta(
            "claimPromise",
            bound.Id,
            message,
            new Dictionary<string, object?>
            {
                ["claimId"] = updated.Id,
                ["promiseId"] = bound.Id,
                ["status"] = bound.Status,
                ["triggerHint"] = bound.TriggerHint,
                ["realizationKind"] = bound.RealizationKind,
            }));
        if (updated.PlayerVisible)
        {
            AddVisibleClaimMessage(message, messages);
        }
    }

    private WorldPromise? MatchingActivePromise(
        ClaimRecord record,
        DialogueClaimProposal proposal,
        string triggerHint,
        string realizationKind) =>
        Engine.State.PromiseLedger.Promises.FirstOrDefault(promise =>
            !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
            && !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
            && promise.Text.Equals(record.Text, StringComparison.OrdinalIgnoreCase)
            && NormalizeToken(promise.RealizationKind ?? "", realizationKind).Equals(realizationKind, StringComparison.OrdinalIgnoreCase)
            && TriggerMatches(promise.TriggerHint, triggerHint)
            && (string.IsNullOrWhiteSpace(proposal.PromiseKind)
                || promise.Kind.Equals(proposal.PromiseKind, StringComparison.OrdinalIgnoreCase)));

    private void AddClaimMemory(DialogueClaimRequest request, ClaimRecord record)
    {
        Engine.State.Memories.Append(
            request.SpeakerId,
            record.Text,
            $"claim:{record.Id}",
            record.Salience,
            shareable: true);
        var speaker = Engine.EntityById(request.SpeakerId);
        if (speaker is null)
        {
            return;
        }

        var existing = speaker.TryGet<MemoryComponent>(out var memory)
            ? memory.Records.ToList()
            : new List<EntityMemoryRecord>();
        existing.Add(new EntityMemoryRecord(
            $"memory_{record.Id}",
            record.Text,
            record.Id,
            "dialogue_claim",
            record.Salience,
            Shareable: true));
        speaker.Set(new MemoryComponent(existing));
    }

    private Entity? ResolveMerchantForClaim(DialogueClaimRequest request, DialogueClaimProposal proposal)
    {
        foreach (var id in new[] { proposal.MerchantId, proposal.TargetEntityId, request.SpeakerId })
        {
            if (!string.IsNullOrWhiteSpace(id)
                && Engine.EntityById(id) is { } merchant
                && merchant.Has<MerchantComponent>())
            {
                return merchant;
            }
        }

        return Engine.State.Entities.Values
            .OrderBy(entity => entity.Id.Value)
            .FirstOrDefault(entity => entity.Has<MerchantComponent>());
    }

    private IReadOnlyList<WorldMemoryRecord> RecentMemoriesFor(string speakerId) =>
        Engine.State.Memories.Records
            .Where(record => record.SubjectId.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                || record.Provenance.StartsWith("gift", StringComparison.OrdinalIgnoreCase)
                || record.Provenance.StartsWith("claim:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(8)
            .ToArray();

    private void AddVisibleClaimMessage(string message, List<string> messages)
    {
        Engine.State.AddMessage(message);
        messages.Add(message);
    }

    private static bool ShouldBindToRegion(string? triggerHint, string? realizationKind) =>
        TriggerMatches(triggerHint, "travel")
        && NormalizeToken(realizationKind ?? "", "site") is "site" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade";

    private static bool TriggerMatches(string? hint, string trigger) =>
        string.IsNullOrWhiteSpace(hint)
        || hint.Equals(trigger, StringComparison.OrdinalIgnoreCase)
        || hint.Equals("encounter", StringComparison.OrdinalIgnoreCase)
        || hint.Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals(trigger, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeRealizationKind(string? realizationKind, string category)
    {
        var normalized = NormalizeToken(realizationKind ?? category, "memory");
        return normalized switch
        {
            "place" or "site" or "town" or "landmark" => "site",
            "merchant_stock" or "stock" or "ware" or "wares" or "trade" => "merchant_stock",
            "npc" or "person" or "relative" => "person",
            "enemy" or "danger" or "threat" => "threat",
            "item" or "blade" or "weapon" => "item",
            _ => normalized,
        };
    }

    private static string NormalizeToken(string text, string fallback)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ReadString(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value switch
        {
            string text => text,
            _ => value?.ToString(),
        } : null;

    private static IReadOnlyList<string>? ReadStringList(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            string text => new[] { text },
            IEnumerable<string> strings => strings.ToArray(),
            IEnumerable<object> objects => objects.Select(item => item?.ToString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToArray(),
            _ => null,
        };
    }

    private static IReadOnlyList<string> TagsFor(Entity entity) =>
        entity.TryGet<TagsComponent>(out var tags) ? tags.Tags : Array.Empty<string>();

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

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

    private ActionResult CompleteRunIfNeeded(ActionResult result)
    {
        if (result.ShouldQuit
            || !Engine.State.RunStatus.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        if (EmperorDefeated())
        {
            return CompleteRun(
                result,
                "victory",
                "Emperor Odran of Vigovia is dead.",
                "emperor_odran",
                "Emperor Odran falls. The marble empire discovers it had a throat.");
        }

        if (ControlledBodyDefeated())
        {
            return CompleteRun(
                result,
                "defeat",
                "The sorcerer's current body is dead.",
                Engine.State.ControlledEntityId.Value,
                "Your body falls. Somewhere, the world begins arranging a stranger's dawn.");
        }

        return result;
    }

    private ActionResult CompleteRun(
        ActionResult result,
        string status,
        string conclusion,
        string target,
        string message)
    {
        Engine.State.RunStatus = status;
        Engine.State.RunConclusion = conclusion;
        Engine.AddMessage(message);
        var chronicle = RunChronicle.Build(Engine.State);
        Engine.State.Canon.Add(
            "chronicle",
            Engine.State.ControlledEntityId.Value,
            chronicle.Text,
            chronicle.Conclusion,
            new[] { "chronicle", status },
            "run_end",
            Engine.State.Turn);
        var delta = new StateDelta(
            "runComplete",
            target,
            message,
            new Dictionary<string, object?>
            {
                ["status"] = status,
                ["conclusion"] = conclusion,
            });
        return result with
        {
            ShouldQuit = true,
            Messages = result.Messages.Concat(new[] { message }).ToArray(),
            Deltas = result.Deltas.Concat(new[] { delta }).ToArray(),
        };
    }

    private bool EmperorDefeated() =>
        Engine.State.Entities.Values.Any(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("emperor", StringComparer.OrdinalIgnoreCase)
            && entity.TryGet<ActorComponent>(out var actor)
            && !actor.Alive);

    private bool ControlledBodyDefeated() =>
        Engine.State.ControlledEntity.TryGet<ActorComponent>(out var actor)
        && !actor.Alive;

    private static bool CanExecuteDuringPendingCast(GameCommand command) =>
        command is AwaitCastCommand
            or CancelCastCommand
            or InspectCommand
            or MapCommand
            or SaveCommand
            or LoadCommand
            or HelpCommand
            or QuitCommand;

    private PendingCastSave? PendingCastToSave() =>
        _pendingCast is null
            ? null
            : new PendingCastSave(
                _pendingCast.Id,
                _pendingCast.Command.Text,
                _pendingCast.Command.Performance);

    private ActionResult Quit() =>
        new()
        {
            Action = "quit",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = Engine.State.Turn,
            TurnAfter = Engine.State.Turn,
            Messages = new[] { "Leaving Sorcerer." },
            ShouldQuit = true,
        };

    private sealed record PendingCast(string Id, CastCommand Command);

    private sealed record PendingClaimExtraction(
        DialogueClaimRequest Request,
        Task<DialogueClaimExtractionResult> Task);
}
