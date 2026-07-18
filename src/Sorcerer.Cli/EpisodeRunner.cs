using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Views;
using Sorcerer.Magic;
using Sorcerer.Magic.Auditing;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Cli;

public sealed record EpisodeRunnerOptions(
    int Episodes,
    int MaxTurns,
    int Seed,
    string? LogPath,
    string? QuickstartScene);

public static class EpisodeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private static readonly string[] SpellCycle =
    {
        "bind the nearest enemy in sticky blue webbing",
        "summon a friendly brass moth that bites enemies",
        "push the nearest soldier away with a rude wind",
        "heal my wounds with green light",
        "promise that the cell door will remember my name",
        "lightning storm blasts all enemies",
    };

    public static async Task<int> RunAsync(
        ISpellProvider provider,
        IDialogueProvider dialogueProvider,
        IDialogueRouter dialogueRouter,
        IDialogueParserRouter dialogueParserRouter,
        IDialogueParser dialogueParser,
        IDialogueAuditSink dialogueAudit,
        ISpellAuditSink audit,
        IBackgroundTextGenerator? backgroundTextGenerator,
        EpisodeRunnerOptions options,
        bool json,
        CancellationToken cancellationToken = default)
    {
        var summaries = new List<EpisodeSummary>();
        StreamWriter? writer = null;
        if (!string.IsNullOrWhiteSpace(options.LogPath))
        {
            var directory = Path.GetDirectoryName(options.LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            writer = new StreamWriter(options.LogPath, append: false);
        }

        try
        {
            for (var episode = 1; episode <= Math.Max(1, options.Episodes); episode++)
            {
                var seed = options.Seed + episode - 1;
                var session = GameSession.CreateImperialEncounter(
                    new WildMagicController(provider, audit: audit),
                    seed: seed,
                    dialogueRouter: dialogueRouter,
                    dialogueParserRouter: dialogueParserRouter,
                    dialogueParser: dialogueParser,
                    dialogueProvider: dialogueProvider,
                    dialogueAudit: dialogueAudit,
                    backgroundTextGenerator: backgroundTextGenerator);
                Program.ApplyQuickstart(session, options.QuickstartScene);

                var summary = await RunEpisodeAsync(
                    episode,
                    seed,
                    session,
                    Math.Max(1, options.MaxTurns),
                    options.QuickstartScene,
                    writer,
                    cancellationToken);
                summaries.Add(summary);
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }

        var report = new EpisodeReport(
            Provider: provider.Name,
            Episodes: summaries.Count,
            Passed: summaries.Count(summary => summary.Passed),
            Failed: summaries.Count(summary => !summary.Passed),
            Summaries: summaries);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine(
                $"Episode runner: {report.Passed}/{report.Episodes} passed with provider {provider.Name}.");
            foreach (var summary in summaries)
            {
                var mark = summary.Passed ? "PASS" : "FAIL";
                Console.WriteLine(
                    $"{mark} | episode {summary.Episode} | seed {summary.Seed} | turn {summary.FinalTurn} | steps {summary.Steps} | hp {summary.PlayerHitPoints}/{summary.PlayerMaxHitPoints}");
                if (summary.Issues.Count > 0)
                {
                    Console.WriteLine($"  issues: {string.Join(" / ", summary.Issues)}");
                }
            }

            if (!string.IsNullOrWhiteSpace(options.LogPath))
            {
                Console.WriteLine($"JSONL steps: {options.LogPath}");
            }
        }

        return report.Failed == 0 ? 0 : 1;
    }

    private static async Task<EpisodeSummary> RunEpisodeAsync(
        int episode,
        int seed,
        GameSession session,
        int maxTurns,
        string? quickstartScene,
        StreamWriter? writer,
        CancellationToken cancellationToken)
    {
        await WriteLogAsync(writer, new EpisodeStartRecord(
            RecordType: "episode_start",
            Episode: episode,
            Seed: seed,
            MaxTurns: maxTurns,
            Quickstart: true,
            QuickstartScene: quickstartScene,
            InitialObservation: session.Observation(debug: true)));

        var issues = new List<string>();
        var step = 0;
        var maxSteps = maxTurns * 4;
        while (session.View().Turn < maxTurns && step < maxSteps)
        {
            var command = ChooseCommand(session, step);
            var commandText = CommandToText(command);
            var result = await session.ExecuteAsync(command, cancellationToken);
            var validation = session.Engine.ValidateState();
            var stepIssues = CheckInvariants(result, validation);
            issues.AddRange(stepIssues);

            var observation = session.Observation(debug: true);
            var player = PlayerCard(observation.View);
            var record = new EpisodeStepRecord(
                RecordType: "episode_step",
                Episode: episode,
                Seed: seed,
                Step: step,
                Command: commandText,
                Action: result.Action,
                Success: result.Success,
                ConsumedTurn: result.ConsumedTurn,
                TechnicalFailure: result.TechnicalFailure,
                TurnBefore: result.TurnBefore,
                TurnAfter: result.TurnAfter,
                Result: result,
                Messages: result.Messages.Take(6).ToArray(),
                MagicEffects: result.Magic?.EffectTypes ?? Array.Empty<string>(),
                Issues: stepIssues,
                PlayerHitPoints: player?.HitPoints,
                PlayerMaxHitPoints: player?.MaxHitPoints,
                EntityCount: observation.Debug?.EntityCount ?? observation.View.Entities.Count,
                PromiseCount: observation.View.Promises.Count,
                PendingCast: observation.PendingCast?.State,
                Observation: observation);

            await WriteLogAsync(writer, record);

            step++;
            if (stepIssues.Count > 0 || result.ShouldQuit || IsFinished(session))
            {
                break;
            }
        }

        var finalView = session.View();
        var finalPlayer = PlayerCard(finalView);
        if (step >= maxSteps && finalView.Turn < maxTurns)
        {
            issues.Add($"episode reached {maxSteps} steps before turn {maxTurns}; too many free/failed actions");
        }

        issues.AddRange(CheckLongRunInvariants(session));

        var summary = new EpisodeSummary(
            Episode: episode,
            Seed: seed,
            Passed: issues.Count == 0,
            Steps: step,
            FinalTurn: finalView.Turn,
            PlayerHitPoints: finalPlayer?.HitPoints,
            PlayerMaxHitPoints: finalPlayer?.MaxHitPoints,
            PlayerAlive: finalPlayer is null || finalPlayer.HitPoints is null || finalPlayer.HitPoints > 0,
            HostilesAlive: Hostiles(finalView).Count,
            Promises: finalView.Promises.Count,
            Issues: issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        await WriteLogAsync(writer, new EpisodeFinalRecord(
            RecordType: "episode_final",
            Summary: summary,
            FinalObservation: session.Observation(debug: true)));

        return summary;
    }

    private static async Task WriteLogAsync(StreamWriter? writer, object record)
    {
        if (writer is null)
        {
            return;
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions));
        await writer.FlushAsync();
    }

    private static GameCommand ChooseCommand(GameSession session, int step)
    {
        var observation = session.Observation(debug: true);
        if (observation.PendingCast is not null)
        {
            return new AwaitCastCommand();
        }

        var view = observation.View;
        var player = PlayerCard(view);
        if (player is null)
        {
            return new InspectCommand();
        }

        if (step == 0)
        {
            return new InspectCommand();
        }

        var socialProbe = ChooseSocialProbe(session, view, player, step);
        if (socialProbe is not null)
        {
            return socialProbe;
        }

        var redTincture = FindInventory(view, "red tincture");
        if (player.HitPoints is { } hp
            && player.MaxHitPoints is { } maxHp
            && hp <= Math.Max(1, maxHp / 2)
            && redTincture is not null)
        {
            return new UseItemCommand(redTincture.Name);
        }

        var wand = FindInventory(view, "charcoal wand");
        if (wand is not null && !wand.Equipped)
        {
            return new EquipCommand(wand.Name);
        }

        if (wand is not null && wand.Equipped && !wand.Focused)
        {
            return new FocusCommand(wand.Name);
        }

        var nearbyItem = view.Entities
            .Where(entity => entity.Id != view.ControlledEntityId)
            .Where(entity => TagsContain(entity, "item"))
            .Where(entity => Distance(player, entity) <= 1)
            .OrderBy(entity => Distance(player, entity))
            .ThenBy(entity => entity.Id)
            .FirstOrDefault();
        if (nearbyItem is not null)
        {
            return new PickupCommand(nearbyItem.Name);
        }

        var nearbyUnread = view.Entities
            .Where(entity => TagsContain(entity, "readable"))
            .Where(entity => Distance(player, entity) <= 1)
            .Where(entity => !session.Engine.State.Canon.Records.Any(record =>
                record.AttachedTo.Equals(entity.Id, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entity => Distance(player, entity))
            .FirstOrDefault();
        if (nearbyUnread is not null)
        {
            return new ReadCommand(nearbyUnread.Name);
        }

        var cellDoor = view.Entities.FirstOrDefault(entity => TagsContain(entity, "door") && TagsContain(entity, "cell"));
        if (cellDoor is not null
            && Distance(player, cellDoor) <= 1
            && IsClosedDoor(session, cellDoor.Id)
            && FindInventory(view, "imperial cell key") is not null)
        {
            return new OpenCommand(cellDoor.Name);
        }

        var hostiles = Hostiles(view);
        if (hostiles.Count > 0)
        {
            var nearest = hostiles.OrderBy(entity => Distance(player, entity)).First();
            if (Distance(player, nearest) <= 1)
            {
                return new MoveCommand(DirectionToward(player, nearest));
            }

            if (step % 3 == 0)
            {
                return new CastCommand(SpellCycle[(step / 3) % SpellCycle.Length], CastPerformance.Neutral);
            }
        }

        var destination = ChooseDestination(session, view, player);
        return destination is null
            ? new WaitCommand()
            : new MoveCommand(DirectionToward(player, destination));
    }

    private static GameCommand? ChooseSocialProbe(GameSession session, GameView view, EntityCard player, int step)
    {
        if (step % 17 == 5
            || (step % 7 == 3 && view.Promises.Any(promise => promise.PlayerVisible)))
        {
            return new JournalCommand();
        }

        if (step % 19 == 7
            && (view.Rumors?.Count ?? 0) > 0)
        {
            return new RumorsCommand();
        }

        if (Hostiles(view).Any(hostile => Distance(player, hostile) <= 2))
        {
            return null;
        }

        var speaker = NearbyFriendlyTalker(view, player);
        if (speaker is null)
        {
            return null;
        }

        if (FindInventory(view, "grave salt") is not null
            && !HasGiftMemory(session, speaker.Id))
        {
            return new GiveCommand("grave salt", speaker.Name);
        }

        if (HasGiftMemory(session, speaker.Id)
            && !HasBondWithPlayer(session, speaker.Id))
        {
            return new TalkCommand($"{speaker.Name}, I meant that kindly. Can you trust me with one useful thing?");
        }

        if (!HasClaimAbout(session, "Hollowmere")
            && !HasPromiseAbout(view, "Hollowmere"))
        {
            return new TalkCommand($"{speaker.Name}, what road or town waits south of here?");
        }

        if (!HasClaimAbout(session, "fine blade")
            && !HasPromiseAbout(view, "fine blade"))
        {
            return new TalkCommand($"{speaker.Name}, do you know anyone who can sell me a fine blade?");
        }

        return null;
    }

    private static EntityCard? ChooseDestination(GameSession session, GameView view, EntityCard player)
    {
        if (FindInventory(view, "imperial cell key") is null)
        {
            var key = view.Entities.FirstOrDefault(entity =>
                entity.Name.Contains("key", StringComparison.OrdinalIgnoreCase)
                || entity.Id.Contains("key", StringComparison.OrdinalIgnoreCase));
            if (key is not null)
            {
                return key;
            }
        }

        if (FindInventory(view, "red tincture") is null)
        {
            var tincture = view.Entities.FirstOrDefault(entity =>
                entity.Name.Contains("tincture", StringComparison.OrdinalIgnoreCase));
            if (tincture is not null)
            {
                return tincture;
            }
        }

        var unread = view.Entities
            .Where(entity => TagsContain(entity, "readable"))
            .FirstOrDefault(entity => !session.Engine.State.Canon.Records.Any(record =>
                record.AttachedTo.Equals(entity.Id, StringComparison.OrdinalIgnoreCase)));
        if (unread is not null)
        {
            return unread;
        }

        if (FindInventory(view, "imperial cell key") is not null)
        {
            var door = view.Entities.FirstOrDefault(entity =>
                TagsContain(entity, "door")
                && TagsContain(entity, "cell")
                && IsClosedDoor(session, entity.Id));
            if (door is not null)
            {
                return door;
            }
        }

        return Hostiles(view)
            .OrderBy(entity => Distance(player, entity))
            .FirstOrDefault();
    }

    private static EntityCard? NearbyFriendlyTalker(GameView view, EntityCard player) =>
        view.Entities
            .Where(entity => entity.Id != view.ControlledEntityId)
            .Where(entity => TagsContain(entity, "npc") || TagsContain(entity, "resident") || TagsContain(entity, "prisoner"))
            .Where(entity => !string.Equals(entity.Faction, "empire", StringComparison.OrdinalIgnoreCase))
            .Where(entity => Distance(player, entity) <= 2)
            .OrderByDescending(entity => TagsContain(entity, "prisoner"))
            .ThenBy(entity => Distance(player, entity))
            .ThenBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static bool HasGiftMemory(GameSession session, string entityId) =>
        session.Engine.State.Memories.Records.Any(memory =>
            memory.SubjectId.Equals(entityId, StringComparison.OrdinalIgnoreCase)
            && (memory.Provenance.StartsWith("gift", StringComparison.OrdinalIgnoreCase)
                || memory.Text.Contains("accepted", StringComparison.OrdinalIgnoreCase)));

    private static bool HasBondWithPlayer(GameSession session, string entityId)
    {
        var entity = session.Engine.EntityById(entityId);
        if (entity is null)
        {
            return false;
        }

        var subjectSoulId = entity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : entity.Id.Value;
        var player = session.Engine.State.ControlledEntity;
        var playerSoulId = player.TryGet<SoulComponent>(out var playerSoul)
            ? playerSoul.SoulId
            : player.Id.Value;
        return session.Engine.State.Bonds.TryGet(subjectSoulId, playerSoulId, out _);
    }

    private static bool HasClaimAbout(GameSession session, string text) =>
        session.Engine.State.Claims.Records.Any(claim =>
            claim.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
            || claim.Subject.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static bool HasPromiseAbout(GameView view, string text) =>
        view.Promises.Any(promise =>
            promise.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
            || promise.Subject.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> CheckInvariants(
        ActionResult result,
        Core.Validation.StateValidationReport validation)
    {
        var issues = new List<string>();
        if (!validation.IsValid)
        {
            issues.AddRange(validation.Issues.Select(issue => $"{issue.Code}:{issue.EntityId ?? "state"}:{issue.Message}"));
        }

        if (result.TechnicalFailure)
        {
            issues.Add("technical failure during episode");
        }

        if (result.ConsumedTurn && result.TurnAfter <= result.TurnBefore)
        {
            issues.Add($"consumed command {result.Action} did not advance the turn");
        }

        if (!result.ConsumedTurn && result.TurnAfter != result.TurnBefore)
        {
            issues.Add($"free command {result.Action} changed turn from {result.TurnBefore} to {result.TurnAfter}");
        }

        // A plain move is intentionally silent now (the map shows it; no "You move." spam), so a
        // successful move with no messages is expected, not a defect (message-log immersion pass).
        if (result.Success && result.Messages.Count == 0 && !IsSilentAction(result.Action))
        {
            issues.Add($"successful command {result.Action} returned no messages");
        }

        return issues;

        static bool IsSilentAction(string action) =>
            string.Equals(action, "move", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> CheckLongRunInvariants(GameSession session)
    {
        var issues = new List<string>();
        var savedAt = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        try
        {
            var before = GameSaveService.Serialize(session.Engine.State, savedAt: savedAt);
            var loaded = GameSaveService.Deserialize(before);
            var after = GameSaveService.Serialize(
                loaded.State,
                loaded.PendingCast,
                loaded.PendingCastSerial,
                savedAt);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                issues.Add("save/load/save changed serialized state");
            }

            var loadedValidation = Core.Validation.StateValidator.Validate(loaded.State);
            if (!loadedValidation.IsValid)
            {
                issues.AddRange(loadedValidation.Issues.Select(issue => $"loaded:{issue.Code}:{issue.EntityId ?? "state"}:{issue.Message}"));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or JsonException or IOException)
        {
            issues.Add($"save/load invariant failed: {ex.Message}");
        }

        var duplicatePromises = session.Engine.State.PromiseLedger.Promises
            .GroupBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var duplicate in duplicatePromises)
        {
            issues.Add($"duplicate promise id {duplicate}");
        }

        foreach (var faction in session.Engine.State.Factions.Factions)
        {
            foreach (var pair in faction.Standing)
            {
                if (Math.Abs(pair.Value) > 100)
                {
                    issues.Add($"standing out of bounds {faction.Id}:{pair.Key}:{pair.Value}");
                }
            }

            foreach (var maxPair in faction.Resources.Where(pair => pair.Key.StartsWith("max_", StringComparison.OrdinalIgnoreCase)))
            {
                var resource = maxPair.Key["max_".Length..];
                if (faction.Resources.TryGetValue(resource, out var current) && current > maxPair.Value)
                {
                    issues.Add($"resource exceeds max {faction.Id}:{resource}:{current}>{maxPair.Value}");
                }
            }
        }

        var legendWeights = session.Engine.State.Legend.Tags
            .GroupBy(tag => (tag.ActorSoulId, tag.Tag))
            .Select(group => (group.Key.ActorSoulId, group.Key.Tag, Weight: group.Sum(tag => tag.Weight)));
        foreach (var item in legendWeights.Where(item => Math.Abs(item.Weight) > 100))
        {
            issues.Add($"legend out of bounds {item.ActorSoulId}:{item.Tag}:{item.Weight}");
        }

        if (!session.Engine.State.RunStatus.Equals("running", StringComparison.OrdinalIgnoreCase)
            && !session.Engine.State.Canon.Records.Any(record => record.Kind.Equals("chronicle", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add("completed run has no chronicle canon");
        }

        return issues;
    }

    private static bool IsFinished(GameSession session)
    {
        var view = session.View();
        var noHostiles = Hostiles(view).Count == 0;
        var promiseRealized = view.Promises.Any(promise =>
            promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase));
        return noHostiles && promiseRealized;
    }

    private static IReadOnlyList<EntityCard> Hostiles(GameView view)
    {
        var player = PlayerCard(view);
        if (player?.Faction is null)
        {
            return Array.Empty<EntityCard>();
        }

        return view.Entities
            .Where(entity => entity.Id != view.ControlledEntityId)
            .Where(entity => entity.HitPoints is > 0)
            .Where(entity => entity.Faction is not null
                && !entity.Faction.Equals(player.Faction, StringComparison.OrdinalIgnoreCase)
                && !entity.Faction.Equals("neutral", StringComparison.OrdinalIgnoreCase)
                && !entity.Faction.Equals("hollowmere", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static EntityCard? PlayerCard(GameView view) =>
        view.Entities.FirstOrDefault(entity => entity.Id == view.ControlledEntityId);

    private static bool IsClosedDoor(GameSession session, string entityId) =>
        session.Engine.EntityById(entityId)?.TryGet<DoorComponent>(out var door) == true
        && !door.IsOpen;

    private static ItemCard? FindInventory(GameView view, string name) =>
        view.Inventory?.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || item.Id.Equals(name, StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static bool TagsContain(EntityCard entity, string tag) =>
        entity.Tags.Any(candidate => candidate.Equals(tag, StringComparison.OrdinalIgnoreCase));

    private static int Distance(EntityCard a, EntityCard b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static Direction DirectionToward(EntityCard from, EntityCard to)
    {
        var dx = Math.Sign(to.X - from.X);
        var dy = Math.Sign(to.Y - from.Y);
        return (dx, dy) switch
        {
            (0, -1) => Direction.North,
            (0, 1) => Direction.South,
            (1, 0) => Direction.East,
            (-1, 0) => Direction.West,
            (1, -1) => Direction.NorthEast,
            (-1, -1) => Direction.NorthWest,
            (1, 1) => Direction.SouthEast,
            (-1, 1) => Direction.SouthWest,
            _ => Direction.East,
        };
    }

    private static string CommandToText(GameCommand command) =>
        command switch
        {
            MoveCommand move => $"move {move.Direction}",
            WaitCommand => "wait",
            InspectCommand => "inspect",
            CastCommand cast => $"cast {cast.Text}",
            BeginCastCommand cast => $"begin_cast {cast.Text}",
            AwaitCastCommand => "await_cast",
            CancelCastCommand => "cancel_cast",
            TargetCommand target => $"target {target.Position.X} {target.Position.Y}",
            ClearTargetCommand => "untarget",
            TravelCommand travel => $"travel {travel.Direction}",
            JourneyCommand journey => $"journey {journey.Destination}",
            AtlasCommand => "atlas",
            PickupCommand pickup => $"pickup {pickup.Target}",
            DropCommand drop => $"drop {drop.Item}",
            UseItemCommand use => $"use {use.Item}",
            EquipCommand equip => $"equip {equip.Item}",
            UnequipCommand unequip => $"unequip {unequip.SlotOrItem}",
            FocusCommand focus => $"focus {focus.SlotOrItem}",
            UnfocusCommand unfocus => $"unfocus {unfocus.SlotOrItem}",
            InventoryCommand => "inventory",
            ReagentsCommand => "reagents",
            ThreatsCommand => "threats",
            WaresCommand wares => $"wares {wares.Target}",
            BuyCommand buy => $"buy {buy.Item} from {buy.Target}",
            SellCommand sell => $"sell {sell.Item} to {sell.Target}",
            ServicesCommand services => $"services {services.Target}",
            RequestServiceCommand service => $"request {service.Service} from {service.Target}",
            JournalCommand => "journal",
            RumorsCommand => "rumors",
            TalkCommand talk => $"talk {talk.Text}",
            GroupTalkCommand groupTalk => $"gather {groupTalk.Text}",
            SettleCommand settle => $"settle {settle.Target}",
            BargainsCommand bargains => $"bargains {bargains.Target}",
            FulfillCommand fulfill => $"fulfill {fulfill.Reference}",
            OfferCommand offer => $"offer {offer.Text}",
            BargainCommand bargain => $"bargain {bargain.Target}",
            ConcedeCommand concede => $"concede {concede.Target}",
            IntimidateCommand intimidate => $"intimidate {intimidate.Target}",
            ExchangeCommand exchange => $"exchange {exchange.Text}",
            CleanseCommand cleanse => $"cleanse {cleanse.Reference}",
            BraceCommand => "brace",
            CounterCommand counter => $"counter {counter.Text}",
            BreachCommand breach => $"breach {breach.Target}",
            ForgeCommand forge => $"forge {forge.Text}",
            GiveCommand give => $"give {give.Item} to {give.Target}",
            RecruitCommand recruit => $"recruit {recruit.Target}",
            BondsCommand bonds => $"bonds {bonds.Target}",
            ReadCommand read => $"read {read.Target}",
            ExamineCommand examine => $"examine {examine.Target}",
            OpenCommand open => $"open {open.Target}",
            EnterCommand enter => $"enter {enter.Target}",
            LeaveCommand => "leave",
            PossessCommand possess => $"possess {possess.Target}",
            StandingCommand => "standing",
            FollowersCommand => "followers",
            JobsCommand => "jobs",
            HelpCommand => "help",
            QuitCommand => "quit",
            UnknownCommand unknown => unknown.Text,
            _ => command.GetType().Name,
        };

    private sealed record EpisodeStartRecord(
        string RecordType,
        int Episode,
        int Seed,
        int MaxTurns,
        bool Quickstart,
        string? QuickstartScene,
        AgentObservation InitialObservation);

    private sealed record EpisodeFinalRecord(
        string RecordType,
        EpisodeSummary Summary,
        AgentObservation FinalObservation);

    private sealed record EpisodeReport(
        string Provider,
        int Episodes,
        int Passed,
        int Failed,
        IReadOnlyList<EpisodeSummary> Summaries);

    private sealed record EpisodeSummary(
        int Episode,
        int Seed,
        bool Passed,
        int Steps,
        int FinalTurn,
        int? PlayerHitPoints,
        int? PlayerMaxHitPoints,
        bool PlayerAlive,
        int HostilesAlive,
        int Promises,
        IReadOnlyList<string> Issues);

    private sealed record EpisodeStepRecord(
        string RecordType,
        int Episode,
        int Seed,
        int Step,
        string Command,
        string Action,
        bool Success,
        bool ConsumedTurn,
        bool TechnicalFailure,
        int TurnBefore,
        int TurnAfter,
        ActionResult Result,
        IReadOnlyList<string> Messages,
        IReadOnlyList<string> MagicEffects,
        IReadOnlyList<string> Issues,
        int? PlayerHitPoints,
        int? PlayerMaxHitPoints,
        int EntityCount,
        int PromiseCount,
        string? PendingCast,
        AgentObservation Observation);
}
