using System.Diagnostics;
using System.Text.Json;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Scenarios;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

public sealed class GameSession
{
    private const int VisibleClaimSalience = 3;
    private const int DialogueBondDeltaLimit = 2;
    private const int DialogueRecentMemoryLimit = 5;
    private const int DialogueRecentClaimLimit = 4;

    private readonly IWildMagicController _magic;
    private readonly IDialogueProvider _dialogueProvider;
    private readonly IDialogueRouter _dialogueRouter;
    private readonly IDialogueAuditSink _dialogueAudit;
    private readonly IDialogueParser _dialogueParser;
    private readonly IDialogueParserRouter _dialogueParserRouter;
    private readonly IBackgroundTextGenerator? _backgroundTextGenerator;
    private readonly List<PendingClaimExtraction> _pendingClaimExtractions = new();
    // Per-speaker conversation history so the speech model can call back and stay consistent
    // (docs/OPTIMIZATION_PLAN.md WS3.4). Session-scoped and intentionally not persisted: it is a
    // latency/continuity aid, not authoritative state — memories/bonds already carry what must
    // survive a save. Keyed by speaker entity id; each entry is a "who: line" pair, newest last.
    private const int RecentDialogueLineCap = 6;
    private const int RecentDialogueLineLength = 240;
    private readonly Dictionary<string, List<string>> _recentDialogueBySpeaker =
        new(StringComparer.OrdinalIgnoreCase);
    private PendingCast? _pendingCast;
    private int _pendingCastSerial;

    public GameSession(
        GameState state,
        IWildMagicController? magic = null,
        IDialogueClaimExtractor? claimExtractor = null,
        IDialogueProvider? dialogueProvider = null,
        IDialogueRouter? dialogueRouter = null,
        IDialogueAuditSink? dialogueAudit = null,
        IBackgroundTextGenerator? backgroundTextGenerator = null,
        IDialogueParser? dialogueParser = null,
        IDialogueParserRouter? dialogueParserRouter = null)
    {
        Engine = new GameEngine(state, backgroundTextGenerator);
        _magic = magic ?? NullWildMagicController.Instance;
        var usesClaimExtractorAdapter = dialogueParser is null && claimExtractor is not null;
        _dialogueParser = dialogueParser
            ?? (claimExtractor is null
                ? NullDialogueClaimExtractor.Instance
                : new DialogueClaimExtractorParserAdapter(claimExtractor));
        _dialogueProvider = dialogueProvider ?? NullDialogueProvider.Instance;
        _dialogueRouter = dialogueRouter ?? NullDialogueRouter.Instance;
        _dialogueAudit = dialogueAudit ?? NullDialogueAuditSink.Instance;
        _dialogueParserRouter = dialogueParserRouter
            ?? (usesClaimExtractorAdapter
                ? ClaimExtractorCompatibilityDialogueParserRouter.Instance
                : DeterministicDialogueParserRouter.Instance);
        _backgroundTextGenerator = backgroundTextGenerator;
    }

    public GameEngine Engine { get; private set; }

    public static GameSession CreateImperialEncounter(
        IWildMagicController? magic = null,
        string? originId = null,
        int seed = 7,
        IReadOnlyList<RunChronicleRecord>? memorials = null,
        IDialogueClaimExtractor? claimExtractor = null,
        IDialogueProvider? dialogueProvider = null,
        IDialogueRouter? dialogueRouter = null,
        IDialogueAuditSink? dialogueAudit = null,
        IBackgroundTextGenerator? backgroundTextGenerator = null,
        IDialogueParser? dialogueParser = null,
        IDialogueParserRouter? dialogueParserRouter = null,
        Characters.CharacterBuild? build = null)
    {
        var state = TestScenarios.ImperialEncounter(originId, memorials, build);
        state.Seed = Math.Max(1, seed);
        state.Rng = new DeterministicRng(state.Seed);
        return new GameSession(state, magic, claimExtractor, dialogueProvider, dialogueRouter, dialogueAudit, backgroundTextGenerator, dialogueParser, dialogueParserRouter);
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
            CastCommand cast => await CastSpellAsync(cast, cancellationToken),
            BeginCastCommand cast => BeginCast(cast),
            AwaitCastCommand awaitCast => await AwaitCast(awaitCast, cancellationToken),
            CancelCastCommand => CancelCast(),
            CharterCommand charter => CharterMagic(charter.Spell),
            EchoesCommand => ListEchoes(),
            EchoCommand echo => CastEcho(echo.Reference),
            TargetCommand target => SetTarget(target),
            ClearTargetCommand => ClearTarget(),
            MapCommand map => Engine.Map(map.Radius),
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
            ServicesCommand services => Engine.Services(services.Target),
            RequestServiceCommand service => Engine.RequestService(service.Service, service.Target),
            JournalCommand => Engine.Journal(),
            RumorsCommand => Engine.Rumors(),
            CharacterCommand => Engine.CharacterSheet(),
            TalkCommand talk => await TalkAsync(talk, cancellationToken),
            GiveCommand give => Engine.Give(give.Item, give.Target),
            RecruitCommand recruit => Engine.Recruit(recruit.Target),
            BondsCommand bonds => Engine.Bonds(bonds.Target),
            ReadCommand read => Engine.Read(read.Target),
            ExamineCommand examine => Engine.Examine(examine.Target),
            OpenCommand open => Engine.Open(open.Target),
            EnterCommand enter => Engine.EnterInterior(enter.Target),
            LeaveCommand => Engine.LeaveInterior(),
            PossessCommand possess => Engine.Possess(possess.Target),
            StandingCommand => Engine.Standing(),
            FollowersCommand => Engine.Followers(),
            JobsCommand => Engine.Jobs(),
            SaveCommand save => await SaveGameAsync(save.Path, cancellationToken),
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
            if (ShouldQueueDialogueClaimExtraction(result))
            {
                QueueDialogueClaimExtraction(spokenDialogue, result);
            }
        }

        result = ApplyCompletedClaimExtractions(result, pendingClaimCountBeforeCommand);
        var completed = CompleteRunIfNeeded(result);
        if (completed.ShouldQuit)
        {
            return completed;
        }

        completed = AddActorTurns(completed);
        var objectiveDeltas = Engine.EvaluateObjectiveProgress(completed);
        if (objectiveDeltas.Count > 0)
        {
            completed = completed with
            {
                Messages = completed.Messages.Concat(objectiveDeltas.PlayerMessages()).ToArray(),
                Deltas = completed.Deltas.Concat(objectiveDeltas).ToArray(),
            };
        }

