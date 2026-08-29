#nullable enable

namespace Loadout.Services.OneRelic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Loadout.Services.ContentBans;
using Loadout.Services.RelicReplacement;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

internal static class OneRelicLiveOfferService
{
    private static readonly FieldInfo RelicRewardField = AccessTools.Field(typeof(RelicReward), "_relic");
    private static readonly FieldInfo PredeterminedRelicRewardField = AccessTools.Field(typeof(RelicReward), "_predeterminedRelic");
    private static readonly FieldInfo MerchantRelicModelField = AccessTools.Field(typeof(MerchantRelicEntry), "<Model>k__BackingField");
    private static readonly FieldInfo AncientGeneratedOptionsField = AccessTools.Field(typeof(AncientEventModel), "_generatedOptions");
    private static readonly MethodInfo SetEventStateMethod = AccessTools.Method(typeof(EventModel), "SetEventState");
    private static readonly FieldInfo EventOptionTitleField = AccessTools.Field(typeof(EventOption), "<Title>k__BackingField");
    private static readonly FieldInfo EventOptionDescriptionField = AccessTools.Field(typeof(EventOption), "<Description>k__BackingField");
    private static readonly FieldInfo EventOptionHistoryNameField = AccessTools.Field(typeof(EventOption), "<HistoryName>k__BackingField");
    private static ConditionalWeakTable<RelicReward, Baseline> _rewardBaselines = new();
    private static ConditionalWeakTable<RelicReward, NestedGrantMarker> _nestedGrantRewards = new();
    private static ConditionalWeakTable<MerchantRelicEntry, Baseline> _merchantBaselines = new();
    private static ConditionalWeakTable<EventOption, AncientOptionBaseline> _ancientOptionBaselines = new();

    internal static void ReconcileCurrentOffers()
    {
        foreach (RewardsSet set in ContentBanLiveOfferService.GetTrackedRewardSets())
            ReconcileRewardsSet(set);
        ReconcileMerchant();
        ReconcileAncients();
    }

    internal static IReadOnlyList<EventOption> ReconcileAncientInitial(
        AncientEventModel ancient,
        IReadOnlyList<EventOption> options)
    {
        if (ancient.Owner is null
            || !OneRelicModeService.TryGetSelectedRelic(ancient.Owner, out RelicModel selected))
        {
            return options;
        }

        foreach (EventOption option in options)
            ApplyAncientOption(ancient, option, selected);
        return options;
    }

    internal static void ReconcileRewardsSet(RewardsSet set)
    {
        foreach (RelicReward reward in set.Rewards.OfType<RelicReward>().Where(reward => !reward.SuccessfullySelected))
        {
            RelicModel? current = reward.Relic;
            if (current is null)
                continue;

            if (_nestedGrantRewards.TryGetValue(reward, out _))
            {
                RestoreNestedGrantReward(reward, current);
                continue;
            }

            if (OneRelicModeService.TryGetSelectedRelic(set.Player, out RelicModel selected))
            {
                Baseline baseline = GetOrCaptureBaseline(_rewardBaselines, reward, current);
                if (current.CanonicalInstance.Id == selected.Id
                    && RelicReplacementProvenance.IsForced(RelicReplacementSource.OneRelic, current))
                {
                    continue;
                }

                RelicModel replacement = RelicReplacementProvenance.CreateOccurrence(
                    RelicReplacementSource.OneRelic,
                    selected,
                    baseline.Relic);
                RelicRewardField.SetValue(reward, replacement);
                if (PredeterminedRelicRewardField.GetValue(reward) is not null)
                    PredeterminedRelicRewardField.SetValue(reward, replacement);
                ContentBanLiveOfferService.RefreshTrackedReward(reward);
            }
            else if (_rewardBaselines.TryGetValue(reward, out Baseline? baseline))
            {
                RelicRewardField.SetValue(reward, baseline.Relic);
                if (PredeterminedRelicRewardField.GetValue(reward) is not null)
                    PredeterminedRelicRewardField.SetValue(reward, baseline.Relic);
                _rewardBaselines.Remove(reward);
                ContentBanLiveOfferService.RefreshTrackedReward(reward);
            }
        }
    }

    internal static void Reset()
    {
        _rewardBaselines = new ConditionalWeakTable<RelicReward, Baseline>();
        _nestedGrantRewards = new ConditionalWeakTable<RelicReward, NestedGrantMarker>();
        _merchantBaselines = new ConditionalWeakTable<MerchantRelicEntry, Baseline>();
        _ancientOptionBaselines = new ConditionalWeakTable<EventOption, AncientOptionBaseline>();
    }

    internal static void ExcludeNestedGrant(RewardsSet set)
    {
        ExcludeNestedGrants(set.Rewards);
    }

    internal static void ExcludeNestedGrants(IEnumerable<Reward> rewards)
    {
        foreach (RelicReward reward in rewards.OfType<RelicReward>())
        {
            if (!_nestedGrantRewards.TryGetValue(reward, out _))
                _nestedGrantRewards.Add(reward, new NestedGrantMarker());
        }
    }

    internal static IDisposable? BeginNestedGrantSelection(RelicReward reward)
    {
        return _nestedGrantRewards.TryGetValue(reward, out _)
            ? OneRelicModeService.BeginExactRelicGrant()
            : null;
    }

    private static void RestoreNestedGrantReward(RelicReward reward, RelicModel current)
    {
        if (!RelicReplacementProvenance.TryGetOriginal(
                RelicReplacementSource.OneRelic,
                current,
                out RelicModel original))
        {
            return;
        }

        RelicRewardField.SetValue(reward, original);
        if (PredeterminedRelicRewardField.GetValue(reward) is not null)
            PredeterminedRelicRewardField.SetValue(reward, original);
        ContentBanLiveOfferService.RefreshTrackedReward(reward);
    }

