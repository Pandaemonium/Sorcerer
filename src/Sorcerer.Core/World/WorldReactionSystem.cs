using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;

namespace Sorcerer.Core.World;

public sealed record WorldReactionSummary(
    IReadOnlyList<string> StandingChanges,
    IReadOnlyList<string> NewPromises,
    IReadOnlyList<string> NarrationHooks);

public sealed record WorldReactionApplication(
    IReadOnlyList<string> Messages,
    IReadOnlyList<StateDelta> Deltas,
    bool AppliedAny);

public sealed record DeedCapturePlan(
    int Turn,
    string ActorSoulId,
    string Kind,
    int Magnitude,
    string PlaceKey,
    string Visibility,
    IReadOnlyList<string> Witnesses,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> EffectWitnesses,
    string? AttributedSoulId,
    string AttributionStatus);

public sealed class WorldReactionSystem
{
    public DeedCapturePlan PlanDeed(
        GameState state,
        Entity actor,
        string kind,
        int magnitude,
        GridPoint origin,
        GridPoint? effectPoint,
        IReadOnlyList<Entity> actorWitnesses,
        IReadOnlyList<Entity> effectWitnesses,
        IEnumerable<string>? tags = null)
    {
        var actorSoulId = actor.TryGet<SoulComponent>(out var soul) ? soul.SoulId : actor.Id.Value;
        var actorWitnessIds = actorWitnesses
            .Select(WitnessId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToArray();
        var effectWitnessIds = effectWitnesses
            .Select(WitnessId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToArray();
        var visibility = ClassifyVisibility(actorWitnessIds, effectWitnessIds, magnitude);
        var attributionStatus = visibility.Equals("suspicious", StringComparison.OrdinalIgnoreCase)
            ? "unattributed"
            : actorWitnessIds.Length > 0 ? "attributed" : "secret";

        return new DeedCapturePlan(
            state.Turn,
            actorSoulId,
            kind,
            Math.Max(1, magnitude),
            $"{state.RegionId}:{origin.X},{origin.Y}",
            visibility,
            actorWitnessIds,
            NormalizeTags(tags),
            effectWitnessIds,
            attributionStatus.Equals("attributed", StringComparison.OrdinalIgnoreCase) ? actorSoulId : null,
            attributionStatus);
    }

    public WorldReactionApplication ApplyPending(
        GameState state,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        var apply = applyConsequence ?? (consequence => WorldConsequenceGuard.ApplyWithNewApplier(state, consequence));
        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        var appliedAny = false;
        foreach (var deed in state.Deeds.Records
            .Where(deed => !state.Deeds.IsApplied(deed.Id))
            .OrderBy(deed => deed.Turn)
            .ThenBy(deed => deed.Id)
            .ToArray())
        {
            var snapshot = GameStateSnapshot.Capture(state);
            var deedMessages = new List<string>();
            var deedDeltas = new List<StateDelta>();
            ApplyDeed(state, deed, deedMessages, deedDeltas, apply);
            if (!HasRejected(deedDeltas))
            {
                ApplyConsequence(deedMessages, deedDeltas, apply, WorldConsequence.UpdateDeed(
                "world_reaction",
                deed.Id,
                action: "mark_applied",
                evidence: $"World reactions were applied for deed {deed.Id}.",
                reason: "World reaction processing marks each deed once its consequences have been submitted.",
                operation: "deedApplied"));
            }

            if (HasRejected(deedDeltas))
            {
                snapshot.Restore(state);
                deltas.AddRange(deedDeltas.Where(IsRejectedDelta));
                deltas.Add(WorldReactionSkippedDelta(deed, deedDeltas));
                continue;
            }

            messages.AddRange(deedMessages);
            deltas.AddRange(deedDeltas);
            appliedAny = true;
        }

        return new WorldReactionApplication(messages, deltas, appliedAny);
    }

    public WorldReactionSummary PreviewDailyTick(GameState state)
    {
        var standingChanges = state.Deeds.Records
            .Where(deed => deed.Kind.Contains("imperial", StringComparison.OrdinalIgnoreCase))
            .Select(deed => $"Empire pressure notices {deed.Kind}.")
            .ToArray();

        return new WorldReactionSummary(
            standingChanges,
            Array.Empty<string>(),
            Array.Empty<string>());
    }

    private static void ApplyDeed(
        GameState state,
        DeedRecord deed,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (deed.Visibility.Equals("secret", StringComparison.OrdinalIgnoreCase))
        {
            // Even a perfectly quiet kill of the Empire's own is eventually noticed as silence:
            // the post misses its patrol and opens a file (docs/FREE_FOLK_MOVEMENT.md).
            ScheduleOverdueAudit(state, deed, messages, deltas, applyConsequence);
            return;
        }

        if (RumorSystem.ConsequenceFromDeed(state, deed) is { } rumor)
        {
            ApplyConsequence(messages, deltas, applyConsequence, rumor);
        }

        RecordWitnessMemories(state, deed, messages, deltas, applyConsequence);

        switch (deed.Kind)
        {
            case "freed_prisoner":
                AddLegend(messages, deltas, applyConsequence, deed, "merciful", 2);
                AddLegend(messages, deltas, applyConsequence, deed, "defiant", 1);
                AdjustFactionStanding(messages, deltas, applyConsequence, "hollowmere", "gratitude", 2);
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "suspicion", 1);
                // Liberation erodes the empire's grip: a real anti-imperial victory spends a point of
                // imperial defense, which later shows up as one fewer guard at the capital (organic
                // capital approach -- see memory capital-organic-approach-design).
                SpendEmpireDefenses(state, messages, deltas, applyConsequence, 1);
                AddMessage(messages, deltas, applyConsequence, deed, "freed_prisoner",
                    ScheduleEmpireReport(state, deed, 1, messages, deltas, applyConsequence)
                        ? "By morning someone will have carried word of the rescue down the road."
                        : "No one who would tell the Empire saw the rescue; only those who wanted it free know.");
                break;
            case "body_swap":
                AddLegend(messages, deltas, applyConsequence, deed, "uncanny", 2);
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "suspicion", deed.Magnitude);
                ScheduleEmpireReport(state, deed, Math.Max(1, deed.Magnitude - 1), messages, deltas, applyConsequence);
                AddMessage(messages, deltas, applyConsequence, deed, "body_swap", "Whoever watched that will be looking for someone willing to believe them.");
                break;
            case "kill":
                AddLegend(messages, deltas, applyConsequence, deed, "butcher", Math.Max(1, deed.Magnitude));
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "fear", deed.Magnitude);
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "notoriety", deed.Magnitude);
                // Force route of the organic capital approach: cutting down an imperial in the open
                // spends a point of the empire's finite defense, so beating its forces in the field
                // means fewer guards stand at the throne later. The kill deed carries the victim's
                // faction as a tag (MovementSystem), so this needs no new plumbing.
                if (VictimIsEmpireBloc(state, deed))
                {
                    SpendEmpireDefenses(state, messages, deltas, applyConsequence, 1);
                }

