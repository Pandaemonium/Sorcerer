using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplyAlterItem(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Alter-item consequence did not include a carrier entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var item = FirstNonBlank(ReadString(payload, "item"), ReadString(payload, "itemName"));
        var profileId = FirstNonBlank(ReadString(payload, "profileId"), ReadString(payload, "profile_id"));
        var action = NormalizeToken(ReadString(payload, "action") ?? "apply", "apply");
        if (string.IsNullOrWhiteSpace(item))
        {
            return Reject(consequence, "Alter-item consequence did not name an item.");
        }

        var profile = string.IsNullOrWhiteSpace(profileId) ? null : CostProfileCatalog.Default.Find(profileId);
        if (action is not "remove" and not "clear"
            && (profile is null || !profile.Kind.Equals("altered_item", StringComparison.OrdinalIgnoreCase)))
        {
            return Reject(consequence, "Alter-item consequence did not name a supported altered-item profile.");
        }

        var carrier = target.Entity!;
        var inventoryKey = carrier.TryGet<InventoryComponent>(out var inventory)
            ? inventory.Items.Keys.FirstOrDefault(key => key.Equals(item, StringComparison.OrdinalIgnoreCase))
            : carrier.TryGet<ItemComponent>(out _) && carrier.Name.Equals(item, StringComparison.OrdinalIgnoreCase)
                ? carrier.Name
                : null;
        if (inventoryKey is null)
        {
            return Reject(consequence, $"{carrier.Name} does not carry {item}.");
        }

        var alterations = carrier.TryGet<ItemAlterationComponent>(out var existing)
            ? existing
            : ItemAlterationComponent.Empty();
        if (action is "remove" or "clear")
        {
            if (!alterations.Profiles.Remove(inventoryKey))
            {
                return Reject(consequence, $"{inventoryKey} is not altered.");
            }
        }
        else
        {
            alterations.Profiles[inventoryKey] = profile!.Id;
        }

        if (alterations.Profiles.Count == 0)
        {
            carrier.Remove<ItemAlterationComponent>();
        }
        else
        {
            carrier.Set(alterations);
        }

        if (carrier.Has<EquipmentComponent>())
        {
            EquipmentEffectService.Recompute(carrier, ItemCatalog.LoadDefault());
        }

        var operation = ReadString(payload, "operation") ?? "alterItem";
        var summary = action is "remove" or "clear"
            ? $"The alteration leaves {inventoryKey}."
            : $"{inventoryKey} becomes {profile!.Name}: {profile.Condition}";
        var messages = MaybeVisibleMessage(consequence, summary);
        return Applied(
            consequence,
            carrier.Id.Value,
            messages,
            new StateDelta(
                operation,
                carrier.Id.Value,
                summary,
                Details(
                    consequence,
                    ("item", inventoryKey),
                    ("profileId", action is "remove" or "clear" ? null : profile!.Id),
                    ("action", action),
                    ("playerVisible", true))));
    }

    private WorldConsequenceApplyResult ApplyResolveCost(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Resolve-cost consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var category = NormalizeToken(ReadString(payload, "category") ?? "curse", "curse");
        var requestedProfile = FirstNonBlank(ReadString(payload, "profileId"), ReadString(payload, "profile_id"));
        if (category is "altered_item" or "altered" or "item")
        {
            var item = FirstNonBlank(ReadString(payload, "item"), ReadString(payload, "itemName"));
            if (string.IsNullOrWhiteSpace(item))
            {
                return Reject(consequence, "Clearing an item alteration requires an item name.");
            }

            return ApplyAlterItem(WorldConsequence.AlterItem(
                consequence.Source,
                target.Entity!.Id.Value,
                item,
                requestedProfile ?? "altered_item",
                action: "remove",
                visibility: consequence.Visibility,
                sourceEntityId: consequence.SourceEntityId,
                evidence: consequence.Evidence,
                reason: consequence.Reason,
                operation: ReadString(payload, "operation") ?? "resolveAlteredItem"));
        }

        var active = _state.PromiseLedger.Promises
            .Where(promise => promise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase))
            .Where(promise => promise.Status is not "cleared" and not "fulfilled")
            .Where(promise => string.IsNullOrWhiteSpace(requestedProfile)
                || string.Equals(promise.CostProfileId, requestedProfile, StringComparison.OrdinalIgnoreCase))
            .Where(promise => string.IsNullOrWhiteSpace(promise.BoundTargetId)
                || promise.BoundTargetId.Equals(target.Entity!.Id.Value, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(promise => promise.Salience)
            .ThenBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (active is null)
        {
            // Cleansing is intentionally idempotent. A charter form may be prepared before the
            // player knows whether a strange condition is a durable curse, and "nothing to file"
            // should be a clean no-op rather than a failed cast that rolls back unrelated effects.
            var noOpSummary = "No matching active curse answers the cleansing writ.";
            return Applied(
                consequence,
                target.Entity!.Id.Value,
                MaybeVisibleMessage(consequence, noOpSummary),
                new StateDelta(
                    ReadString(payload, "operation") ?? "resolveCostNoOp",
                    target.Entity.Id.Value,
                    noOpSummary,
                    Details(
                        consequence,
                        ("profileId", requestedProfile),
                        ("status", "not_present"),
                        ("playerVisible", true))));
        }

        var updated = _state.PromiseLedger.SetStatus(active.Id, "cleared", _state.CurrentZoneId);
        if (updated is null)
        {
            return Reject(consequence, "The curse promise could not be cleared.");
        }

        var childDeltas = new List<StateDelta>();
        var statusId = CurseStatusId(active.CostProfileId);
        if (!string.IsNullOrWhiteSpace(statusId))
        {
            childDeltas.AddRange(Apply(WorldConsequence.RemoveStatus(
                consequence.Source,
                target.Entity!.Id.Value,
                statusId,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: consequence.SourceEntityId,
                evidence: consequence.Evidence,
                reason: "The curse's visible status ended with its durable promise.",
                operation: "resolveCurseStatus",
                emitMessage: false)).Deltas);
        }

        if (active.CostProfileId?.Equals("curse_iron_thirst", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var damageType in new[] { "iron", "metal", "charter" })
            {
                childDeltas.AddRange(Apply(WorldConsequence.SetWeakness(
                    consequence.Source,
                    target.Entity!.Id.Value,
                    damageType,
                    amount: 0,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: consequence.SourceEntityId,
                    evidence: consequence.Evidence,
                    reason: "Clearing Iron Thirst removes its typed weaknesses.",
                    operation: "resolveIronThirstWeakness")).Deltas);
            }
        }

        var profile = string.IsNullOrWhiteSpace(active.CostProfileId) ? null : CostProfileCatalog.Default.Find(active.CostProfileId);
        var summary = $"{profile?.Name ?? active.Subject} is cleared. The journal records how it ended.";
        var messages = MaybeVisibleMessage(consequence, summary);
        var delta = new StateDelta(
            ReadString(payload, "operation") ?? "resolveCost",
            active.Id,
            summary,
            Details(
                consequence,
                ("promiseId", active.Id),
                ("profileId", active.CostProfileId),
                ("status", "cleared"),
                ("playerVisible", true)));
        return new WorldConsequenceApplyResult(
            true,
            active.Id,
            null,
            messages,
            childDeltas.Concat(new[] { delta }).ToArray(),
            Details(consequence, ("promiseId", active.Id), ("profileId", active.CostProfileId)));
    }

    internal static string CurseStatusId(string? profileId) => profileId?.ToLowerInvariant() switch
    {
        "curse_marked_by_color" => "marked_by_color",
        "curse_hollow_name" => "hollow_name",
        "curse_iron_thirst" => "iron_thirst",
        "curse_tide_debt_body" => "borrowed_tide",
        _ => "",
    };
}