    private static void ReconcileMerchant()
    {
        RunState? runState;
        try
        {
            runState = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        }
        catch
        {
            return;
        }

        if (runState?.CurrentRoom is not MerchantRoom room)
            return;

        foreach (MerchantInventory inventory in room.Inventories)
        {
            foreach (MerchantRelicEntry entry in inventory.RelicEntries)
            {
                RelicModel? current = entry.Model;
                if (current is null)
                    continue;

                if (OneRelicModeService.TryGetSelectedRelic(inventory.Player, out RelicModel selected))
                {
                    Baseline baseline = GetOrCaptureBaseline(_merchantBaselines, entry, current);
                    if (current.CanonicalInstance.Id == selected.Id
                        && RelicReplacementProvenance.IsForced(RelicReplacementSource.OneRelic, current))
                    {
                        continue;
                    }

                    MerchantRelicModelField.SetValue(entry,
                        RelicReplacementProvenance.CreateOccurrence(
                            RelicReplacementSource.OneRelic,
                            selected,
                            baseline.Relic));
                    entry.OnMerchantInventoryUpdated();
                }
                else if (_merchantBaselines.TryGetValue(entry, out Baseline? baseline))
                {
                    MerchantRelicModelField.SetValue(entry, baseline.Relic);
                    _merchantBaselines.Remove(entry);
                    entry.OnMerchantInventoryUpdated();
                }
            }
        }
    }

    private static void ReconcileAncients()
    {
        RunState? runState;
        try
        {
            runState = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        }
        catch
        {
            return;
        }

        if (runState?.CurrentRoom is not EventRoom)
            return;

        foreach (EventModel eventModel in RunManager.Instance.EventSynchronizer.Events)
        {
            if (eventModel is not AncientEventModel ancient || ancient.Owner is null)
                continue;
            if (AncientGeneratedOptionsField.GetValue(ancient) is not List<EventOption> generated
                || generated.Any(option => option.WasChosen)
                || !generated.SequenceEqual(ancient.CurrentOptions))
            {
                continue;
            }

            bool hasSelection = OneRelicModeService.TryGetSelectedRelic(ancient.Owner, out RelicModel selected);
            bool changed = false;
            foreach (EventOption option in generated)
            {
                if (!_ancientOptionBaselines.TryGetValue(option, out AncientOptionBaseline? baseline))
                {
                    if (!hasSelection || option.Relic is null)
                        continue;
                    baseline = CaptureAncientBaseline(option);
                    _ancientOptionBaselines.Add(option, baseline);
                }

                if (hasSelection)
                    ApplyAncientOption(ancient, option, selected);
                else
                    RestoreAncientOption(option, baseline);
                changed = true;
            }

            if (changed)
                SetEventStateMethod.Invoke(ancient, [ancient.Description, generated]);
        }
    }

    private static void ApplyAncientOption(AncientEventModel ancient, EventOption option, RelicModel selected)
    {
        if (option.Relic is null)
            return;

        if (!_ancientOptionBaselines.TryGetValue(option, out AncientOptionBaseline? baseline))
        {
            baseline = CaptureAncientBaseline(option);
            _ancientOptionBaselines.Add(option, baseline);
        }

        RelicModel occurrence = RelicReplacementProvenance.CreateOccurrence(
            RelicReplacementSource.OneRelic,
            selected,
            baseline.Relic);
        occurrence.Owner = ancient.Owner!;
        option.WithRelic(occurrence);
        EventOptionTitleField.SetValue(option, occurrence.Title);
        EventOptionDescriptionField.SetValue(option, occurrence.DynamicEventDescription);
        EventOptionHistoryNameField.SetValue(option, occurrence.Title);
        option.HoverTips = occurrence.HoverTipsExcludingRelic;
    }

    private static AncientOptionBaseline CaptureAncientBaseline(EventOption option)
        => new(
            option.Relic!,
            option.Title,
            option.Description,
            option.HistoryName,
            option.HoverTips);

    private static void RestoreAncientOption(EventOption option, AncientOptionBaseline baseline)
    {
        option.WithRelic(baseline.Relic);
        EventOptionTitleField.SetValue(option, baseline.Title);
        EventOptionDescriptionField.SetValue(option, baseline.Description);
        EventOptionHistoryNameField.SetValue(option, baseline.HistoryName);
        option.HoverTips = baseline.HoverTips;
        _ancientOptionBaselines.Remove(option);
    }

    private static Baseline GetOrCaptureBaseline<TKey>(
        ConditionalWeakTable<TKey, Baseline> table,
        TKey key,
        RelicModel current)
        where TKey : class
    {
        bool hasProvenanceBaseline = RelicReplacementProvenance.TryGetOriginal(
            RelicReplacementSource.OneRelic,
            current,
            out RelicModel original);
        if (table.TryGetValue(key, out Baseline? existing))
        {
            if (!hasProvenanceBaseline || ReferenceEquals(existing.Relic, original))
                return existing;
            table.Remove(key);
        }

        RelicModel baselineRelic = hasProvenanceBaseline ? original : current;
        Baseline baseline = new(baselineRelic);
        table.Add(key, baseline);
        return baseline;
    }

    private sealed record Baseline(RelicModel Relic);

    private sealed class NestedGrantMarker
    {
    }

    private sealed record AncientOptionBaseline(
        RelicModel Relic,
        LocString Title,
        LocString Description,
        LocString HistoryName,
        IEnumerable<IHoverTip> HoverTips);
}
