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

public sealed partial class GameSession
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
            // In checkpoint mode a killing blow rewinds to the last safe rest instead of ending the
            // run (Phase 2.5); only when there is no checkpoint to fall back on does the body die.
            if (TryRestoreCheckpoint(ref result))
            {
                return result;
            }

            // The body is disposed of in the register of whoever struck it down (Phase 2.6).
            var treatment = DeathTreatment.ForDefeat(Engine.State.LastControlledDamageProvenance);
            return CompleteRun(
                result,
                "defeat",
                "The sorcerer's current body is dead.",
                Engine.State.ControlledEntityId.Value,
                DeathTreatment.Disposition(treatment));
        }

        // The run continues: record a checkpoint if the sorcerer is resting somewhere safe.
        MaybeCaptureCheckpoint();
        return result;
    }

    // Phase 2.5 checkpoint mode. The snapshot is held only in memory: it is a within-session safety
    // net, orthogonal to the ordinary run save (which carries the run across quit/load). GameState's
    // full-fidelity snapshot -- the same one that backs transactional rollback -- is authoritative,
    // so a restore rewinds the whole world to the rest, not a curated subset.
    private GameStateSnapshot? _checkpoint;

    private bool IsCheckpointMode() =>
        Engine.State.RunMode.Equals("checkpoint", StringComparison.OrdinalIgnoreCase);

    // A safe settlement rest: standing in a settlement place with no hostile the sorcerer can
    // perceive. The imprisonment start is a settlement place too, but its guards are perceivable
    // hostiles, so it never qualifies -- a checkpoint only forms once the sorcerer is actually safe.
    private bool IsSafeRest() =>
        Engine.CurrentPlace.Settlement is not null
        && Engine.FindNearestHostile() is null;

    private void MaybeCaptureCheckpoint()
    {
        if (IsCheckpointMode() && IsSafeRest())
        {
            _checkpoint = GameStateSnapshot.Capture(Engine.State);
        }
    }

    private bool TryRestoreCheckpoint(ref ActionResult result)
    {
        if (!IsCheckpointMode() || _checkpoint is null)
        {
            return false;
        }

        _checkpoint.Restore(Engine.State);
        Engine.State.RestorationCount++;
        const string message = "You come to at your last safe rest, the killing blow already thinning into a bad dream.";
        Engine.State.AddMessage(message);
        result = result with
        {
            Messages = result.Messages.Append(message).ToArray(),
        };
        return true;
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