                AddMessage(messages, deltas, applyConsequence, deed, "kill",
                    ScheduleEmpireReport(state, deed, Math.Max(1, deed.Magnitude), messages, deltas, applyConsequence)
                        ? "Someone will carry word of the killing to the next town before nightfall."
                        : "No one who would tell the Empire saw the killing; only the silence will speak, and slowly.");
                break;
            case "attack":
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "fear", Math.Max(1, deed.Magnitude - 1));
                ScheduleEmpireReport(state, deed, 1, messages, deltas, applyConsequence);
                AddLegend(messages, deltas, applyConsequence, deed, "dangerous", 1);
                break;
            case "wild_magic":
                ApplyWildMagic(state, deed, messages, deltas, applyConsequence);
                break;
            case "charter_magic":
                ApplyCharterMagic(state, deed, messages, deltas, applyConsequence);
                break;
        }
    }

    // A witnessed charter cast reads as plausibly licensed work (docs/CHARTER_MAGIC.md):
    // no uncanny legend, no fear, no empire heat - only a sliver of suspicion that someone
    // might check the paperwork later. This is what makes charter magic the quiet option,
    // at the price of its capped power. Escalation for a caster the Empire already marks
    // as unlicensed (wanted posters, regalia checks) is a later layer.
    private static void ApplyCharterMagic(
        GameState state,
        DeedRecord deed,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (deed.Visibility.Equals("suspicious", StringComparison.OrdinalIgnoreCase))
        {
            AdjustEmpireBloc(state, messages, deltas, applyConsequence, "suspicion", 1);
            AddMessage(messages, deltas, applyConsequence, deed, "suspicious_charter_magic", "Someone glimpsed tidy, licensed-looking magic with no caster to pin it on.", playerVisible: FirstReactionOfKind(state, "suspicious_charter_magic"));
            return;
        }

        AdjustEmpireBloc(state, messages, deltas, applyConsequence, "suspicion", 1);
        AddMessage(messages, deltas, applyConsequence, deed, "public_charter_magic", "The casting reads as licensed charter work; nobody reaches for an alarm bell.", playerVisible: FirstReactionOfKind(state, "public_charter_magic"));
    }

    private static void RecordWitnessMemories(
        GameState state,
        DeedRecord deed,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        foreach (var witnessId in deed.Witnesses
            .Concat(deed.EffectWitnesses ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var witness = ResolveWitnessEntity(state, witnessId);
            if (witness is null || witness.Id == state.ControlledEntityId)
            {
                continue;
            }

            var memoryText = $"{witness.Name} witnessed {DeedMemoryText(deed)}";
            ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.RecordMemory(
                "world_reaction",
                witness.Id.Value,
                memoryText,
                $"deed:{deed.Id}",
                Math.Clamp(deed.Magnitude, 1, 5),
                shareable: true,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: witness.Id.Value,
                evidence: deed.Id,
                reason: "A witnessed deed left durable NPC context through the shared memory lifecycle.",
                operation: "deedWitnessMemory",
                details: new Dictionary<string, object?>
                {
                    ["deedId"] = deed.Id,
                    ["deedKind"] = deed.Kind,
                    ["deedVisibility"] = deed.Visibility,
                    ["attributionStatus"] = deed.AttributionStatus,
                    ["actorSoulId"] = deed.ActorSoulId,
                    ["witnessSoulId"] = witnessId,
                    ["placeKey"] = deed.PlaceKey,
                    ["summary"] = $"{witness.Name} remembers witnessing {CleanKind(deed.Kind)}.",
                    ["playerVisible"] = false,
                }));
        }
    }

    private static Entity? ResolveWitnessEntity(GameState state, string witnessId) =>
        state.Entities.Values.FirstOrDefault(entity =>
            entity.Id.Value.Equals(witnessId, StringComparison.OrdinalIgnoreCase)
            || (entity.TryGet<SoulComponent>(out var soul)
                && soul.SoulId.Equals(witnessId, StringComparison.OrdinalIgnoreCase)));

    private static string DeedMemoryText(DeedRecord deed)
    {
        var actor = deed.AttributionStatus.Equals("attributed", StringComparison.OrdinalIgnoreCase)
            ? deed.ActorSoulId
            : "someone unnamed";
        return $"{CleanKind(deed.Kind)} in {ReadablePlace(deed.PlaceKey)} by {actor}.";
    }

    private static string CleanKind(string kind) => kind.Replace('_', ' ');

    // Player-facing text must never show a raw place key like "imperial_encounter:13,29" or its
    // title-cased id "Imperial Encounter": resolve the region's authored display name.
    private static string ReadablePlace(string placeKey) => RegionCatalog.ReadablePlace(placeKey);

    private static void ApplyWildMagic(
        GameState state,
        DeedRecord deed,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (deed.Visibility.Equals("suspicious", StringComparison.OrdinalIgnoreCase))
        {
            AdjustEmpireBloc(state, messages, deltas, applyConsequence, "suspicion", Math.Max(1, deed.Magnitude));
            ScheduleEmpireReport(state, deed, 1, messages, deltas, applyConsequence);
            var effectSeer = ResolveWitnessName(state, deed.EffectWitnesses, "A passerby");
            AddMessage(messages, deltas, applyConsequence, deed, "suspicious_wild_magic", $"{effectSeer} saw the magic flare, but not the hand that loosed it.", playerVisible: FirstReactionOfKind(state, "suspicious_wild_magic"));
            return;
        }

        AddLegend(messages, deltas, applyConsequence, deed, "uncanny", Math.Max(1, deed.Magnitude));
        if (deed.Tags.Any(tag => tag.Contains("damage", StringComparison.OrdinalIgnoreCase)))
        {
            AddLegend(messages, deltas, applyConsequence, deed, "dangerous", 1);
            AdjustEmpireBloc(state, messages, deltas, applyConsequence, "fear", 1);
        }

        AdjustEmpireBloc(state, messages, deltas, applyConsequence, "imperial-threat", Math.Max(1, deed.Magnitude));
        AdjustEmpireBloc(state, messages, deltas, applyConsequence, "notoriety", 1);
        ScheduleEmpireReport(state, deed, Math.Max(1, deed.Magnitude), messages, deltas, applyConsequence);
        var actorSeer = ResolveWitnessName(state, deed.Witnesses, "A bystander");
        AddMessage(messages, deltas, applyConsequence, deed, "public_wild_magic", $"{actorSeer} saw you loose the wild magic, and the ones who watched are already telling each other what they think it was.", playerVisible: FirstReactionOfKind(state, "public_wild_magic"));
    }

    private static void AdjustEmpireBloc(
        GameState state,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        string axis,
        int delta) =>
        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.AdjustFactionStanding(
            "world_reaction",
            "empire_bloc",
            axis,
            delta,
            targetIsRole: true));

    // --- Report-borne heat (docs/FREE_FOLK_MOVEMENT.md, "The marble answers slowly") ---
    // The Empire's alarm is knowledge, and knowledge needs a carrier. A deed raises `heat`
    // only when word of it physically arrives: a surviving witness willing to talk reaches an
    // imperial desk after a travel delay, or - when the Empire's own people vanish with no
    // witness left - an overdue audit eventually notices the silence. Material losses (dead
    // soldiers, spent defenses) still land instantly; attribution and alarm must travel.

    internal const string EmpireReportEventKind = "empire_report";
    internal const string OverdueReportCause = "overdue";
    internal const int ImperialReportTravelTurns = 4;
    internal const int CivilianReportTravelTurns = 8;
    internal const int OverdueAuditTurns = 18;

    /// <summary>
    /// Schedules the report that will raise empire heat once it reaches a desk. Returns true
    /// when a living witness is carrying word; false when nothing (or only silence) will travel.
    /// </summary>
    private static bool ScheduleEmpireReport(
        GameState state,
        DeedRecord deed,
        int heat,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (heat <= 0)
        {
            return false;
        }

        var reporters = ReportingWitnesses(state, deed);
        if (reporters.Count == 0)
        {
            ScheduleOverdueAudit(state, deed, messages, deltas, applyConsequence);
            return false;
        }

        // A surviving imperial reports through its own chain quickly; a civilian's word
        // wanders toward a desk more slowly. Tuning lives in docs/FREE_FOLK_MOVEMENT.md.
        var imperialReporter = reporters.Any(witness => IsEmpireBlocFaction(state, WitnessFactionId(witness)));
        var travel = imperialReporter ? ImperialReportTravelTurns : CivilianReportTravelTurns;
        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.ScheduleEvent(
            "world_reaction",
            EmpireReportEventKind,
            travel,
            new Dictionary<string, object?>
            {
                ["heat"] = heat,
                ["cause"] = deed.Kind,
                ["placeKey"] = deed.PlaceKey,
                ["witnessIds"] = string.Join(",", reporters.Select(witness => witness.Id.Value)),
                ["text"] = $"Word of the {CleanKind(deed.Kind)} reaches an imperial desk; the district's file grows a page.",
            },
            evidence: deed.Id,
            reason: "A witnessed deed needs a living carrier before the Empire's alarm can rise."));
        return true;
    }

    private static void ScheduleOverdueAudit(
        GameState state,
        DeedRecord deed,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (!deed.Kind.Equals("kill", StringComparison.OrdinalIgnoreCase) || !VictimIsEmpireBloc(state, deed))
        {
            return;
        }

        // One pending audit is enough: five silent kills read as one discovered silence, not
        // five separate files.
        if (state.ScheduledEvents.Events.Any(item =>
            item.Kind.Equals(EmpireReportEventKind, StringComparison.OrdinalIgnoreCase)
            && item.Payload.TryGetValue("cause", out var cause)
            && OverdueReportCause.Equals(Convert.ToString(cause), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.ScheduleEvent(
            "world_reaction",
            EmpireReportEventKind,
            OverdueAuditTurns,
            new Dictionary<string, object?>
            {
                ["heat"] = 2,
                ["cause"] = OverdueReportCause,
                ["placeKey"] = deed.PlaceKey,
                ["text"] = "Somewhere up the road, an imperial post marks a patrol overdue and opens a file on the silence.",
            },
            evidence: deed.Id,
            reason: "The Empire eventually notices its own silence even when no witness survives."));
    }

    // A witness carries word to the Empire only if they are alive, not the sorcerer, and not
    // someone whose allegiance points away from an imperial desk (player faction, resistance
    // cells, followers/allies). Dead witnesses file no reports.
    private static IReadOnlyList<Entity> ReportingWitnesses(GameState state, DeedRecord deed)
    {
        return deed.Witnesses
            .Concat(deed.EffectWitnesses ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => ResolveWitnessEntity(state, id))
            .Where(witness => witness is not null && WouldReportToEmpire(state, witness))
            .Select(witness => witness!)
            .DistinctBy(witness => witness.Id.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(witness => witness.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool WouldReportToEmpire(GameState state, Entity witness)
    {
        if (witness.Id == state.ControlledEntityId)
        {
            return false;
        }

        if (!witness.TryGet<ActorComponent>(out var actor) || !actor.Alive)
        {
            return false;
        }

        if (witness.TryGet<AiComponent>(out var ai)
            && (ai.PolicyId.Equals("follower", StringComparison.OrdinalIgnoreCase)
                || ai.PolicyId.Equals("ally", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var factionId = WitnessFactionId(witness);
        if (string.IsNullOrWhiteSpace(factionId))
        {
            return true;
        }

        var role = state.Factions.Factions
            .FirstOrDefault(faction => faction.Id.Equals(factionId, StringComparison.OrdinalIgnoreCase))
            ?.Role;
        return role is null
            || (!role.Equals("player", StringComparison.OrdinalIgnoreCase)
                && !role.Equals("resistance", StringComparison.OrdinalIgnoreCase));
    }

    private static string WitnessFactionId(Entity witness) =>
        witness.TryGet<ActorComponent>(out var actor) && !string.IsNullOrWhiteSpace(actor.Faction)
            ? actor.Faction
            : witness.TryGet<FactionComponent>(out var faction) ? faction.FactionId : string.Empty;

    private static bool IsEmpireBlocFaction(GameState state, string factionId) =>
        !string.IsNullOrWhiteSpace(factionId)
        && state.Factions.FactionsByRole("empire_bloc").Any(faction =>
            faction.Id.Equals(factionId, StringComparison.OrdinalIgnoreCase));

    // Spend the empire's finite defense capacity in response to a real anti-imperial victory. This
    // is the player-driven half of the organic capital approach: the capital guard is generated from
    // the current defenses, so eroding them here means fewer guards stand between the player and the
    // throne later. Clamped at 0 by AdjustFactionResource.
    private static void SpendEmpireDefenses(
        GameState state,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        int amount)
    {
        foreach (var faction in state.Factions.FactionsByRole("empire_bloc"))
        {
            AdjustFactionResource(messages, deltas, applyConsequence, faction.Id, "defenses", -Math.Max(0, amount));
        }
    }

    // A kill/attack deed carries the victim's faction id among its tags (MovementSystem records it).
    // This is true when the felled actor belonged to an empire-bloc faction, i.e. it was one of the
    // empire's own forces.
    private static bool VictimIsEmpireBloc(GameState state, DeedRecord deed)
    {
        var empireFactionIds = state.Factions.FactionsByRole("empire_bloc")
            .Select(faction => faction.Id)
            .ToArray();
        return deed.Tags.Any(tag =>
            empireFactionIds.Any(id => id.Equals(tag, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AddLegend(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        DeedRecord deed,
        string tag,
        int weight)
    {
        if (!deed.AttributionStatus.Equals("attributed", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("witnessed", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("mythic", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.AddLegend(
            "world_reaction",
            deed.ActorSoulId,
            tag,
            weight,
            deed.Id));
    }

    // The "witnesses are talking" flavor lines teach something real the first time (charter reads
    // as licensed; wild magic is seen and spreads) but repeat verbatim on every subsequent cast.
    // Show each once per run, then keep it audit-only. WorldFlags is snapshot/rollback-safe, so a
    // rolled-back cast does not spend the first-showing.
    private static bool FirstReactionOfKind(GameState state, string reactionKind)
    {
        var key = $"reaction_shown:{reactionKind}";
        if (state.WorldFlags.ContainsKey(key))
        {
            return false;
        }

        state.WorldFlags[key] = true;
        return true;
    }

    // playerVisible: false keeps the narration in deltas/audit but out of the player log.
    private static void AddMessage(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        DeedRecord deed,
        string reactionKind,
        string message,
        bool playerVisible = true)
    {
        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.Message(
            "world_reaction",
            message,
            targetEntityId: deed.Id,
            visibility: playerVisible ? WorldConsequenceVisibility.Message : WorldConsequenceVisibility.Hidden,
            evidence: $"{deed.Kind} deed {deed.Id} became {deed.Visibility}.",
            reason: "A deed became visible enough for world-reaction narration.",
            operation: "worldReactionMessage",
            details: new Dictionary<string, object?>
            {
                ["deedId"] = deed.Id,
                ["deedKind"] = deed.Kind,
                ["actorSoulId"] = deed.ActorSoulId,
                ["deedVisibility"] = deed.Visibility,
                ["attributionStatus"] = deed.AttributionStatus,
                ["reactionKind"] = reactionKind,
                ["playerVisible"] = playerVisible,
            }));
    }

    private static void AdjustFactionStanding(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        string factionId,
        string axis,
        int delta) =>
        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.AdjustFactionStanding(
            "world_reaction",
            factionId,
            axis,
            delta));

    private static void AdjustFactionResource(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        string factionId,
        string resource,
        int delta,
        int min = 0,
        int? max = null)
    {
        if (delta == 0)
        {
            return;
        }

        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.AdjustFactionResource(
            "world_reaction",
            factionId,
            resource,
            delta,
            min,
            max));
    }

    private static void ApplyConsequence(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        WorldConsequence consequence)
    {
        var applied = applyConsequence(consequence);
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private static bool HasRejected(IEnumerable<StateDelta> deltas) =>
        deltas.Any(IsRejectedDelta);

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    private static StateDelta WorldReactionSkippedDelta(DeedRecord deed, IReadOnlyList<StateDelta> rejectedDeltas)
    {
        var errors = rejectedDeltas
            .Where(IsRejectedDelta)
            .Select(delta => delta.Summary)
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StateDelta(
            "worldReactionSkipped",
            deed.Id,
            $"World reaction for {deed.Id} was rolled back after a rejected consequence.",
            new Dictionary<string, object?>
            {
                ["deedId"] = deed.Id,
                ["deedKind"] = deed.Kind,
                ["deedVisibility"] = deed.Visibility,
                ["attributionStatus"] = deed.AttributionStatus,
                ["rejectedCount"] = errors.Length,
                ["errors"] = errors,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });
    }

    private static string ClassifyVisibility(
        IReadOnlyList<string> actorWitnesses,
        IReadOnlyList<string> effectWitnesses,
        int magnitude)
    {
        if (magnitude >= 8 && actorWitnesses.Count > 0)
        {
            return "mythic";
        }

        if (actorWitnesses.Count >= 2)
        {
            return "public";
        }

        if (actorWitnesses.Count == 1)
        {
            return "witnessed";
        }

        return effectWitnesses.Count > 0 ? "suspicious" : "secret";
    }

    private static string WitnessId(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    // Name the actual carrier of a witnessed deed (docs/AESTHETICS_AND_TONE.md: name people, never
    // "someone"). Resolves a witness soul id from the shared classification to a concrete name,
    // preferring the public appearance name; falls back only if the witness has since left the zone.
    private static string ResolveWitnessName(GameState state, IReadOnlyList<string>? witnessSoulIds, string fallback)
    {
        foreach (var soulId in witnessSoulIds ?? Array.Empty<string>())
        {
            var witness = state.Entities.Values.FirstOrDefault(entity =>
                entity.TryGet<SoulComponent>(out var soul)
                && soul.SoulId.Equals(soulId, StringComparison.OrdinalIgnoreCase));
            if (witness is null)
            {
                continue;
            }

            return witness.TryGet<ProfileComponent>(out var profile) && !string.IsNullOrWhiteSpace(profile.PublicName)
                ? profile.PublicName
                : witness.Name;
        }

        return fallback;
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? Array.Empty<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant().Replace(' ', '_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag)
            .ToArray();
}