        return CompleteRunIfNeeded(completed);
    }

    public GameView View() => Engine.View();

    public AgentObservation Observation(bool debug = false)
    {
        RefreshPendingCastResolution();
        var observation = Engine.Observation(debug);
        return observation with
        {
            PendingCast = _pendingCast is null
                ? null
                : BuildPendingCastView(_pendingCast),
        };
    }

    private WorldConsequenceApplyResult ApplySessionMessage(
        string source,
        string message,
        string operation,
        IReadOnlyDictionary<string, object?>? details = null) =>
        Engine.ApplyConsequence(WorldConsequence.Message(
            source,
            message,
            targetEntityId: Engine.State.ControlledEntityId.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: Engine.State.ControlledEntityId.Value,
            evidence: message,
            operation: operation,
            details: details));

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

        var targetState = Engine.ApplyConsequence(WorldConsequence.SetSelectedTarget(
            "target",
            command.Position.X,
            command.Position.Y,
            sourceEntityId: Engine.State.ControlledEntityId.Value,
            evidence: $"Target command set {command.Position.X},{command.Position.Y}.",
            operation: "setSelectedTarget"));
        if (!targetState.Applied)
        {
            var failure = targetState.Error ?? "Target could not be set.";
            return new ActionResult
            {
                Action = "target",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turn,
                TurnAfter = turn,
                Messages = new[] { failure },
                Deltas = targetState.Deltas,
            };
        }

        var message = $"Target set to {command.Position.X},{command.Position.Y}.";
        var applied = ApplySessionMessage(
            "target",
            message,
            operation: "targetMessage",
            details: new Dictionary<string, object?>
            {
                ["x"] = command.Position.X,
                ["y"] = command.Position.Y,
            });
        return new ActionResult
        {
            Action = "target",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = applied.Messages.ToArray(),
            Deltas = targetState.Deltas.Concat(applied.Deltas).ToArray(),
        };
    }

    private ActionResult ClearTarget()
    {
        var turn = Engine.State.Turn;
        var targetState = Engine.ApplyConsequence(WorldConsequence.SetSelectedTarget(
            "target",
            clear: true,
            sourceEntityId: Engine.State.ControlledEntityId.Value,
            evidence: "Target command cleared the selected target.",
            operation: "clearSelectedTarget"));
        var message = "Target cleared.";
        var applied = ApplySessionMessage("target", message, operation: "targetMessage");
        return new ActionResult
        {
            Action = "target",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = applied.Messages.ToArray(),
            Deltas = targetState.Deltas.Concat(applied.Deltas).ToArray(),
        };
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

        // Same instant charter detour as CastSpellAsync below: a *known* form's name casts it
        // now, with no provider call and no pending cast, so the GUI spell box (begin/await)
        // and CLI `cast` agree on charter names. Agent contract: begin_cast with a charter name
        // settles immediately — a following await_cast gets "No spell is waiting to resolve.";
        // Observation().PendingCast == null remains the settled signal.
        if (CharterSpellbook.Default.Find(command.Text) is { } charterSpell
            && Engine.State.Souls.KnowsCharterSpell(ControlledSoulId(), charterSpell.Id))
        {
            return CastCharterSpell(charterSpell);
        }

        var id = $"cast_{++_pendingCastSerial}";
        var castCommand = new CastCommand(command.Text, command.Performance ?? CastPerformance.Neutral);
        var cancellation = new CancellationTokenSource();
        _pendingCast = new PendingCast(
            id,
            castCommand,
            ResolutionTask: _magic.ResolveAsync(Engine, castCommand, cancellation.Token),
            Cancellation: cancellation);
        // Show the player the spell they reached for while it resolves, not an internal cast id.
        var message = $"Wild spell: {command.Text.Trim()}";
        var applied = ApplySessionMessage(
            "pending_cast",
            message,
            operation: "pendingCastMessage",
            details: new Dictionary<string, object?>
            {
                ["pendingCastId"] = id,
                ["status"] = "resolving",
                ["performance"] = command.Performance?.ToString() ?? CastPerformance.Neutral.ToString(),
            });
        return new ActionResult
        {
            Action = "begin_cast",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = applied.Messages.ToArray(),
            Deltas = applied.Deltas,
        };
    }

    private async Task<ActionResult> AwaitCast(AwaitCastCommand command, CancellationToken cancellationToken)
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

        var materialized = await MaterializePendingCastAsync(pending, cancellationToken);
        if (command.Performance is { } performance)
        {
            // The minigame score finalizes after the provider call started, so it arrives with
            // await_cast and is stamped here, at the apply boundary the engine owns.
            materialized = materialized with { Performance = performance };
        }

        if (_pendingCast?.Id == pending.Id)
        {
            _pendingCast = null;
        }

        DisposePendingCastResolutionOwnership(pending);
        var result = _magic.ApplyResolved(Engine, materialized);
        RecordEchoIfEnabled(pending.Command.Text, result);
        return result;
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

        var pending = _pendingCast;
        var id = pending.Id;
        _pendingCast = null;
        CancelPendingCastResolution(pending);
        var message = $"Pending cast {id} dissipates.";
        var applied = ApplySessionMessage(
            "pending_cast",
            message,
            operation: "pendingCastMessage",
            details: new Dictionary<string, object?>
            {
                ["pendingCastId"] = id,
                ["status"] = "cancelled",
            });
        return new ActionResult
        {
            Action = "cancel_cast",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = applied.Messages.ToArray(),
            Deltas = applied.Deltas,
        };
    }

    private ActionResult ProtectItem(string item, bool protectedState)
    {
        var turn = Engine.State.Turn;
        var actor = Engine.State.ControlledEntity;
        var action = protectedState ? "protect" : "unprotect";
        var message = protectedState
            ? $"{item} is protected from wild magic costs."
            : $"{item} is available as ordinary spell fuel.";
        var applied = Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "inventory",
            actor.Id.Value,
            item,
            op: action,
            amount: 0,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: item,
            operation: action,
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"You are not carrying {item}.";
            return new ActionResult
            {
                Action = action,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turn,
                TurnAfter = turn,
                Messages = new[] { failure },
                Deltas = applied.Deltas,
            };
        }

        return new ActionResult
        {
            Action = action,
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = applied.Messages.ToArray(),
            Deltas = applied.Deltas,
        };
    }

    /// <summary>Flag key gating the spell-echo experiment (docs/SPELL_ECHOES.md).</summary>
    public const string EchoesEnabledFlag = "echoes_enabled";

    /// <summary>
    /// Wild casting, with one instant detour: text that exactly matches a *known* charter
    /// form's id or name casts that form with zero model calls (docs/CHARTER_MAGIC.md). The
    /// GUI spell box and CLI `cast` both reach charter magic this way without a new verb, and
    /// free-text wild casting is untouched because only learned forms intercept.
    /// </summary>
    private async Task<ActionResult> CastSpellAsync(CastCommand cast, CancellationToken cancellationToken)
    {
        if (CharterSpellbook.Default.Find(cast.Text) is { } charterSpell
            && Engine.State.Souls.KnowsCharterSpell(ControlledSoulId(), charterSpell.Id))
        {
            return CastCharterSpell(charterSpell);
        }

        var result = await _magic.CastAsync(Engine, cast, cancellationToken);
        RecordEchoIfEnabled(cast.Text, result);
        return result;
    }

    private ActionResult CharterMagic(string? reference)
    {
        var turn = Engine.State.Turn;
        var soulId = ControlledSoulId();
        if (string.IsNullOrWhiteSpace(reference))
        {
            var known = Engine.State.Souls.KnownCharterSpellsFor(soulId)
                .Select(id => CharterSpellbook.Default.Find(id))
                .OfType<CharterSpell>()
                .ToArray();
            if (known.Length == 0)
            {
                return ActionResult.Simple(
                    "charter",
                    success: true,
                    consumedTurn: false,
                    turn,
                    turn,
                    "You know no charter forms. They are learned from manuals, warrants, and licensed paraphernalia.");
            }

            var lines = new List<string> { $"Known charter forms ({known.Length}):" };
            lines.AddRange(known.Select(spell =>
                $"- {spell.Id} ({spell.Name}): {spell.Summary} Cost: {spell.CostText}. Targeting: {spell.Targeting}."));
            lines.Add("Cast one with 'charter <id>'. Charter magic is instant, weak, and precise.");
            return ActionResult.Simple("charter", success: true, consumedTurn: false, turn, turn, lines.ToArray());
        }

        var spell = CharterSpellbook.Default.Find(reference);
        if (spell is null)
        {
            return ActionResult.Simple(
                "charter",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                $"No charter form answers to '{reference.Trim()}'.");
        }

        if (!Engine.State.Souls.KnowsCharterSpell(soulId, spell.Id))
        {
            return ActionResult.Simple(
                "charter",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                $"You have not learned {spell.Name}. Charter forms are learned, not improvised.");
        }

        return CastCharterSpell(spell);
    }

    private ActionResult CastCharterSpell(CharterSpell spell) =>
        _magic.ApplyResolved(Engine, new MaterializedMagicResolution(
            Provider: "charter",
            SpellText: spell.Name,
            Performance: CastPerformance.Neutral,
            RawText: "",
            Accepted: true,
            TechnicalFailure: false,
            Error: null,
            EffectTypes: spell.EffectTypes,
            ResolvedMagicJson: spell.BuildResolvedMagicJson(),
            DeedKind: "charter_magic"));

    private bool EchoesEnabled => EchoesEnabledFor(Engine.State);

    /// <summary>Tolerant read of the echoes world flag (bool, "true", or "1"), shared with the
    /// view builder so the GUI's repertoire panel gates the same way the commands do.</summary>
    public static bool EchoesEnabledFor(GameState state)
    {
        if (!state.WorldFlags.TryGetValue(EchoesEnabledFlag, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is bool flag)
        {
            return flag;
        }

        var text = Convert.ToString(raw);
        return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
    }

    private void RecordEchoIfEnabled(string spellText, ActionResult result)
    {
        if (!EchoesEnabled
            || !result.Success
            || string.IsNullOrWhiteSpace(spellText)
            || result.Magic is not { Accepted: true } magic
            || string.IsNullOrWhiteSpace(magic.ResolvedMagicJson))
        {
            return;
        }

        Engine.State.Echoes.Record(
            spellText,
            magic.ResolvedMagicJson!,
            magic.EffectTypes,
            ControlledSoulId(),
            Engine.State.Turn);
    }

    private ActionResult ListEchoes()
    {
        var turn = Engine.State.Turn;
        if (!EchoesEnabled)
        {
            return ActionResult.Simple(
                "echoes",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "Spell echoes are disabled for this run.");
        }

        var mine = Engine.State.Echoes.ForSoul(ControlledSoulId());
        if (mine.Count == 0)
        {
            return ActionResult.Simple(
                "echoes",
                success: true,
                consumedTurn: false,
                turn,
                turn,
                "Your grimoire is empty. Accepted wild casts are remembered here as echoes.");
        }

        var lines = new List<string> { $"Grimoire ({mine.Count} echoes):" };
        lines.AddRange(mine.Select((record, index) =>
        {
            var fatigue = record.TimesCast > 0 ? $", +{record.TimesCast} mana fatigue" : "";
            return $"{index + 1}. {record.Name} (cast {record.TimesCast}x{fatigue})";
        }));
        lines.Add("Re-cast one instantly with 'echo <number>'. Repetition climbs the cost ladder.");
        return ActionResult.Simple("echoes", success: true, consumedTurn: false, turn, turn, lines.ToArray());
    }

    private ActionResult CastEcho(string reference)
    {
        var turn = Engine.State.Turn;
        if (!EchoesEnabled)
        {
            return ActionResult.Simple(
                "echo",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "Spell echoes are disabled for this run.");
        }

        var record = Engine.State.Echoes.Find(reference, ControlledSoulId());
        if (record is null)
        {
            return ActionResult.Simple(
                "echo",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                $"No echo in your grimoire answers to '{reference.Trim()}'. Use 'echoes' to list them.");
        }

        // Repetition fatigue (docs/SPELL_ECHOES.md): each repeat of the same echo adds a mana
        // surcharge on top of the recorded costs, so improvisation stays the best deal. The
        // surcharge rides the ordinary cost pipeline and shows up in the cost line.
        var json = record.TimesCast > 0
            ? WithEchoFatigue(record.ResolvedMagicJson, record.TimesCast)
            : record.ResolvedMagicJson;
        var result = _magic.ApplyResolved(Engine, new MaterializedMagicResolution(
            Provider: "echo",
            SpellText: record.SpellText,
            Performance: CastPerformance.Neutral,
            RawText: "",
            Accepted: true,
            TechnicalFailure: false,
            Error: null,
            EffectTypes: record.EffectTypes,
            ResolvedMagicJson: json));
        if (result.Success)
        {
            Engine.State.Echoes.IncrementCast(record.Id);
        }

        return result;
    }

    private static string WithEchoFatigue(string resolvedMagicJson, int surcharge)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(resolvedMagicJson) is not System.Text.Json.Nodes.JsonObject root)
            {
                return resolvedMagicJson;
            }

            if (root["costs"] is not System.Text.Json.Nodes.JsonArray costs)
            {
                costs = new System.Text.Json.Nodes.JsonArray();
                root["costs"] = costs;
            }

            costs.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "mana",
                ["fields"] = new System.Text.Json.Nodes.JsonObject { ["amount"] = surcharge },
            });
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return resolvedMagicJson;
        }
    }

    private string ControlledSoulId() =>
        Engine.State.ControlledEntity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : Engine.State.ControlledEntityId.Value;

    private ActionResult Help() =>
        ActionResult.Simple(
            "help",
            success: true,
            consumedTurn: false,
            Engine.State.Turn,
            Engine.State.Turn,
                "Commands: inspect, map, travel, atlas, move, wait, target, pickup, drop, use, equip, focus, open, enter, leave, read, examine, talk, give, recruit, bonds, possess, cast, begin_cast, await_cast, cancel_cast, charter, echoes, echo, protect, unprotect, reagents, wares, buy, sell, services, request, journal, rumors, character, standing, followers, jobs, save, load, quit.");

    private async Task<ActionResult> SaveGameAsync(string path, CancellationToken cancellationToken)
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
        var materializedPendingCast = await FlushPendingCastResolutionAsync(cancellationToken);

        try
        {
            GameSaveService.Save(path, Engine.State, PendingCastToSave(), _pendingCastSerial);
            return result with
            {
                Messages = result.Messages.Concat(new[] { $"Saved run to {path}." }).ToArray(),
                Magic = materializedPendingCast is null
                    ? result.Magic
                    : ToMagicRecord(materializedPendingCast.Resolution),
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
            if (_pendingCast is not null)
            {
                CancelPendingCastResolution(_pendingCast);
            }

            Engine = new GameEngine(loaded.State, _backgroundTextGenerator);
            _pendingCastSerial = loaded.PendingCastSerial;
            _pendingCast = loaded.PendingCast is null
                ? null
                : new PendingCast(
                    loaded.PendingCast.Id,
                    new CastCommand(loaded.PendingCast.Text, loaded.PendingCast.Performance ?? CastPerformance.Neutral),
                    Resolution: loaded.PendingCast.Resolution);
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
            Messages = result.Messages.Concat(deltas.PlayerMessages()).ToArray(),
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

        var route = await RouteDialogueContextAsync(preparation.Turn, cancellationToken);
        var request = route.Assembly.BuildDialogueRequest(route.Selection) with
        {
            RecentDialogue = RecentDialogueFor(preparation.Turn.SpeakerId),
        };
        route = route with
        {
            Record = AttachGeneratorRequestMetrics(route.Record, request),
        };
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
            RecordDialogueAudit(request, providerResult, failure, new[] { ex.Message }, route.Record);
            return failure with
            {
                Dialogue = ToDialogueResolutionRecord(providerResult),
                DialogueRoute = route.Record,
            };
        }

        if (providerResult.TechnicalFailure || providerResult.Response is null)
        {
            var failure = DialogueTechnicalFailure(
                preparation.Turn,
                providerResult.Provider,
                providerResult.RawText,
                providerResult.Error ?? "Dialogue provider failed.");
            RecordDialogueAudit(request, providerResult, failure, new[] { providerResult.Error ?? "Dialogue provider failed." }, route.Record);
            return failure with
            {
                Dialogue = ToDialogueResolutionRecord(providerResult),
                DialogueRoute = route.Record,
            };
        }

        var normalized = NormalizeDialogueResponse(request, providerResult.Response, out var validationError);
        if (normalized is null)
        {
            var failure = DialogueTechnicalFailure(
                preparation.Turn,
                providerResult.Provider,
                providerResult.RawText,
                validationError ?? "Dialogue provider returned invalid speech.");
            RecordDialogueAudit(request, providerResult, failure, new[] { validationError ?? "invalid_dialogue" }, route.Record);
            return failure with
            {
                Dialogue = ToDialogueResolutionRecord(providerResult),
                DialogueRoute = route.Record,
            };
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
        result = result with
        {
            Dialogue = ToDialogueResolutionRecord(providerResult with { Response = normalized }),
            DialogueRoute = route.Record,
        };
        RememberDialogueExchange(preparation.Turn, request.PlayerText, normalized.SpokenText);
        RecordDialogueAudit(request, providerResult, result, Array.Empty<string>(), route.Record);
        return result;
    }

    private IReadOnlyList<string>? RecentDialogueFor(string speakerId) =>
        _recentDialogueBySpeaker.TryGetValue(speakerId, out var lines) && lines.Count > 0
            ? lines.ToArray()
            : null;

    /// <summary>
    /// Appends this turn's player line and the NPC's reply to the speaker's rolling history, so the
    /// next turn with the same NPC can reference what was just said. Trims each line and keeps only
    /// the last <see cref="RecentDialogueLineCap"/> lines (docs/OPTIMIZATION_PLAN.md WS3.4).
    /// </summary>
    private void RememberDialogueExchange(PreparedDialogueTurn turn, string playerText, string spokenText)
    {
        if (!_recentDialogueBySpeaker.TryGetValue(turn.SpeakerId, out var lines))
        {
            lines = new List<string>();
            _recentDialogueBySpeaker[turn.SpeakerId] = lines;
        }

        lines.Add($"player: {TrimDialogueLine(playerText)}");
        lines.Add($"{turn.SpeakerName}: {TrimDialogueLine(spokenText)}");
        if (lines.Count > RecentDialogueLineCap)
        {
            lines.RemoveRange(0, lines.Count - RecentDialogueLineCap);
        }
    }

    private static string TrimDialogueLine(string text)
    {
        var collapsed = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > RecentDialogueLineLength
            ? collapsed[..RecentDialogueLineLength].TrimEnd() + "…"
            : collapsed;
    }

    private static DialogueResolutionRecord ToDialogueResolutionRecord(DialogueProviderResult providerResult) =>
        new(
            providerResult.Provider,
            providerResult.RawText,
            providerResult.TechnicalFailure,
            providerResult.Error,
            providerResult.Response);

    private static bool ShouldQueueDialogueClaimExtraction(ActionResult result) =>
        result.Dialogue?.Response?.Proposals is null;

    private async Task<PreparedDialogueRoute> RouteDialogueContextAsync(
        PreparedDialogueTurn turn,
        CancellationToken cancellationToken)
    {
        var assembly = DialogueContextAssembler.Build(Engine, turn);
        var stopwatch = Stopwatch.StartNew();
        DialogueRouteResult result;
        try
        {
            result = await _dialogueRouter.RouteAsync(assembly.RouteRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            result = new DialogueRouteResult(
                _dialogueRouter.Name,
                "",
                TechnicalFailure: true,
                Error: ex.Message,
                SelectedCardIds: Array.Empty<string>());
        }

        stopwatch.Stop();
        var selection = assembly.Select(result);
        var record = new DialogueRouteRecord(
            assembly.RouteRequest,
            result.Provider,
            result.RawText,
            result.TechnicalFailure,
            result.Error,
            selection.SelectedCardIds,
            selection.FallbackCardIds,
            result.Reason,
            selection.UsedFallback,
            assembly.CreateMetrics(selection, stopwatch.ElapsedMilliseconds));
        return new PreparedDialogueRoute(record, assembly, selection);
    }

    private static DialogueRouteRecord AttachGeneratorRequestMetrics(
        DialogueRouteRecord record,
        DialogueRequest request)
    {
        var generatorRequestBytes = DialoguePayloadSizer.JsonUtf8Bytes(request);
        if (record.Metrics is null)
        {
            return record;
        }

        return record with
        {
            Metrics = record.Metrics with { GeneratorRequestBytes = generatorRequestBytes },
        };
    }

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
            var exchangeDeltas = AddDialogueExchangeMemory(turn, response.SpokenText);
            return exchangeDeltas.Count == 0
                ? result
                : result with { Deltas = result.Deltas.Concat(exchangeDeltas).ToArray() };
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
            Engine.State.Claims.Records.TakeLast(DialogueRecentClaimLimit).ToArray(),
            Engine.State.ControlledEntityId.Value);

        ApplyDialogueProposalSet(
            turn,
            claimRequest,
            provider,
            response.SpokenText,
            response.Intent,
            proposals,
            requiresSpokenTextSupport: true,
            parserOrigin: false,
            messages,
            deltas);

        deltas.AddRange(AddDialogueExchangeMemory(turn, response.SpokenText));
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

    private void ApplyDialogueProposalSet(
        PreparedDialogueTurn turn,
        DialogueClaimRequest claimRequest,
        string provider,
        string spokenText,
        string? intent,
        DialogueProposalSet? proposals,
        bool requiresSpokenTextSupport,
        bool parserOrigin,
        List<string> messages,
        List<StateDelta> deltas)
    {
        if (proposals is null)
        {
            return;
        }

        var actions = proposals.Actions ?? Array.Empty<DialogueActionProposal>();
        var preAppliedActionIndexes = new HashSet<int>();
        for (var index = 0; index < actions.Count; index++)
        {
            if (NormalizeDialogueActionType(actions[index].Type) != "reveal_service")
            {
                continue;
            }

            ApplyDialogueActionProposal(provider, turn, intent, spokenText, actions[index], messages, deltas);
            preAppliedActionIndexes.Add(index);
        }

        foreach (var claim in proposals.Claims ?? Array.Empty<DialogueClaimProposal>())
        {
            if (requiresSpokenTextSupport
                && !GeneratedClaimIsSupportedBySpokenText(claim, spokenText))
            {
                var operation = parserOrigin ? "claimExtractionSkipped" : "dialogueProposalSkipped";
                var summary = parserOrigin
                    ? "Dialogue claim extraction skipped because it was not supported by spoken text."
                    : "Dialogue claim proposal skipped because it was not supported by spoken text.";
                deltas.Add(new StateDelta(
                    operation,
                    turn.SpeakerId,
                    summary,
                    new Dictionary<string, object?>
                    {
                        ["provider"] = provider,
                        ["proposalType"] = "claim",
                        ["claimText"] = claim.Text,
                        ["reason"] = "unsupported_by_spoken_text",
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                continue;
            }

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

        if (proposals.Want is not null)
        {
            ApplyDialogueWantProposal(provider, proposals.Want, messages, deltas);
        }

        for (var index = 0; index < actions.Count; index++)
        {
            if (preAppliedActionIndexes.Contains(index))
            {
                continue;
            }

            var action = actions[index];
            ApplyDialogueActionProposal(provider, turn, intent, spokenText, action, messages, deltas);
        }
    }

    private static bool GeneratedClaimIsSupportedBySpokenText(DialogueClaimProposal claim, string spokenText)
    {
        if (string.IsNullOrWhiteSpace(claim.Text))
        {
            return false;
        }

        var spokenTokens = ClaimSupportTokens(spokenText);
        if (spokenTokens.Count == 0)
        {
            return false;
        }

        var namedTokens = ProperNameTokens($"{claim.Text} {claim.Subject} {claim.ItemName}");
        if (namedTokens.Count > 0 && !namedTokens.Any(spokenTokens.Contains))
        {
            return false;
        }

        var claimTokens = ClaimSupportTokens($"{claim.Text} {claim.Subject} {claim.ItemName}");
        if (claimTokens.Count == 0)
        {
            return false;
        }

        var overlap = claimTokens.Count(spokenTokens.Contains);
        var required = Math.Min(3, Math.Max(1, claimTokens.Count / 2));
        return overlap >= required;
    }

    private static bool GeneratedFactIsSupportedBySpokenText(string factText, string? subject, string spokenText)
    {
        if (string.IsNullOrWhiteSpace(factText))
        {
            return false;
        }

        var spokenTokens = ClaimSupportTokens(spokenText);
        if (spokenTokens.Count == 0)
        {
            return false;
        }

        var factSource = $"{factText} {subject}";
        var namedTokens = ProperNameTokens(factSource);
        if (namedTokens.Count > 0 && !namedTokens.Any(spokenTokens.Contains))
        {
            return false;
        }

        var factTokens = ClaimSupportTokens(factSource);
        if (factTokens.Count == 0)
        {
            return false;
        }

        var overlap = factTokens.Count(spokenTokens.Contains);
        var required = Math.Min(3, Math.Max(1, factTokens.Count / 2));
        return overlap >= required;
    }

    private static HashSet<string> ClaimSupportTokens(string text) =>
        new string(text
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeClaimSupportToken)
            .Where(token => token.Length >= 3)
            .Where(token => !ClaimSupportStopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeClaimSupportToken(string token)
    {
        if (token.Length > 5 && token.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
        {
            return $"{token[..^3]}y";
        }

        if (token.Length > 5 && token.EndsWith("ing", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^3];
        }

        if (token.Length > 4 && token.EndsWith("ed", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^2];
        }

        if (token.Length > 3
            && token.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && !token.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^1];
        }

        return token;
    }

    private static HashSet<string> ProperNameTokens(string text) =>
        text.Split(new[] { ' ', ',', '.', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '|', '-' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length >= 4)
            .Where(token => char.IsUpper(token[0]))
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(token => token.Length >= 4)
            .Where(token => !ClaimSupportStopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ClaimSupportStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "after",
        "before",
        "behind",
        "break",
        "bring",
        "can",
        "could",
        "from",
        "have",
        "into",
        "keep",
        "keeps",
        "know",
        "knows",
        "lead",
        "leads",
        "left",
        "lower",
        "marks",
        "named",
        "near",
        "need",
        "offer",
        "offers",
        "only",
        "past",
        "quietly",
        "sells",
        "should",
        "that",
        "there",
        "they",
        "this",
        "through",
        "until",
        "wards",
        "where",
        "with",
        "would",
    };

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
        var consequence = WorldConsequence.RecordMemory(
            $"dialogue:{provider}",
            ownerId,
            proposal.Text.Trim(),
            string.IsNullOrWhiteSpace(proposal.Provenance) ? "conversation" : proposal.Provenance.Trim(),
            salience,
            proposal.Shareable,
            sourceEntityId: ownerId,
            operation: "dialogueMemory",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "memory",
                ["requireOwnerEntity"] = true,
            },
            reason: "Dialogue response proposed a durable memory.");
        var applied = Engine.ApplyConsequence(consequence);
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddDialogueProposalSkipped(
                provider,
                "memory",
                ownerId,
                applied.Error ?? "Dialogue memory proposal could not be applied.",
                deltas,
                new Dictionary<string, object?>
                {
                    ["memoryText"] = proposal.Text.Trim(),
                });
        }
    }

    private void ApplyDialogueBondProposal(
        string provider,
        DialogueBondProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var applied = Engine.ApplyConsequence(WorldConsequence.UpdateBond(
            $"dialogue:{provider}",
            proposal.EntityId,
            SoulIdFor(Engine.State.ControlledEntity),
            proposal.LoyaltyDelta,
            proposal.FearDelta,
            proposal.AdmirationDelta,
            proposal.ResentmentDelta,
            proposal.Posture,
            WorldConsequenceVisibility.Message,
            operation: "dialogueBondShift",
            maxDelta: DialogueBondDeltaLimit,
            reason: proposal.Reason,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "bond",
            }));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddDialogueProposalSkipped(
                provider,
                "bond",
                proposal.EntityId,
                applied.Error ?? "Dialogue bond proposal could not be applied.",
                deltas,
                new Dictionary<string, object?>
                {
                    ["posture"] = proposal.Posture,
                    ["providerReason"] = proposal.Reason,
                });
        }
    }

    private void ApplyDialogueWantProposal(
        string provider,
        DialogueWantProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var entityId = string.IsNullOrWhiteSpace(proposal.EntityId)
            ? Engine.State.ControlledEntityId.Value
            : proposal.EntityId.Trim();
        var applied = Engine.ApplyConsequence(WorldConsequence.UpdateWant(
            $"dialogue:{provider}",
            entityId,
            proposal.Text,
            proposal.Salience,
            proposal.Status,
            proposal.Stakes,
            proposal.Tags,
            proposal.AddTags,
            proposal.RemoveTags,
            WorldConsequenceVisibility.Hidden,
            sourceEntityId: entityId,
            reason: proposal.Reason,
            operation: "dialogueWantShift",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "want",
            },
            recordMemory: true,
            memoryText: FirstNonBlank(
                proposal.Reason,
                string.IsNullOrWhiteSpace(proposal.Text)
                    ? "A conversation with the sorcerer changed the speaker's active want."
                    : $"A conversation with the sorcerer changed the speaker's active want: {proposal.Text.Trim()}"),
            memoryProvenance: "conversation",
            memorySalience: proposal.Salience,
            memoryShareable: null));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddDialogueProposalSkipped(
                provider,
                "want",
                entityId,
                applied.Error ?? "Dialogue want proposal could not be applied.",
                deltas,
                new Dictionary<string, object?>
                {
                    ["status"] = proposal.Status,
                    ["providerReason"] = proposal.Reason,
                });
        }
    }

    private static void AddDialogueProposalSkipped(
        string provider,
        string proposalType,
        string? target,
        string reason,
        List<StateDelta> deltas,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var payload = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        payload["provider"] = provider;
        payload["proposalType"] = proposalType;
        payload["reason"] = reason;
        payload["auditOnly"] = true;
        payload["playerVisible"] = false;
        deltas.Add(new StateDelta(
            "dialogueProposalSkipped",
            target ?? "",
            $"Dialogue {proposalType} proposal skipped: {reason}",
            payload));
    }

    private void ApplyDialogueActionProposal(
        string provider,
        PreparedDialogueTurn turn,
        string? intent,
        string spokenText,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var type = NormalizeDialogueActionType(proposal.Type);
        if (type == "none")
        {
            return;
        }

        if (IsDirectWorldConsequenceActionType(type))
        {
            proposal = proposal with
            {
                Type = "consequence",
                ConsequenceType = FirstNonBlank(proposal.ConsequenceType, type),
            };
            type = "consequence";
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
            case "attack":
                ApplyDialogueAttack(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "create_promise":
                ApplyDialogueCreatePromise(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "add_canon":
                ApplyDialogueAddCanon(provider, turn.SpeakerId, spokenText, proposal, messages, deltas);
                return;
            case "offer_trade":
                ApplyDialogueOfferTrade(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "mark_location":
            case "spawn_fixture":
                ApplyDialogueSpawnFixture(provider, turn.SpeakerId, type, proposal, messages, deltas);
                return;
            case "reveal_service":
                ApplyDialogueRevealService(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "recruit":
                ApplyDialogueRecruit(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "consequence":
                ApplyDialogueTypedConsequence(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
        }

        RejectDialogueAction(provider, proposal, "Dialogue action handlers are not implemented for this action type yet.", deltas);
    }

    private static bool IsRefusalIntent(string? intent) =>
        NormalizeToken(intent ?? "", "").Equals("refuse", StringComparison.OrdinalIgnoreCase);

    private static bool IsCooperativeDialogueAction(string type) =>
        type is "step_aside" or "give_item" or "open_door" or "create_promise" or "add_canon" or "offer_trade" or "mark_location" or "reveal_service" or "recruit" or "consequence";

    private static bool IsDirectWorldConsequenceActionType(string type) =>
        WorldConsequenceTypes.IsKnown(type)
        && !type.Equals(WorldConsequenceTypes.CreatePromise, StringComparison.OrdinalIgnoreCase)
        && !type.Equals(WorldConsequenceTypes.AddCanon, StringComparison.OrdinalIgnoreCase)
        && !type.Equals(WorldConsequenceTypes.SpawnFixture, StringComparison.OrdinalIgnoreCase)
        && !type.Equals(WorldConsequenceTypes.OfferTrade, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDialogueActionType(string? type)
    {
        var normalized = NormalizeToken(type ?? "", "none");
        return normalized switch
        {
            "" or "none" or "no_action" => "none",
            "open" or "unlock" or "open_or_unlock" => "open_door",
            "give" or "hand_over" or "transfer" or "transfer_item" => "give_item",
            "move_aside" or "make_room" or "step_out_of_the_way" => "step_aside",
            "run" or "run_away" or "retreat" => "flee",
            "summon_help" or "raise_alarm" or "call_for_help" => "call_help",
            "strike" or "hit" or "attack_entity" => "attack",
            "promise" or "make_promise" or "bind_promise" or "create_promise_now" => "create_promise",
            "canonize" or "canonize_fact" or "canonicalize_fact" or "add_canon" or "record_canon" or "record_fact" => "add_canon",
            "trade" or "offer_trade_now" or "open_trade" => "offer_trade",
            "service" or "offer_service" or "reveal_service_now" => "reveal_service",
            "world_consequence" or "worldconsequence" or "typed_consequence" or "apply_consequence" => "consequence",
            "mark" or "mark_place" or "mark_site" or "mark_point" => "mark_location",
            "create_fixture" or "create_marker" or "place_fixture" => "spawn_fixture",
            "join" or "join_me" or "follow" or "follow_me" or "come_with" or "come_with_me" or "ally" => "recruit",
            _ => normalized,
        };
    }

    private void ApplyDialogueRecruit(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var result = Engine.RecruitFromDialogue(actorId, provider, proposal.Reason);
        if (result.Deltas.Count == 0)
        {
            RejectDialogueAction(
                provider,
                proposal,
                result.Messages.FirstOrDefault() ?? "Dialogue recruit action could not be applied.",
                deltas);
            return;
        }

        messages.AddRange(result.Messages);
        deltas.AddRange(result.Deltas);
    }

    private void ApplyDialogueTypedConsequence(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var consequenceType = FirstNonBlank(
            proposal.ConsequenceType,
            TextPayload(proposal.ConsequencePayload, "consequenceType"),
            TextPayload(proposal.ConsequencePayload, "consequence_type"),
            TextPayload(proposal.ConsequencePayload, "worldConsequenceType"),
            TextPayload(proposal.ConsequencePayload, "world_consequence_type"));
        if (string.IsNullOrWhiteSpace(consequenceType))
        {
            RejectDialogueAction(provider, proposal, "Generic dialogue consequence did not include consequenceType.", deltas);
            return;
        }

        var timing = WorldConsequenceTiming.Normalize(FirstNonBlank(
            proposal.ConsequenceTiming,
            TextPayload(proposal.ConsequencePayload, "timing"),
            TextPayload(proposal.ConsequencePayload, "consequenceTiming"),
            TextPayload(proposal.ConsequencePayload, "consequence_timing"),
            WorldConsequenceTiming.Immediate));

        var normalizedConsequenceType = NormalizeDialogueConsequenceType(consequenceType);
        var payload = proposal.ConsequencePayload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(proposal.ConsequencePayload, StringComparer.OrdinalIgnoreCase);
        PromoteDialogueActionFieldsToPayload(proposal, normalizedConsequenceType, payload);
        payload["operation"] = FirstNonBlank(TextPayload(payload, "operation"), "dialogueConsequence");
        payload["provider"] = provider;
        payload["proposalType"] = "consequence";
        payload["speakerId"] = actorId;
        if (proposal.PlayerVisible is not null)
        {
            payload["playerVisible"] = proposal.PlayerVisible.Value;
        }

        var targetEntityId = FirstNonBlank(
            proposal.TargetEntityId,
            TextPayload(payload, "targetEntityId"),
            TextPayload(payload, "target_entity_id"),
            TextPayload(payload, "targetId"),
            TextPayload(payload, "target_id"),
            TextPayload(payload, "entityId"),
            TextPayload(payload, "entity_id"),
            TextPayload(payload, "target"),
            normalizedConsequenceType.Equals(WorldConsequenceTypes.RequestService, StringComparison.OrdinalIgnoreCase)
                ? actorId
                : null);
        if (normalizedConsequenceType.Equals(WorldConsequenceTypes.RequestService, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(TextPayload(payload, "actorEntityId"))
            && string.IsNullOrWhiteSpace(TextPayload(payload, "actor_entity_id"))
            && string.IsNullOrWhiteSpace(TextPayload(payload, "requesterEntityId"))
            && string.IsNullOrWhiteSpace(TextPayload(payload, "requester_entity_id")))
        {
            payload["actorEntityId"] = Engine.State.ControlledEntityId.Value;
        }

        var sourceEntityId = FirstNonBlank(
            TextPayload(payload, "sourceEntityId"),
            TextPayload(payload, "source_entity_id"),
            actorId);
        var visibility = NormalizeToken(FirstNonBlank(
            proposal.ConsequenceVisibility,
            TextPayload(payload, "visibility"),
            TextPayload(payload, "consequenceVisibility"),
            TextPayload(payload, "consequence_visibility"),
            proposal.PlayerVisible == true ? WorldConsequenceVisibility.Message : WorldConsequenceVisibility.Hidden)!, WorldConsequenceVisibility.Hidden);
        var salience = Math.Clamp(proposal.Salience ?? IntPayload(payload, "salience") ?? 1, 1, 5);
        var confidence = Math.Clamp(proposal.ConsequenceConfidence ?? IntPayload(payload, "confidence") ?? 100, 0, 100);

        var applied = Engine.ApplyConsequence(new WorldConsequence(
            normalizedConsequenceType,
            $"dialogue:{provider}",
            SourceEntityId: sourceEntityId,
            TargetEntityId: targetEntityId,
            Salience: salience,
            Confidence: confidence,
            Visibility: visibility,
            Evidence: FirstNonBlank(proposal.Reason, TextPayload(payload, "evidence")),
            Reason: FirstNonBlank(proposal.Reason, TextPayload(payload, "reason"), $"Dialogue action proposed {consequenceType}."),
            Payload: payload,
            Timing: timing));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? $"Dialogue consequence {consequenceType} could not be applied.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private static string NormalizeDialogueConsequenceType(string type) =>
        WorldConsequenceTypes.Normalize(type);

    private static void PromoteDialogueActionFieldsToPayload(
        DialogueActionProposal proposal,
        string normalizedConsequenceType,
        Dictionary<string, object?> payload)
    {
        PutPayloadIfMissing(payload, "targetEntityId", proposal.TargetEntityId);
        PutPayloadIfMissing(payload, "itemName", proposal.ItemName);
        PutPayloadIfMissing(payload, "item", proposal.ItemName);
        PutPayloadIfMissing(payload, "quantity", proposal.Quantity);
        PutPayloadIfMissing(payload, "gold", proposal.Gold);
        PutPayloadIfMissing(payload, "name", proposal.Name);
        PutPayloadIfMissing(payload, "description", proposal.Description);
        PutPayloadIfMissing(payload, "fixtureType", proposal.FixtureType);
        PutPayloadIfMissing(payload, "material", proposal.Material);
        PutPayloadIfMissing(payload, "tags", proposal.Tags);
        PutPayloadIfMissing(payload, "interactableVerbs", proposal.InteractableVerbs);
        PutPayloadIfMissing(payload, "x", proposal.X);
        PutPayloadIfMissing(payload, "y", proposal.Y);
        PutPayloadIfMissing(payload, "blocksMovement", proposal.BlocksMovement);
        PutPayloadIfMissing(payload, "blocksSight", proposal.BlocksSight);
        PutPayloadIfMissing(payload, "serviceId", proposal.ServiceId);
        PutPayloadIfMissing(payload, "service", proposal.ServiceId);
        PutPayloadIfMissing(payload, "serviceName", proposal.Name);
        PutPayloadIfMissing(payload, "effectKind", proposal.EffectKind);
        PutPayloadIfMissing(payload, "targetHint", proposal.TargetHint);
        PutPayloadIfMissing(payload, "itemCost", proposal.ItemCost);
        PutPayloadIfMissing(payload, "goldCost", proposal.GoldCost);
        PutPayloadIfMissing(payload, "triggerHint", proposal.TriggerHint);
        PutPayloadIfMissing(payload, "realizationKind", proposal.RealizationKind);
        PutPayloadIfMissing(payload, "claimedPlace", proposal.ClaimedPlace);
        PutPayloadIfMissing(payload, "subject", proposal.Subject);
        PutPayloadIfMissing(payload, "playerVisible", proposal.PlayerVisible);
        PutPayloadIfMissing(payload, "salience", proposal.Salience);
        PutPayloadIfMissing(payload, "autoBind", proposal.AutoBind);
        PutPayloadIfMissing(payload, "stackExisting", proposal.StackExisting);
        PutPayloadIfMissing(payload, "wantStatusOnComplete", proposal.WantStatusOnComplete);
        PutPayloadIfMissing(payload, "wantStakesOnComplete", proposal.WantStakesOnComplete);
        PutPayloadIfMissing(payload, "wantAddTagsOnComplete", proposal.WantAddTagsOnComplete);
        PutPayloadIfMissing(payload, "wantRemoveTagsOnComplete", proposal.WantRemoveTagsOnComplete);

        if (normalizedConsequenceType.Equals(WorldConsequenceTypes.CreatePromise, StringComparison.OrdinalIgnoreCase))
        {
            PutPayloadIfMissing(payload, "text", proposal.PromiseText);
            PutPayloadIfMissing(payload, "kind", proposal.PromiseKind);
        }
        else if (normalizedConsequenceType.Equals(WorldConsequenceTypes.AddCanon, StringComparison.OrdinalIgnoreCase))
        {
            PutPayloadIfMissing(payload, "text", proposal.CanonText);
            PutPayloadIfMissing(payload, "kind", proposal.CanonKind);
            PutPayloadIfMissing(payload, "summary", proposal.CanonSummary);
        }
        else if (normalizedConsequenceType is WorldConsequenceTypes.Message or WorldConsequenceTypes.RecordMemory)
        {
            PutPayloadIfMissing(payload, "text", FirstNonBlank(proposal.Description, proposal.Name, proposal.Reason));
        }
    }

    private static void PutPayloadIfMissing(Dictionary<string, object?> payload, string key, object? value)
    {
        if (value is null || payload.ContainsKey(key))
        {
            return;
        }

        if (value is string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                payload[key] = text.Trim();
            }

            return;
        }

        if (value is IEnumerable<string> strings)
        {
            var values = strings
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToArray();
            if (values.Length > 0)
            {
                payload[key] = values;
            }

            return;
        }

        payload[key] = value;
    }

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
            RejectDialogueAction(
                provider,
                new DialogueActionProposal(flee ? "flee" : "step_aside", actorId),
                "The speaker is missing or has no position.",
                deltas);
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

        var operation = flee ? "dialogueFlee" : "dialogueStepAside";
        var applied = Engine.ApplyConsequence(WorldConsequence.MoveEntity(
            $"dialogue:{provider}",
            actor.Id.Value,
            destination.Value.X,
            destination.Value.Y,
            operation,
            WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            reason: flee ? "dialogue action: flee" : "dialogue action: step aside",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = flee ? "flee" : "step_aside",
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, new DialogueActionProposal(flee ? "flee" : "step_aside", actorId), applied.Error ?? "Dialogue movement could not be applied.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
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

        if (giverInventory.TreasuredItems.Contains(item))
        {
            RejectDialogueAction(provider, proposal, "The proposed item is protected.", deltas);
            return;
        }

        var receiver = string.IsNullOrWhiteSpace(proposal.TargetEntityId)
            ? Engine.State.ControlledEntity
            : Engine.EntityById(proposal.TargetEntityId) ?? Engine.State.ControlledEntity;
        var transfer = Engine.ApplyConsequence(WorldConsequence.TransferItem(
            $"dialogue:{provider}",
            giver.Id.Value,
            "give",
            item,
            quantity: 1,
            recipientEntityId: receiver.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: giver.Id.Value,
            evidence: proposal.Reason,
            operation: "dialogueGiveItem",
            message: $"{giver.Name} gives {item} to {receiver.Name}.",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["speakerId"] = giver.Id.Value,
                ["receiverId"] = receiver.Id.Value,
                ["inventoryKey"] = item,
            }));
        if (!transfer.Applied)
        {
            RejectDialogueAction(provider, proposal, transfer.Error ?? "The speaker could not give the proposed item.", deltas);
            deltas.AddRange(transfer.Deltas);
            return;
        }

        messages.AddRange(transfer.Messages);
        deltas.AddRange(transfer.Deltas);
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
            : $"{actor.Name} calls for help, loud enough that someone nearby could come.";
        var message = $"{actor.Name} calls for help.";
        var applied = Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            $"dialogue:{provider}",
            kind,
            turns: 2,
            eventPayload: new Dictionary<string, object?>
            {
                ["text"] = text,
                ["source"] = "dialogue",
                ["provider"] = provider,
                ["reason"] = proposal.Reason,
            },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: proposal.Reason,
            operation: "dialogueCallHelp",
            message: message,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["kind"] = kind,
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "Help could not be scheduled.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private void ApplyDialogueAttack(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var attacker = Engine.EntityById(actorId);
        if (attacker is null || !attacker.TryGet<ActorComponent>(out var attackerActor) || !attackerActor.Alive)
        {
            RejectDialogueAction(provider, proposal, "The speaker is not a living actor who can attack.", deltas);
            return;
        }

        var target = ResolveDialogueAttackTarget(attacker, proposal.TargetEntityId);
        if (target is null || !target.TryGet<ActorComponent>(out var targetActor) || !targetActor.Alive)
        {
            RejectDialogueAction(provider, proposal, "No living attack target was found.", deltas);
            return;
        }

        if (target.Id == attacker.Id)
        {
            RejectDialogueAction(provider, proposal, "The speaker cannot attack themself through dialogue.", deltas);
            return;
        }

        if (!attacker.TryGet<PositionComponent>(out var attackerPosition)
            || !target.TryGet<PositionComponent>(out var targetPosition)
            || GameEngine.Distance(attackerPosition.Position, targetPosition.Position) > 1)
        {
            RejectDialogueAction(provider, proposal, "The attack target is not adjacent.", deltas);
            return;
        }

        var attackDeltas = Engine.AttackEntity(
            attacker,
            target,
            source: $"dialogue:{provider}",
            evidence: proposal.Reason ?? $"{attacker.Name} attacked {target.Name} during dialogue.",
            reason: "dialogue action: attack",
            operation: "dialogueAttack",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "attack",
                ["targetEntityId"] = target.Id.Value,
            });
        if (attackDeltas.Count == 0)
        {
            RejectDialogueAction(provider, proposal, "The attack produced no consequence.", deltas);
            return;
        }

        messages.AddRange(attackDeltas.PlayerMessages());
        deltas.AddRange(attackDeltas);
    }

    private Entity? ResolveDialogueAttackTarget(Entity attacker, string? targetEntityId)
    {
        if (!string.IsNullOrWhiteSpace(targetEntityId)
            && Engine.EntityById(targetEntityId) is { } target)
        {
            return target;
        }

        return Engine.State.ControlledEntity.Id == attacker.Id
            ? null
            : Engine.State.ControlledEntity;
    }

    private void ApplyDialogueCreatePromise(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var speaker = Engine.EntityById(actorId);
        if (speaker is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        Entity? anchor = null;
        string? unresolvedTargetHint = null;
        if (!string.IsNullOrWhiteSpace(proposal.TargetEntityId))
        {
            anchor = Engine.EntityById(proposal.TargetEntityId);
            if (anchor is null)
            {
                unresolvedTargetHint = proposal.TargetEntityId.Trim();
            }
        }

        var text = FirstNonBlank(proposal.PromiseText, proposal.Description, proposal.Name, proposal.ItemName);
        if (string.IsNullOrWhiteSpace(text))
        {
            RejectDialogueAction(provider, proposal, "The promise action did not include promise text.", deltas);
            return;
        }

        var triggerHint = FirstNonBlank(proposal.TriggerHint, proposal.TargetHint, unresolvedTargetHint, "");
        var realizationKind = string.IsNullOrWhiteSpace(proposal.RealizationKind)
            ? null
            : proposal.RealizationKind.Trim();
        var applied = Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            $"dialogue:{provider}",
            string.IsNullOrWhiteSpace(proposal.PromiseKind) ? "rumor" : proposal.PromiseKind.Trim(),
            text.Trim(),
            anchorEntityId: anchor?.Id.Value,
            triggerHint: triggerHint ?? "",
            visibility: (proposal.PlayerVisible ?? true) ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
            sourceEntityId: speaker.Id.Value,
            evidence: proposal.Reason ?? text.Trim(),
            reason: "dialogue action: create promise",
            operation: "dialogueCreatePromise",
            stackExisting: proposal.StackExisting ?? true,
            playerVisible: proposal.PlayerVisible ?? true,
            salience: Math.Clamp(proposal.Salience ?? 3, 1, 5),
            subject: FirstNonBlank(proposal.Subject, proposal.Name, proposal.ItemName, unresolvedTargetHint),
            claimedPlace: FirstNonBlank(proposal.ClaimedPlace, unresolvedTargetHint),
            realizationKind: realizationKind,
            autoBind: proposal.AutoBind ?? (anchor is not null),
            emitMessage: proposal.PlayerVisible ?? true,
            message: $"A promise enters the world through {speaker.Name}: {text.Trim()}",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "create_promise",
                ["speakerId"] = speaker.Id.Value,
                ["unresolvedTargetHint"] = unresolvedTargetHint,
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The promise could not be created.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private void ApplyDialogueAddCanon(
        string provider,
        string actorId,
        string spokenText,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var speaker = Engine.EntityById(actorId);
        if (speaker is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        var text = FirstNonBlank(
            proposal.CanonText,
            proposal.Description,
            proposal.PromiseText,
            proposal.Name,
            proposal.ItemName);
        var subject = FirstNonBlank(proposal.Subject, proposal.Name, proposal.TargetHint, proposal.TargetEntityId);
        if (string.IsNullOrWhiteSpace(text))
        {
            RejectDialogueAction(provider, proposal, "The canon action did not include fact text.", deltas);
            return;
        }

        if (!GeneratedFactIsSupportedBySpokenText(text, subject, spokenText))
        {
            RejectDialogueAction(provider, proposal, "The canon fact was not supported by the NPC's spoken line.", deltas);
            return;
        }

        var attachedTo = FirstNonBlank(proposal.TargetEntityId, speaker.Id.Value)!;
        var kind = NormalizeToken(
            FirstNonBlank(
                proposal.CanonKind,
                proposal.FixtureType,
                proposal.PromiseKind,
                proposal.RealizationKind,
                "dialogue_fact")!,
            "dialogue_fact");
        var summary = FirstNonBlank(proposal.CanonSummary, proposal.Name, proposal.Subject, text)!;
        var tags = (proposal.Tags ?? Array.Empty<string>())
            .Concat(new[] { "dialogue", "canonized" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var applied = Engine.ApplyConsequence(WorldConsequence.AddCanon(
            $"dialogue:{provider}",
            kind,
            attachedTo,
            text.Trim(),
            summary.Trim(),
            tags,
            visibility: (proposal.PlayerVisible ?? false)
                ? WorldConsequenceVisibility.Message
                : WorldConsequenceVisibility.Hidden,
            sourceEntityId: speaker.Id.Value,
            evidence: spokenText,
            reason: "dialogue action: canonize fact",
            operation: "dialogueAddCanon",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "add_canon",
                ["speakerId"] = speaker.Id.Value,
                ["playerVisible"] = proposal.PlayerVisible ?? false,
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The canon fact could not be recorded.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private void ApplyDialogueOfferTrade(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var merchant = Engine.EntityById(actorId);
        if (merchant is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        var itemName = string.IsNullOrWhiteSpace(proposal.ItemName)
            ? null
            : proposal.ItemName.Trim();
        var applied = Engine.ApplyConsequence(WorldConsequence.OfferTrade(
            $"dialogue:{provider}",
            merchant.Id.Value,
            itemName,
            quantity: Math.Max(1, proposal.Quantity ?? 1),
            gold: Math.Max(0, proposal.Gold ?? 30),
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: merchant.Id.Value,
            evidence: proposal.Reason,
            reason: "dialogue action: offer trade",
            operation: "dialogueOfferTrade",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "offer_trade",
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The trade offer could not be applied.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private void ApplyDialogueSpawnFixture(
        string provider,
        string actorId,
        string type,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var actor = Engine.EntityById(actorId);
        if (actor is null || !actor.TryGet<PositionComponent>(out var actorPosition))
        {
            RejectDialogueAction(provider, proposal, "The speaker is missing or has no local position.", deltas);
            return;
        }

        var point = ResolveDialogueFixturePoint(actorPosition.Position, proposal);
        if (point is null)
        {
            RejectDialogueAction(provider, proposal, "No valid nearby tile was available for the fixture.", deltas);
            return;
        }

        var isMarker = type == "mark_location";
        var name = FirstNonBlank(proposal.Name, proposal.ItemName, proposal.Description, isMarker ? "marked place" : "strange fixture")!;
        var fixtureType = NormalizeToken(
            FirstNonBlank(proposal.FixtureType, isMarker ? "marker" : "feature")!,
            isMarker ? "marker" : "feature");
        var material = NormalizeToken(
            FirstNonBlank(proposal.Material, isMarker ? "chalk" : "wood")!,
            isMarker ? "chalk" : "wood");
        var tags = (proposal.Tags ?? Array.Empty<string>())
            .Concat(new[] { "dialogue", isMarker ? "marked" : "spawned" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var verbs = proposal.InteractableVerbs is { Count: > 0 }
            ? proposal.InteractableVerbs
            : new[] { "examine" };
        var blocksMovement = proposal.BlocksMovement ?? !isMarker;
        var blocksSight = proposal.BlocksSight ?? false;
        var operation = isMarker ? "dialogueMarkLocation" : "dialogueSpawnFixture";
        var applied = Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
            $"dialogue:{provider}",
            name,
            point.Value.X,
            point.Value.Y,
            prefix: NormalizeToken(name, fixtureType),
            glyph: isMarker ? '*' : '#',
            palette: fixtureType,
            fixtureType: fixtureType,
            material: material,
            tags: tags,
            blocksMovement: blocksMovement,
            blocksSight: blocksSight,
            description: proposal.Description,
            interactableVerbs: verbs,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: proposal.Reason,
            reason: isMarker ? "dialogue action: mark location" : "dialogue action: spawn fixture",
            operation: operation,
            message: isMarker
                ? $"{actor.Name} marks {name} nearby."
                : $"{actor.Name} reveals {name} nearby.",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = type,
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The fixture could not be created.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private GridPoint? ResolveDialogueFixturePoint(GridPoint actorPosition, DialogueActionProposal proposal)
    {
        if (proposal.X is not null && proposal.Y is not null)
        {
            var point = new GridPoint(proposal.X.Value, proposal.Y.Value);
            return GameEngine.Distance(actorPosition, point) <= 2 && IsOpenDialogueFixturePoint(point)
                ? point
                : null;
        }

        return AdjacentOffsets()
            .Select(offset => actorPosition.Translate(offset.X, offset.Y))
            .Where(IsOpenDialogueFixturePoint)
            .OrderBy(point => GameEngine.Distance(point, Engine.State.ControlledEntity.Get<PositionComponent>().Position))
            .ThenBy(point => point.X)
            .ThenBy(point => point.Y)
            .Select(point => (GridPoint?)point)
            .FirstOrDefault();
    }

    private bool IsOpenDialogueFixturePoint(GridPoint point) =>
        Engine.InBounds(point)
        && !Engine.State.BlockingTerrain.Contains(point)
        && Engine.BlockingEntityAt(point) is null;

    private void ApplyDialogueRevealService(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var serviceProvider = Engine.EntityById(actorId);
        if (serviceProvider is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        var serviceName = FirstNonBlank(proposal.Name, proposal.ItemName, "quiet service")!;
        var description = FirstNonBlank(proposal.Description, proposal.Reason, serviceName)!;
        var serviceId = NormalizeToken(FirstNonBlank(proposal.ServiceId, serviceName)!, "service");
        var effectKind = NormalizeToken(FirstNonBlank(proposal.EffectKind, InferServiceEffect(description, serviceName))!, "record_memory");
        var applied = Engine.ApplyConsequence(WorldConsequence.OfferService(
            $"dialogue:{provider}",
            serviceProvider.Id.Value,
            serviceId,
            serviceName,
            description,
            effectKind,
            goldCost: Math.Max(0, proposal.GoldCost ?? proposal.Gold ?? 0),
            itemCost: proposal.ItemCost,
            targetHint: proposal.TargetHint,
            tags: proposal.Tags,
            wantStatusOnComplete: proposal.WantStatusOnComplete,
            wantStakesOnComplete: proposal.WantStakesOnComplete,
            wantAddTagsOnComplete: proposal.WantAddTagsOnComplete,
            wantRemoveTagsOnComplete: proposal.WantRemoveTagsOnComplete,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: serviceProvider.Id.Value,
            evidence: proposal.Reason,
            reason: "dialogue action: reveal service",
            operation: "dialogueRevealService",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "reveal_service",
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The service offer could not be applied.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
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
                ["normalizedType"] = NormalizeDialogueActionType(type),
                ["itemName"] = proposal.ItemName,
                ["reason"] = reason,
                ["providerReason"] = proposal.Reason,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
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

    private IReadOnlyList<StateDelta> AddDialogueExchangeMemory(PreparedDialogueTurn turn, string spokenText)
    {
        var speaker = Engine.EntityById(turn.SpeakerId);
        if (speaker is null)
        {
            return Array.Empty<StateDelta>();
        }

        var text = $"{turn.SpeakerName} spoke with the sorcerer. Player: {turn.PlayerText} Reply: {spokenText}";
        return Engine.ApplyConsequence(WorldConsequence.RecordMemory(
            "dialogue_exchange",
            speaker.Id.Value,
            text,
            "conversation",
            2,
            shareable: false,
            sourceEntityId: Engine.State.ControlledEntityId.Value,
            evidence: turn.PlayerText,
            reason: "Generated dialogue exchange.",
            operation: "dialogueExchangeMemory",
            details: new Dictionary<string, object?>
            {
                ["speakerId"] = speaker.Id.Value,
                ["playerText"] = turn.PlayerText,
                ["spokenText"] = spokenText,
            })).Deltas;
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
        IReadOnlyList<string> validationIssues,
        DialogueRouteRecord? route = null)
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
            result.Deltas.Select(delta => delta.Operation).ToArray(),
            route,
            providerResult.Stats));
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
        if (_dialogueParser is NullDialogueClaimExtractor)
        {
            return;
        }

        var dialogue = result.Deltas.LastOrDefault(delta =>
            delta.Operation.Equals("dialogue", StringComparison.OrdinalIgnoreCase));
        if (dialogue is null)
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
            Engine.State.Claims.Records.TakeLast(DialogueRecentClaimLimit).ToArray(),
            Engine.State.ControlledEntityId.Value);

        var task = RunDialogueParserFlowAsync(request, CancellationToken.None);
        _pendingClaimExtractions.Add(new PendingClaimExtraction(
            request,
            task,
            _dialogueParser.RequiresSpokenTextSupport));
    }

    private async Task<DialogueParseMaterialization> RunDialogueParserFlowAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var routeRequest = DialogueParserCapabilityCatalog.BuildRouteRequest(request);
        var routeStopwatch = Stopwatch.StartNew();
        DialogueParserRouteResult routeResult;
        try
        {
            routeResult = await _dialogueParserRouter.RouteAsync(routeRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            routeResult = new DialogueParserRouteResult(
                _dialogueParserRouter.Name,
                "",
                TechnicalFailure: true,
                Error: ex.Message,
                HasMechanics: false,
                SelectedCapabilityIds: Array.Empty<string>());
        }

        routeStopwatch.Stop();
        var selection = DialogueParserCapabilityCatalog.Select(routeResult, routeRequest);
        var parserRequest = request with
        {
            SelectedParserCapabilityIds = selection.SelectedCapabilityIds,
            ParserCapabilityCards = selection.SelectedCards,
        };
        var routeRecord = CreateDialogueParserRouteRecord(
            routeRequest,
            routeResult,
            selection,
            routeStopwatch.ElapsedMilliseconds,
            parserRequestBytes: DialoguePayloadSizer.JsonUtf8Bytes(parserRequest),
            parserElapsedMs: null);
        if (!selection.HasMechanics || selection.SelectedCapabilityIds.Count == 0)
        {
            return new DialogueParseMaterialization(
                parserRequest,
                new DialogueParseResult(
                    _dialogueParser.Name,
                    routeResult.RawText,
                    TechnicalFailure: routeResult.TechnicalFailure,
                    Error: routeResult.TechnicalFailure ? routeResult.Error : null,
                    Proposals: routeResult.TechnicalFailure ? null : new DialogueProposalSet()),
                routeRecord);
        }

        var parserStopwatch = Stopwatch.StartNew();
        var parse = await _dialogueParser.ParseAsync(parserRequest, cancellationToken);
        parserStopwatch.Stop();
        routeRecord = routeRecord with
        {
            Metrics = routeRecord.Metrics is null
                ? null
                : routeRecord.Metrics with { ParserElapsedMs = parserStopwatch.ElapsedMilliseconds },
        };
        return new DialogueParseMaterialization(parserRequest, parse, routeRecord);
    }

    private static DialogueParserRouteRecord CreateDialogueParserRouteRecord(
        DialogueParserRouteRequest request,
        DialogueParserRouteResult result,
        DialogueParserCapabilitySelection selection,
        long routerElapsedMs,
        int? parserRequestBytes,
        long? parserElapsedMs) =>
        new(
            request,
            result.Provider,
            result.RawText,
            result.TechnicalFailure,
            result.Error,
            selection.HasMechanics,
            selection.SelectedCapabilityIds,
            selection.FallbackCapabilityIds,
            result.Reason,
            selection.UsedFallback,
            new DialogueParserRouteMetrics(
                request.AvailableCapabilities.Count,
                selection.SelectedCapabilityIds.Count,
                selection.FallbackCapabilityIds.Count,
                DialoguePayloadSizer.JsonUtf8Bytes(request),
                DialoguePayloadSizer.JsonUtf8Bytes(selection.SelectedCards),
                parserRequestBytes,
                Math.Max(0, routerElapsedMs),
                parserElapsedMs,
                selection.UnknownSelectedCapabilityIds));

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
        var extractionRecords = new List<DialogueClaimExtractionRecord>();
        var parseRecords = new List<DialogueParseRecord>();
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
                extractionRecords.Add(FailedClaimExtractionRecord(
                    pending,
                    _dialogueParser.Name,
                    error));
                parseRecords.Add(FailedDialogueParseRecord(
                    pending,
                    _dialogueParser.Name,
                    error));
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    pending.Request.SpeakerId,
                    $"Dialogue claim extraction failed: {error}",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = _dialogueParser.Name,
                        ["error"] = error,
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                continue;
            }

            if (pending.Task.IsCanceled)
            {
                extractionRecords.Add(FailedClaimExtractionRecord(
                    pending,
                    _dialogueParser.Name,
                    "canceled"));
                parseRecords.Add(FailedDialogueParseRecord(
                    pending,
                    _dialogueParser.Name,
                    "canceled"));
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    pending.Request.SpeakerId,
                    "Dialogue claim extraction was canceled.",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = _dialogueParser.Name,
                        ["error"] = "canceled",
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                continue;
            }

            var materialized = pending.Task.Result;
            var extraction = materialized.Parse;
            var parseRequest = materialized.Request;
            var claims = extraction.Proposals?.Claims ?? Array.Empty<DialogueClaimProposal>();
            extractionRecords.Add(new DialogueClaimExtractionRecord(
                parseRequest,
                extraction.Provider,
                extraction.RawText,
                extraction.TechnicalFailure,
                extraction.Error,
                claims,
                pending.RequiresSpokenTextSupport));
            parseRecords.Add(new DialogueParseRecord(
                parseRequest,
                extraction.Provider,
                extraction.RawText,
                extraction.TechnicalFailure,
                extraction.Error,
                extraction.Proposals,
                pending.RequiresSpokenTextSupport,
                materialized.ParserRoute));
            if (extraction.TechnicalFailure)
            {
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    parseRequest.SpeakerId,
                    $"Dialogue claim extraction failed: {extraction.Error ?? "unknown error"}",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = extraction.Provider,
                        ["error"] = extraction.Error,
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                continue;
            }

            ApplyDialogueProposalSet(
                ParserPreparedTurn(parseRequest),
                parseRequest,
                extraction.Provider,
                string.Join(" ", parseRequest.DialogueLines),
                intent: null,
                extraction.Proposals,
                pending.RequiresSpokenTextSupport,
                parserOrigin: true,
                messages,
                deltas);
        }

        if (messages.Count == 0 && deltas.Count == 0 && extractionRecords.Count == 0 && parseRecords.Count == 0)
        {
            return result;
        }

        return result with
        {
            Messages = result.Messages.Concat(messages).ToArray(),
            Deltas = result.Deltas.Concat(deltas).ToArray(),
            DialogueClaimExtractions = result.DialogueClaimExtractions.Concat(extractionRecords).ToArray(),
            DialogueParses = result.DialogueParses.Concat(parseRecords).ToArray(),
        };
    }

    private static DialogueClaimExtractionRecord FailedClaimExtractionRecord(
        PendingClaimExtraction pending,
        string provider,
        string error) =>
        new(
            pending.Request,
            provider,
            RawText: "",
            TechnicalFailure: true,
            Error: error,
            Claims: Array.Empty<DialogueClaimProposal>(),
            pending.RequiresSpokenTextSupport);

    private static DialogueParseRecord FailedDialogueParseRecord(
        PendingClaimExtraction pending,
        string provider,
        string error) =>
        new(
            pending.Request,
            provider,
            RawText: "",
            TechnicalFailure: true,
            Error: error,
            Proposals: null,
            pending.RequiresSpokenTextSupport);

    private static PreparedDialogueTurn ParserPreparedTurn(DialogueClaimRequest request) =>
        new(
            request.Turn,
            request.PlayerText,
            request.SpeakerId,
            request.SpeakerName,
            request.SpeakerTags,
            request.ListenerSoulId,
            SpeakerHostile: false,
            SpeakerProfile: null,
            SpeakerFaction: null,
            BondSummary: null);

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

        var intakeTransaction = GameTransaction.Begin(Engine.State);
        var deltaStart = deltas.Count;
        var messageStart = messages.Count;
        var appliedClaim = Engine.ApplyConsequence(WorldConsequence.RecordClaim(
            $"dialogue:{provider}",
            request.SpeakerId,
            request.ListenerSoulId,
            proposal.Text,
            category,
            subject,
            salience,
            confidence,
            playerVisible,
            tags,
            sourceEntityId: request.SpeakerId,
            evidence: proposal.Text,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
            }));
        messages.AddRange(appliedClaim.Messages);
        deltas.AddRange(appliedClaim.Deltas);
        if (!appliedClaim.Applied || string.IsNullOrWhiteSpace(appliedClaim.TargetId))
        {
            RollBackClaimIntakeTransaction(
                intakeTransaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                proposal.Text,
                appliedClaim.Deltas,
                appliedClaim.Error ?? "claim_record_rejected");
            return;
        }

        var record = Engine.State.Claims.Records.FirstOrDefault(claim =>
            claim.Id.Equals(appliedClaim.TargetId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            RollBackClaimIntakeTransaction(
                intakeTransaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                proposal.Text,
                Array.Empty<StateDelta>(),
                "claim_record_missing");
            return;
        }

        var memory = AddClaimMemory(request, record, messages, deltas);
        if (!memory.Applied)
        {
            RollBackClaimIntakeTransaction(
                intakeTransaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                record.Text,
                memory.Deltas,
                memory.Error ?? "claim_memory_rejected");
            return;
        }

        if (RumorSystem.ConsequenceFromClaim(Engine.State, record) is { } rumorConsequence)
        {
            var appliedRumor = Engine.ApplyConsequence(rumorConsequence);
            messages.AddRange(appliedRumor.Messages);
            deltas.AddRange(appliedRumor.Deltas);
            if (!appliedRumor.Applied)
            {
                RollBackClaimIntakeTransaction(
                    intakeTransaction,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    record.Text,
                    appliedRumor.Deltas,
                    appliedRumor.Error ?? "claim_rumor_rejected");
                return;
            }
        }

        intakeTransaction.Commit();

        if (proposal.UpdateBond)
        {
            ApplyBondClaim(request, provider, proposal, record, messages, deltas);
        }

        var immediateApplied = false;
        if (category.Equals("merchant_stock", StringComparison.OrdinalIgnoreCase))
        {
            immediateApplied |= ApplyMerchantStockClaim(request, proposal, record, messages, deltas);
        }

        if (category.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            immediateApplied |= ApplyServiceClaim(request, proposal, record, messages, deltas);
        }

        if (category.Equals("trade", StringComparison.OrdinalIgnoreCase))
        {
            immediateApplied |= ApplyTradeClaim(request, proposal, record, messages, deltas);
        }

        if (proposal.BindAsCanon)
        {
            immediateApplied |= ApplyCanonClaim(request, provider, proposal, record, category, messages, deltas);
        }

        if (ShouldBindClaimAsPromise(proposal, record, category, immediateApplied))
        {
            BindClaimAsPromise(request, proposal, record, messages, deltas);
        }
        else if (playerVisible)
        {
            AddVisibleClaimMessage(
                record,
                $"A claim settles into your journal: {record.Text}",
                messages,
                deltas,
                "claimJournalMessage");
        }
    }

    private static bool ShouldBindClaimAsPromise(
        DialogueClaimProposal proposal,
        ClaimRecord record,
        string category,
        bool immediateApplied)
    {
        if (proposal.BindAsPromise)
        {
            return true;
        }

        if (record.Salience < 3)
        {
            return false;
        }

        if (immediateApplied && category is "merchant_stock" or "service" or "trade")
        {
            return false;
        }

        return category is "site"
            or "town"
            or "landmark"
            or "person"
            or "item"
            or "merchant_stock"
            or "service"
            or "trade"
            or "threat"
            or "escape_route"
            or "route"
            or "prophecy"
            or "door_rule";
    }

    private void ApplyBondClaim(
        DialogueClaimRequest request,
        string provider,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.UpdateBond(
                $"dialogue_claim:{provider}",
                request.SpeakerId,
                request.ListenerSoulId,
                proposal.LoyaltyDelta,
                proposal.FearDelta,
                proposal.AdmirationDelta,
                proposal.ResentmentDelta,
                proposal.BondPosture,
                record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimBondShift",
                maxDelta: DialogueBondDeltaLimit,
                details: new Dictionary<string, object?>
                {
                    ["provider"] = provider,
                    ["proposalType"] = "bond",
                    ["claimId"] = record.Id,
                }),
            request.SpeakerId,
            request.SpeakerId,
            record.Text,
            "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "bond",
            });
    }

    private WorldConsequenceApplyResult ApplyClaimUpdate(
        ClaimRecord record,
        string? status,
        string? boundPromiseId,
        string? appliedTo,
        string? sourceEntityId,
        string? evidence,
        string operation,
        List<string> messages,
        List<StateDelta> deltas,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var applied = Engine.ApplyConsequence(WorldConsequence.UpdateClaim(
            $"dialogue_claim:{record.Id}",
            record.Id,
            status,
            boundPromiseId,
            appliedTo,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: sourceEntityId,
            evidence: evidence,
            operation: operation,
            details: details));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        return applied;
    }

    private bool ApplyClaimConsequenceWithStatus(
        ClaimRecord record,
        WorldConsequence consequence,
        string? appliedTo,
        string? sourceEntityId,
        string? evidence,
        string operation,
        List<string> messages,
        List<StateDelta> deltas,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var transaction = GameTransaction.Begin(Engine.State);
        var deltaStart = deltas.Count;
        var messageStart = messages.Count;
        var applied = Engine.ApplyConsequence(consequence);
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            RollBackClaimApplicationTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                record,
                applied.Deltas,
                applied.Error ?? $"{consequence.Type}_rejected",
                details);
            return false;
        }

        var update = ApplyClaimUpdate(
            record,
            status: "applied",
            boundPromiseId: null,
            appliedTo: appliedTo ?? applied.TargetId,
            sourceEntityId: sourceEntityId,
            evidence: evidence,
            operation: operation,
            messages,
            deltas,
            details: details);
        if (!update.Applied)
        {
            RollBackClaimApplicationTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                record,
                update.Deltas,
                update.Error ?? "claim_status_rejected",
                details);
            return false;
        }

        transaction.Commit();
        return true;
    }

    private static void RollBackClaimApplicationTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        List<string> messages,
        int messageStart,
        ClaimRecord record,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure,
        IReadOnlyDictionary<string, object?>? details)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        RemoveRangeFrom(messages, messageStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        var payload = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        payload["claimId"] = record.Id;
        payload["failure"] = failure;
        payload["rejectedCount"] = diagnostics.Count;
        payload["auditOnly"] = true;
        payload["playerVisible"] = false;
        deltas.Add(new StateDelta(
            "claimApplicationSkipped",
            record.Id,
            $"Claim application rolled back: {failure}.",
            payload));
    }

    private static void RollBackClaimIntakeTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        List<string> messages,
        int messageStart,
        string claimText,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        RemoveRangeFrom(messages, messageStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        deltas.Add(new StateDelta(
            "claimIntakeSkipped",
            "dialogue_claim",
            $"Claim intake rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["claimText"] = claimText,
                ["failure"] = failure,
                ["rejectedCount"] = diagnostics.Count,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private bool ApplyCanonClaim(
        DialogueClaimRequest request,
        string provider,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        string category,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var attachedTo = FirstNonBlank(proposal.TargetEntityId, proposal.MerchantId, request.SpeakerId)!;
        var kind = NormalizeToken(FirstNonBlank(proposal.CanonKind, category, "dialogue_fact")!, "dialogue_fact");
        var summary = FirstNonBlank(proposal.CanonSummary, record.Subject, record.Text)!;
        var tags = (proposal.Tags ?? Array.Empty<string>())
            .Concat(new[] { "dialogue", "dialogue_claim", "canonized", kind })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.AddCanon(
                $"dialogue_claim:{record.Id}",
                kind,
                attachedTo,
                record.Text,
                summary,
                tags,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                reason: "dialogue claim: canonize fact",
                operation: "claimAddCanon",
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = record.Id,
                    ["provider"] = provider,
                    ["proposalType"] = "add_canon",
                    ["playerVisible"] = false,
                }),
            attachedTo,
            request.SpeakerId,
            record.Text,
            "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "add_canon",
                ["canonKind"] = kind,
            });
    }

    private bool ApplyMerchantStockClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var merchant = ResolveMerchantForClaim(request, proposal);
        if (merchant is null)
        {
            return false;
        }

        var itemName = FirstNonBlank(proposal.ItemName, proposal.Subject, record.Subject);
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        return ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.AddMerchantStock(
                $"dialogue_claim:{record.Id}",
                merchant.Id.Value,
                itemName,
                visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimMerchantStock",
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = record.Id,
                    ["provider"] = "dialogue_claim",
                }),
            merchant.Id.Value,
            request.SpeakerId,
            record.Text,
            "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = "dialogue_claim",
                ["proposalType"] = "merchant_stock",
            });
    }

    private bool ApplyServiceClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var provider = ResolveServiceProviderForClaim(request, proposal, record);
        if (provider is null)
        {
            return false;
        }

        var serviceName = FirstNonBlank(proposal.ItemName, proposal.Subject, record.Subject, "quiet service")!;
        var serviceId = NormalizeToken(serviceName, "service");
        if (ProviderAlreadyOffersService(provider, serviceId, serviceName))
        {
            ApplyClaimUpdate(
                record,
                status: "applied",
                boundPromiseId: null,
                appliedTo: provider.Id.Value,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimApplied",
                messages,
                deltas,
                details: new Dictionary<string, object?>
                {
                    ["provider"] = "dialogue_claim",
                    ["proposalType"] = "service",
                    ["matchedExistingService"] = true,
                    ["serviceId"] = serviceId,
                });
            return true;
        }

        return ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.OfferService(
                $"dialogue_claim:{record.Id}",
                provider.Id.Value,
                serviceId,
                serviceName,
                record.Text,
                InferServiceEffect(record.Text, serviceName),
                targetHint: proposal.TriggerHint,
                tags: proposal.Tags,
                visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimOfferService",
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = record.Id,
                    ["provider"] = "dialogue_claim",
                }),
            appliedTo: null,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = "dialogue_claim",
                ["proposalType"] = "service",
            });
    }

    private static bool ProviderAlreadyOffersService(Entity provider, string serviceId, string serviceName) =>
        provider.TryGet<ServiceComponent>(out var services)
        && services.Offers.Any(offer =>
            offer.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(offer.Name, "service").Equals(serviceId, StringComparison.OrdinalIgnoreCase)
            || offer.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase));

    private bool ApplyTradeClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var traderId = FirstNonBlank(proposal.MerchantId, proposal.TargetEntityId, request.SpeakerId);
        if (traderId is null)
        {
            return false;
        }

        var itemName = FirstNonBlank(proposal.ItemName, proposal.Subject, record.Subject);
        return ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.OfferTrade(
                $"dialogue_claim:{record.Id}",
                traderId,
                itemName,
                visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimOfferTrade",
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = record.Id,
                    ["provider"] = "dialogue_claim",
                }),
            appliedTo: null,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = "dialogue_claim",
                ["proposalType"] = "trade",
            });
    }

    private void BindClaimAsPromise(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var realizationKind = NormalizeRealizationKind(proposal.RealizationKind, proposal.Category, record.Text);
        var triggerHint = NormalizePromiseTrigger(request, proposal, record, realizationKind);
        var existing = MatchingActivePromise(record, proposal, triggerHint, realizationKind);
        if (existing is not null)
        {
            var transaction = GameTransaction.Begin(Engine.State);
            var deltaStart = deltas.Count;
            var mergedTriggerHint = MergeTriggerHints(existing.TriggerHint, triggerHint);
            var mergedRealizationKind = MergeRealizationKind(existing.RealizationKind, realizationKind);
            var linkedPromise = existing;
            if (ShouldRebindExistingPromise(existing, mergedTriggerHint, mergedRealizationKind))
            {
                var updateResult = Engine.ApplyConsequence(WorldConsequence.UpdatePromise(
                    $"dialogue_claim:{record.Id}",
                    existing.Id,
                    status: "bound",
                    boundPlace: ShouldBindToRegion(mergedTriggerHint, mergedRealizationKind)
                        ? existing.BoundPlace ?? Engine.State.RegionId
                        : existing.BoundPlace,
                    boundTargetId: existing.BoundTargetId,
                    triggerHint: mergedTriggerHint,
                    realizationKind: mergedRealizationKind,
                    visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                    sourceEntityId: request.SpeakerId,
                    evidence: record.Text,
                    operation: "claimPromiseRebound",
                    emitMessage: false,
                    message: $"A repeated claim sharpens an existing promise: {existing.Text}",
                    details: new Dictionary<string, object?>
                    {
                        ["claimId"] = record.Id,
                        ["provider"] = "dialogue_claim",
                    }));
                messages.AddRange(updateResult.Messages);
                deltas.AddRange(updateResult.Deltas);
                if (!updateResult.Applied)
                {
                    RollBackClaimPromiseTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        record,
                        updateResult.Deltas,
                        updateResult.Error ?? "promise_rebind_rejected");
                    return;
                }

                if (updateResult.Applied)
                {
                    linkedPromise = Engine.State.PromiseLedger.Promises.FirstOrDefault(promise =>
                        promise.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase)) ?? existing;
                }
            }

            var updateClaim = ApplyClaimUpdate(
                record,
                status: "promised",
                boundPromiseId: linkedPromise.Id,
                appliedTo: null,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimPromiseLinked",
                messages,
                deltas,
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = linkedPromise.Id,
                    ["promiseStatus"] = linkedPromise.Status,
                    ["triggerHint"] = linkedPromise.TriggerHint,
                    ["realizationKind"] = linkedPromise.RealizationKind,
                    ["message"] = $"A repeated claim points back to an existing promise: {linkedPromise.Text}",
                });
            if (!updateClaim.Applied)
            {
                RollBackClaimPromiseTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    record,
                    updateClaim.Deltas,
                    updateClaim.Error ?? "claim_status_rejected");
                return;
            }

            transaction.Commit();
            return;
        }

        var createTransaction = GameTransaction.Begin(Engine.State);
        var createDeltaStart = deltas.Count;
        var shouldBindToRegion = ShouldBindToRegion(triggerHint, realizationKind);
        var applied = Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            $"dialogue_claim:{record.Id}",
            string.IsNullOrWhiteSpace(proposal.PromiseKind) ? "rumor" : proposal.PromiseKind.Trim(),
            record.Text,
            triggerHint: triggerHint,
            visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimPromise",
            playerVisible: record.PlayerVisible,
            salience: record.Salience,
            subject: record.Subject,
            claimedPlace: string.IsNullOrWhiteSpace(proposal.ClaimedPlace) ? null : proposal.ClaimedPlace,
            realizationKind: realizationKind,
            bindPlace: shouldBindToRegion ? Engine.State.RegionId : null,
            sourceClaimId: record.Id,
            sourceSpeakerId: request.SpeakerId,
            sourceListenerSoulId: request.ListenerSoulId,
            sourceConfidence: record.Confidence,
            useCurrentRegionAsClaimedPlace: false,
            autoBind: false,
            emitMessage: false,
            message: shouldBindToRegion
                ? $"A claim becomes a bound promise: {record.Text}"
                : $"A claim becomes a promise: {record.Text}",
            details: new Dictionary<string, object?>
            {
                ["claimId"] = record.Id,
                ["provider"] = "dialogue_claim",
            }));
        if (!applied.Applied)
        {
            messages.AddRange(applied.Messages);
            deltas.AddRange(applied.Deltas);
            return;
        }

        var promiseId = applied.TargetId ?? applied.Deltas.FirstOrDefault()?.Target;
        var bound = Engine.State.PromiseLedger.Promises.FirstOrDefault(promise =>
            promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase));
        if (bound is null)
        {
            RollBackClaimPromiseTransaction(
                createTransaction,
                deltas,
                createDeltaStart,
                record,
                applied.Deltas,
                "promise_missing_after_create");
            return;
        }

        deltas.AddRange(applied.Deltas);
        var updated = ApplyClaimUpdate(
            record,
            status: "promised",
            boundPromiseId: bound.Id,
            appliedTo: null,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimPromiseStatus",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = bound.Id,
                ["promiseStatus"] = bound.Status,
                ["triggerHint"] = bound.TriggerHint,
                ["realizationKind"] = bound.RealizationKind,
            });
        if (!updated.Applied)
        {
            RollBackClaimPromiseTransaction(
                createTransaction,
                deltas,
                createDeltaStart,
                record,
                updated.Deltas,
                updated.Error ?? "claim_status_rejected");
            return;
        }

        createTransaction.Commit();
        var message = bound.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            ? $"A claim becomes a bound promise: {bound.Text}"
            : $"A claim becomes a promise: {bound.Text}";
        if (record.PlayerVisible)
        {
            AddVisibleClaimMessage(
                record,
                message,
                messages,
                deltas,
                "claimPromiseMessage",
                new Dictionary<string, object?>
                {
                    ["promiseId"] = bound.Id,
                    ["promiseStatus"] = bound.Status,
                    ["triggerHint"] = bound.TriggerHint,
                    ["realizationKind"] = bound.RealizationKind,
                });
        }
    }

    private static void RollBackClaimPromiseTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        ClaimRecord record,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        deltas.Add(new StateDelta(
            "claimPromiseSkipped",
            record.Id,
            $"Claim-promise binding rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["claimId"] = record.Id,
                ["failure"] = failure,
                ["rejectedCount"] = diagnostics.Count,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
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

    // Below this Jaccard overlap of significant (4+ char) tokens, two claim texts are treated as
    // unrelated facts rather than a restatement of the same one, even from the same speaker.
    private const double FuzzyClaimPromiseTextOverlapThreshold = 0.6;

    private WorldPromise? MatchingActivePromise(
        ClaimRecord record,
        DialogueClaimProposal proposal,
        string triggerHint,
        string realizationKind)
    {
        var candidates = Engine.State.PromiseLedger.Promises
            .Where(promise =>
                !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
                && !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
                && PromiseRealizationKindsCompatible(promise.RealizationKind, realizationKind)
                && TriggerHintsOverlap(promise.TriggerHint, triggerHint)
                && (string.IsNullOrWhiteSpace(proposal.PromiseKind)
                    || promise.Kind.Equals(proposal.PromiseKind, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var exact = candidates.FirstOrDefault(promise =>
            promise.Text.Equals(record.Text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // A model that restates the same fact in different words should re-link to the existing
        // promise/rumor thread instead of forking a parallel one (ledger sprawl). Guardrail:
        // require shared provenance (same speaker or same non-empty subject) in addition to text
        // overlap, so two genuinely different facts that merely share common words never match.
        return candidates.FirstOrDefault(promise =>
            (SameNonBlank(promise.SourceSpeakerId, record.SpeakerId)
                || SameNonBlank(promise.Subject, record.Subject))
            && SignificantTokenOverlap(promise.Text, record.Text) >= FuzzyClaimPromiseTextOverlapThreshold);
    }

    private static bool SameNonBlank(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static double SignificantTokenOverlap(string left, string right)
    {
        var leftTokens = SignificantTokens(left);
        var rightTokens = SignificantTokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> SignificantTokens(string text) =>
        text
            .ToLowerInvariant()
            .Split(
                new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '\'', '"', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ShouldRebindExistingPromise(
        WorldPromise promise,
        string? triggerHint,
        string? realizationKind) =>
        !string.Equals(promise.TriggerHint, triggerHint, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(promise.RealizationKind, realizationKind, StringComparison.OrdinalIgnoreCase)
        || (ShouldBindToRegion(triggerHint, realizationKind) && string.IsNullOrWhiteSpace(promise.BoundPlace));

    private static bool PromiseRealizationKindsCompatible(string? existing, string requested)
    {
        var left = NormalizeToken(existing ?? "", "");
        var right = NormalizeToken(requested, "");
        return string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right)
            || left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || (IsPlaceLikeRealization(left) && IsPlaceLikeRealization(right))
            || (IsStockLikeRealization(left) && IsStockLikeRealization(right));
    }

    private static string? MergeRealizationKind(string? existing, string requested)
    {
        var left = NormalizeToken(existing ?? "", "");
        var right = NormalizeToken(requested, "");
        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right) ? null : right;
        }

        if (string.IsNullOrWhiteSpace(right) || left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            return left;
        }

        if ((IsPlaceLikeRealization(left) && right is "escape_route" or "route" or "door_rule")
            || (IsStockLikeRealization(left) && right is "merchant_stock" or "stock"))
        {
            return right;
        }

        if ((IsPlaceLikeRealization(right) && left is "escape_route" or "route" or "door_rule")
            || (IsStockLikeRealization(right) && left is "merchant_stock" or "stock"))
        {
            return left;
        }

        return right;
    }

    private static bool IsPlaceLikeRealization(string kind) =>
        kind is "site" or "town" or "landmark" or "escape_route" or "route" or "door_rule";

    private static bool IsStockLikeRealization(string kind) =>
        kind is "merchant_stock" or "stock" or "trade";

    private WorldConsequenceApplyResult AddClaimMemory(
        DialogueClaimRequest request,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var applied = Engine.ApplyConsequence(WorldConsequence.RecordMemory(
            $"claim:{record.Id}",
            request.SpeakerId,
            record.Text,
            $"claim:{record.Id}",
            record.Salience,
            shareable: true,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimMemory",
            details: new Dictionary<string, object?>
            {
                ["claimId"] = record.Id,
                ["speakerId"] = record.SpeakerId,
                ["category"] = record.Category,
            }));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        return applied;
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

    private Entity? ResolveServiceProviderForClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record)
    {
        if (!string.IsNullOrWhiteSpace(proposal.TargetEntityId)
            && Engine.EntityById(proposal.TargetEntityId) is { } target)
        {
            if (!target.Id.Value.Equals(request.SpeakerId, StringComparison.OrdinalIgnoreCase)
                || ClaimNamesSpeakerAsProvider(record.Text, request.SpeakerName))
            {
                return target;
            }

            return null;
        }

        return ClaimNamesSpeakerAsProvider(record.Text, request.SpeakerName)
            ? Engine.EntityById(request.SpeakerId)
            : null;
    }

    private IReadOnlyList<WorldMemoryRecord> RecentMemoriesFor(string speakerId) =>
        Engine.State.Memories.Records
            .Where(record => record.SubjectId.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                || record.Provenance.StartsWith("gift", StringComparison.OrdinalIgnoreCase)
                || record.Provenance.StartsWith("claim:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(DialogueRecentMemoryLimit)
            .ToArray();

    private void AddVisibleClaimMessage(
        ClaimRecord record,
        string message,
        List<string> messages,
        List<StateDelta> deltas,
        string operation,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var mergedDetails = new Dictionary<string, object?>(details ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase)
        {
            ["claimId"] = record.Id,
            ["category"] = record.Category,
            ["subject"] = record.Subject,
        };
        var applied = Engine.ApplyConsequence(WorldConsequence.Message(
            $"dialogue_claim:{record.Id}",
            message,
            visibility: WorldConsequenceVisibility.Journal,
            sourceEntityId: record.SpeakerId,
            evidence: record.Text,
            operation: operation,
            details: mergedDetails));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    // Shared with InteractionSystem's document/prop claim-seed promise binding, so a promise
    // whose realization kind can only ever pay off through travel (site/town/escape_route/etc.)
    // binds to the current region regardless of whether it came from dialogue or a read/examine
    // claim seed. Without this, non-anchored promise kinds that CanBindToRegion doesn't recognize
    // (e.g. "rumor") would never bind and could never become travel-realization candidates.
    internal static bool ShouldBindToRegion(string? triggerHint, string? realizationKind) =>
        TriggerMatches(triggerHint, "travel")
        && NormalizeToken(realizationKind ?? "", "site") is "site" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade" or "service" or "escape_route" or "door_rule" or "route";

    private static bool TriggerMatches(string? hint, string trigger) =>
        string.IsNullOrWhiteSpace(hint)
        || hint.Equals(trigger, StringComparison.OrdinalIgnoreCase)
        || hint.Equals("encounter", StringComparison.OrdinalIgnoreCase)
        || hint.Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals(trigger, StringComparison.OrdinalIgnoreCase));

    private static bool TriggerHintsOverlap(string? left, string? right)
    {
        var leftParts = TriggerHintParts(left);
        var rightParts = TriggerHintParts(right);
        return leftParts.Count == 0
            || rightParts.Count == 0
            || leftParts.Contains("encounter")
            || rightParts.Contains("encounter")
            || leftParts.Overlaps(rightParts);
    }

    private static HashSet<string> TriggerHintParts(string? hint) =>
        string.IsNullOrWhiteSpace(hint)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : hint.Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? MergeTriggerHints(string? existing, string? requested)
    {
        var parts = TriggerHintParts(existing);
        parts.UnionWith(TriggerHintParts(requested));
        if (parts.Count == 0)
        {
            return string.IsNullOrWhiteSpace(existing)
                ? string.IsNullOrWhiteSpace(requested) ? null : requested.Trim()
                : existing.Trim();
        }

        var preferredOrder = new[] { "travel", "talk", "read", "open", "inspect", "buy", "trade", "encounter" };
        return string.Join(
            ",",
            preferredOrder.Where(parts.Contains)
                .Concat(parts.Except(preferredOrder, StringComparer.OrdinalIgnoreCase).OrderBy(part => part, StringComparer.OrdinalIgnoreCase)));
    }

    private string NormalizePromiseTrigger(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        string realizationKind)
    {
        var triggerHint = string.IsNullOrWhiteSpace(proposal.TriggerHint) ? "travel" : proposal.TriggerHint.Trim();
        var categoryKind = NormalizeClaimCategoryAsRealization(record.Category);
        if ((categoryKind.Equals("service", StringComparison.OrdinalIgnoreCase)
                || realizationKind.Equals("service", StringComparison.OrdinalIgnoreCase))
            && ResolveServiceProviderForClaim(request, proposal, record) is null)
        {
            return "travel";
        }

        if (LooksLikeRoutePromise(record.Text) && !TriggerMatches(triggerHint, "travel"))
        {
            return "travel";
        }

        return triggerHint;
    }

    private static string NormalizeRealizationKind(string? realizationKind, string category, string? text = null)
    {
        var categoryKind = NormalizeClaimCategoryAsRealization(category);
        var requestedKind = NormalizeRealizationToken(realizationKind ?? category);
        if (LooksLikeMerchantStockPromise(text))
        {
            return "merchant_stock";
        }

        if ((categoryKind.Equals("site", StringComparison.OrdinalIgnoreCase)
                || requestedKind.Equals("site", StringComparison.OrdinalIgnoreCase))
            && LooksLikeRoutePromise(text))
        {
            return "escape_route";
        }

        if (CategoryControlsRealization(categoryKind) && !requestedKind.Equals(categoryKind, StringComparison.OrdinalIgnoreCase))
        {
            return categoryKind;
        }

        return requestedKind;
    }

    private static string NormalizeClaimCategoryAsRealization(string category) =>
        NormalizeRealizationToken(category);

    private static string NormalizeRealizationToken(string? text)
    {
        var normalized = NormalizeToken(text ?? "", "memory");
        return normalized switch
        {
            "place" or "site" or "town" or "landmark" => "site",
            "merchant_stock" or "stock" or "ware" or "wares" or "trade" => "merchant_stock",
            "npc" or "person" or "relative" => "person",
            "enemy" or "danger" or "threat" => "threat",
            "item" or "blade" or "weapon" => "item",
            "service" or "folk_magic" or "folk_magic_service" => "service",
            "route" or "escape_route" or "hidden_exit" or "tunnel" => "escape_route",
            "door" or "door_rule" or "lock" => "door_rule",
            _ => normalized,
        };
    }

    private static bool CategoryControlsRealization(string categoryKind) =>
        categoryKind is "merchant_stock" or "service" or "escape_route" or "door_rule" or "threat";

    private static bool LooksLikeRoutePromise(string? text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("route", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("road", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("hidden exit", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("escape", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("tunnel", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("drain", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("grate", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("passage", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("way out", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMerchantStockPromise(string? text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("merchant", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("seller", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("sells", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("sell you", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("wares", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("trades", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("for sale", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ClaimNamesSpeakerAsProvider(string text, string speakerName)
    {
        var lower = $" {text.Trim().ToLowerInvariant()} ";
        if (lower.Contains(" i can ", StringComparison.OrdinalIgnoreCase)
            || lower.Contains(" i know ", StringComparison.OrdinalIgnoreCase)
            || lower.Contains(" i will ", StringComparison.OrdinalIgnoreCase)
            || lower.Contains(" i'll ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var name in SpeakerNameTokens(speakerName))
        {
            if (lower.Contains($" {name} can ", StringComparison.OrdinalIgnoreCase)
                || lower.Contains($" {name} knows ", StringComparison.OrdinalIgnoreCase)
                || lower.Contains($" {name} offers ", StringComparison.OrdinalIgnoreCase)
                || lower.Contains($" {name} will ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SpeakerNameTokens(string speakerName)
    {
        var names = new List<string>();
        var full = speakerName.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(full))
        {
            names.Add(full);
        }

        var first = full.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(first))
        {
            names.Add(first);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string InferServiceEffect(string text, string serviceName)
    {
        var lower = $"{text} {serviceName}".ToLowerInvariant();
        if (lower.Contains("door", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("lock", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("unlock", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("ward", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return "open_or_unlock";
        }

        if (lower.Contains("route", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("tunnel", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("drain", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("passage", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("escape", StringComparison.OrdinalIgnoreCase))
        {
            return "create_route";
        }

        return "record_memory";
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

    private static string? TextPayload(IReadOnlyDictionary<string, object?>? map, string key) =>
        map is null ? null : ReadString(map, key);

    private static int? IntPayload(IReadOnlyDictionary<string, object?>? map, string key)
    {
        if (map is null || !map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            int integer => integer,
            long longInteger when longInteger >= int.MinValue && longInteger <= int.MaxValue => (int)longInteger,
            double number => (int)Math.Round(number),
            float number => (int)Math.Round(number),
            decimal number => (int)Math.Round(number),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

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

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

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
        var transaction = GameTransaction.Begin(Engine.State);
        var stagedDeltas = new List<StateDelta>();
        var stagedMessages = new List<string>();
        var runStatus = Engine.ApplyConsequence(WorldConsequence.UpdateRunStatus(
            "run_end",
            status,
            conclusion,
            target,
            sourceEntityId: Engine.State.ControlledEntityId.Value,
            evidence: conclusion,
            operation: "runComplete",
            message: message));
        if (!runStatus.Applied)
        {
            RollBackRunCompletionTransaction(
                transaction,
                stagedDeltas,
                status,
                target,
                runStatus.Deltas,
                runStatus.Error ?? "Run status could not be updated.");
            return result with
            {
                Deltas = result.Deltas.Concat(stagedDeltas).ToArray(),
            };
        }

        stagedDeltas.AddRange(runStatus.Deltas);
        var runMessage = Engine.ApplyConsequence(WorldConsequence.Message(
            "run_end",
            message,
            targetEntityId: target,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: Engine.State.ControlledEntityId.Value,
            evidence: conclusion,
            operation: "runCompleteMessage",
            details: new Dictionary<string, object?>
            {
                ["status"] = status,
                ["conclusion"] = conclusion,
            }));
        if (!runMessage.Applied)
        {
            RollBackRunCompletionTransaction(
                transaction,
                stagedDeltas,
                status,
                target,
                runMessage.Deltas,
                runMessage.Error ?? "Run completion message could not be recorded.");
            return result with
            {
                Deltas = result.Deltas.Concat(stagedDeltas).ToArray(),
            };
        }

        stagedDeltas.AddRange(runMessage.Deltas);
        stagedMessages.AddRange(runMessage.Messages);
        var chronicle = RunChronicle.Build(Engine.State);
        var canon = Engine.ApplyConsequence(WorldConsequence.AddCanon(
            "run_end",
            "chronicle",
            Engine.State.ControlledEntityId.Value,
            chronicle.Text,
            chronicle.Conclusion,
            new[] { "chronicle", status },
            evidence: chronicle.Text,
            operation: "runChronicle"));
        if (!canon.Applied)
        {
            RollBackRunCompletionTransaction(
                transaction,
                stagedDeltas,
                status,
                target,
                canon.Deltas,
                canon.Error ?? "Run chronicle could not be recorded.");
            return result with
            {
                Deltas = result.Deltas.Concat(stagedDeltas).ToArray(),
            };
        }

        stagedDeltas.AddRange(canon.Deltas);
        transaction.Commit();
        return result with
        {
            ShouldQuit = true,
            Messages = result.Messages.Concat(stagedMessages).ToArray(),
            Deltas = result.Deltas.Concat(stagedDeltas).ToArray(),
        };
    }

    private static void RollBackRunCompletionTransaction(
        GameTransaction transaction,
        List<StateDelta> stagedDeltas,
        string status,
        string target,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        stagedDeltas.Clear();
        stagedDeltas.AddRange(FailureDiagnostics(failedDeltas));
        var rejectedCount = FailureDiagnostics(failedDeltas).Count;
        stagedDeltas.Add(new StateDelta(
            "runCompleteSkipped",
            target,
            $"Run completion rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["status"] = status,
                ["target"] = target,
                ["failure"] = failure,
                ["rejectedCount"] = rejectedCount,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
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
            or AtlasCommand
            or JournalCommand
            or RumorsCommand
            or CharacterCommand
            or ReagentsCommand
            or WaresCommand
            or ServicesCommand
            or BondsCommand
            or StandingCommand
            or FollowersCommand
            or JobsCommand
            or SaveCommand
            or LoadCommand
            or HelpCommand
            or QuitCommand;

    private void RefreshPendingCastResolution()
    {
        var pending = _pendingCast;
        if (pending?.Resolution is not null
            || pending?.ResolutionTask is not { IsCompleted: true } task)
        {
            return;
        }

        _pendingCast = pending with
        {
            Resolution = MaterializeCompletedPendingCast(pending, task),
            ResolutionTask = null,
            Cancellation = null,
        };
        DisposePendingCastResolutionOwnership(pending);
    }

    private async Task<PendingCastMaterialization?> FlushPendingCastResolutionAsync(CancellationToken cancellationToken)
    {
        var pending = _pendingCast;
        if (pending is null)
        {
            return null;
        }

        var resolution = await MaterializePendingCastAsync(pending, cancellationToken);
        if (_pendingCast?.Id == pending.Id)
        {
            _pendingCast = pending with
            {
                Resolution = resolution,
                ResolutionTask = null,
                Cancellation = null,
            };
        }

        DisposePendingCastResolutionOwnership(pending);
        return new PendingCastMaterialization(pending.Id, resolution);
    }

    private async Task<MaterializedMagicResolution> MaterializePendingCastAsync(
        PendingCast pending,
        CancellationToken cancellationToken)
    {
        if (pending.Resolution is not null)
        {
            return pending.Resolution;
        }

        try
        {
            if (pending.ResolutionTask is not null)
            {
                return await pending.ResolutionTask.WaitAsync(cancellationToken);
            }

            return await _magic.ResolveAsync(Engine, pending.Command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PendingCastTechnicalResolution(pending, ex.Message);
        }
    }

    private static MaterializedMagicResolution MaterializeCompletedPendingCast(
        PendingCast pending,
        Task<MaterializedMagicResolution> task)
    {
        if (task.IsCanceled)
        {
            return PendingCastTechnicalResolution(pending, "Pending spell resolution was cancelled.");
        }

        if (task.IsFaulted)
        {
            return PendingCastTechnicalResolution(
                pending,
                task.Exception?.GetBaseException().Message ?? "Pending spell resolution failed.");
        }

        return task.Result;
    }

    private PendingCastSave? PendingCastToSave()
    {
        RefreshPendingCastResolution();
        return _pendingCast is null
            ? null
            : new PendingCastSave(
                _pendingCast.Id,
                _pendingCast.Command.Text,
                _pendingCast.Command.Performance,
                _pendingCast.Resolution);
    }

    private static PendingCastView BuildPendingCastView(PendingCast pending)
    {
        var resolution = pending.Resolution;
        return new PendingCastView(
            pending.Id,
            pending.Command.Text,
            PendingCastState(pending),
            resolution?.Provider,
            resolution?.Accepted,
            resolution?.TechnicalFailure,
            resolution?.Error,
            resolution?.EffectTypes);
    }

    private static string PendingCastState(PendingCast pending)
    {
        if (pending.Resolution is { TechnicalFailure: true })
        {
            return "failed";
        }

        if (pending.Resolution is not null)
        {
            return "ready";
        }

        return pending.ResolutionTask is not null ? "resolving" : "waiting";
    }

    private static void CancelPendingCastResolution(PendingCast pending)
    {
        if (pending.Cancellation is null)
        {
            return;
        }

        pending.Cancellation.Cancel();
        if (pending.ResolutionTask is null)
        {
            pending.Cancellation.Dispose();
            return;
        }

        _ = pending.ResolutionTask.ContinueWith(
            _ => pending.Cancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void DisposePendingCastResolutionOwnership(PendingCast pending)
    {
        if (pending.Cancellation is null)
        {
            return;
        }

        if (pending.ResolutionTask is { IsCompleted: false })
        {
            _ = pending.ResolutionTask.ContinueWith(
                _ => pending.Cancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        pending.Cancellation.Dispose();
    }

    private static MaterializedMagicResolution PendingCastTechnicalResolution(
        PendingCast pending,
        string error) =>
        new(
            "pending_cast",
            pending.Command.Text,
            pending.Command.Performance ?? CastPerformance.Neutral,
            RawText: "",
            Accepted: false,
            TechnicalFailure: true,
            Error: error,
            EffectTypes: Array.Empty<string>(),
            ResolvedMagicJson: null);

    private static MagicResolutionRecord ToMagicRecord(MaterializedMagicResolution resolution) =>
        new(
            resolution.Provider,
            resolution.Accepted,
            resolution.TechnicalFailure,
            resolution.EffectTypes,
            resolution.Error)
        {
            ResolvedMagicJson = resolution.ResolvedMagicJson,
        };

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

    private sealed record PendingCast(
        string Id,
        CastCommand Command,
        MaterializedMagicResolution? Resolution = null,
        Task<MaterializedMagicResolution>? ResolutionTask = null,
        CancellationTokenSource? Cancellation = null);

    private sealed record PendingCastMaterialization(
        string Id,
        MaterializedMagicResolution Resolution);

    private sealed record PreparedDialogueRoute(
        DialogueRouteRecord Record,
        DialogueContextAssembly Assembly,
        DialogueRouteSelection Selection);

    private sealed record DialogueParseMaterialization(
        DialogueClaimRequest Request,
        DialogueParseResult Parse,
        DialogueParserRouteRecord ParserRoute);

    private sealed record PendingClaimExtraction(
        DialogueClaimRequest Request,
        Task<DialogueParseMaterialization> Task,
        bool RequiresSpokenTextSupport);
}
