#nullable enable

namespace Loadout.Services.ContentBans;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

internal sealed class ContentBanOfferReconciliation
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("owner")]
    public ulong OwnerNetId { get; set; }
    [JsonPropertyName("container")]
    public int ContainerIndex { get; set; }
    [JsonPropertyName("entry")]
    public int EntryIndex { get; set; }
    [JsonPropertyName("option")]
    public int OptionIndex { get; set; } = -1;
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;
    [JsonPropertyName("cost")]
    public int Cost { get; set; } = -1;
    [JsonPropertyName("rejectedId")]
    public string RejectedId { get; set; } = string.Empty;
    [JsonPropertyName("removeContainer")]
    public bool RemoveContainer { get; set; }
}

internal static class ContentBanLiveOfferService
{
    private const string RewardCardKind = "reward-card";
    private const string RewardRelicKind = "reward-relic";
    private const string RewardPotionKind = "reward-potion";
    private const string MerchantCardKind = "merchant-card";
    private const string MerchantRelicKind = "merchant-relic";
    private const string MerchantPotionKind = "merchant-potion";
    private const string AncientKind = "ancient";

    private static readonly List<WeakReference<RewardsSet>> RewardSets = [];
    private static readonly Dictionary<Reward, WeakReference<NRewardButton>> RewardButtons = [];
    private static readonly List<ContentBanOfferReconciliation> PendingRewardReconciliations = [];
    private static readonly List<ContentBanOfferReconciliation> PendingContextReconciliations = [];
    private static readonly FieldInfo RewardsSetSynchronizerField = AccessTools.Field(typeof(RewardsSet), "_synchronizer");
    private static readonly FieldInfo CardRewardCardsField = AccessTools.Field(typeof(CardReward), "_cards");
    private static readonly FieldInfo CardRewardScreenField = AccessTools.Field(typeof(CardReward), "_currentlyShownScreen");
    private static readonly FieldInfo CardRewardOptionsField = AccessTools.Field(typeof(CardReward), "<Options>k__BackingField");
    private static readonly FieldInfo RewardRngOverrideField = AccessTools.Field(typeof(Reward), "_rngOverride");
    private static readonly MethodInfo CreateSingleRewardCardMethod = AccessTools.GetDeclaredMethods(typeof(CardFactory))
        .Single(method => method.Name == nameof(CardFactory.CreateForReward)
                          && method.IsPrivate
                          && method.ReturnType == typeof(CardModel));
    private static readonly FieldInfo RelicRewardField = AccessTools.Field(typeof(RelicReward), "_relic");
    private static readonly FieldInfo PredeterminedRelicRewardField = AccessTools.Field(typeof(RelicReward), "_predeterminedRelic");
    private static readonly PropertyInfo PotionRewardProperty = AccessTools.Property(typeof(PotionReward), nameof(PotionReward.Potion));
    private static readonly FieldInfo PotionRewardIconField = AccessTools.Field(typeof(PotionReward), "_icon");
    private static readonly FieldInfo MerchantCardResultField = AccessTools.Field(typeof(MerchantCardEntry), "<CreationResult>k__BackingField");
    private static readonly FieldInfo MerchantRelicModelField = AccessTools.Field(typeof(MerchantRelicEntry), "<Model>k__BackingField");
    private static readonly FieldInfo MerchantPotionModelField = AccessTools.Field(typeof(MerchantPotionEntry), "<Model>k__BackingField");
    private static readonly FieldInfo MerchantCostField = AccessTools.Field(typeof(MerchantEntry), "_cost");
    private static readonly MethodInfo MerchantRelicFillMethod = AccessTools.Method(typeof(MerchantRelicEntry), "FillSlot");
    private static readonly MethodInfo MerchantPotionFillMethod = AccessTools.Method(typeof(MerchantPotionEntry), "FillSlot");
    private static readonly FieldInfo AncientGeneratedOptionsField = AccessTools.Field(typeof(AncientEventModel), "_generatedOptions");
    private static readonly MethodInfo SetEventStateMethod = AccessTools.Method(typeof(EventModel), "SetEventState");

    internal static void TrackRewardsSet(RewardsSet set)
    {
        Prune();
        if (!RewardSets.Any(reference => reference.TryGetTarget(out RewardsSet? existing) && ReferenceEquals(existing, set)))
            RewardSets.Add(new WeakReference<RewardsSet>(set));
        ApplyPendingRewards();
    }

    internal static void TrackRewardButton(Reward reward, NRewardButton button)
        => RewardButtons[reward] = new WeakReference<NRewardButton>(button);

    internal static void Reset()
    {
        RewardSets.Clear();
        RewardButtons.Clear();
        PendingRewardReconciliations.Clear();
        PendingContextReconciliations.Clear();
    }

    internal static IReadOnlyList<ContentBanOfferReconciliation> ReconcileHost(ContentBanChangedEvent change)
    {
        if (!change.BecameBanned)
            return [];

        List<ContentBanOfferReconciliation> reconciliations = [];
        ReconcileRewards(change.Target, reconciliations);
        ReconcileMerchant(change.Target, reconciliations);
        ReconcileCurrentAncient(change.Target, reconciliations);
        return reconciliations;
    }

    internal static void Apply(IReadOnlyList<ContentBanOfferReconciliation>? reconciliations)
    {
        if (reconciliations is null || reconciliations.Count == 0)
            return;

        foreach (ContentBanOfferReconciliation reconciliation in reconciliations)
        {
            switch (reconciliation.Kind)
            {
                case RewardCardKind:
                case RewardRelicKind:
                case RewardPotionKind:
                    if (!ApplyReward(reconciliation))
                        QueuePendingReward(reconciliation);
                    break;
                case MerchantCardKind:
                case MerchantRelicKind:
                case MerchantPotionKind:
                    if (!ApplyMerchant(reconciliation))
                        QueuePendingContext(reconciliation);
                    break;
                case AncientKind:
                    if (!ApplyAncient(reconciliation))
                        QueuePendingContext(reconciliation);
                    break;
            }
        }
    }

    internal static IReadOnlyList<EventOption> ReconcileAncientInitial(AncientEventModel ancient, IReadOnlyList<EventOption> options)
    {
        List<EventOption> next = options.ToList();
        bool changed = false;
        for (int index = next.Count - 1; index >= 0; index--)
        {
            RelicModel? relic = next[index].Relic;
            if (relic is null || !ContentBanService.IsBanned(relic))
                continue;

            EventOption? replacement = FindAncientReplacement(ancient, next);
            if (replacement is null)
                next.RemoveAt(index);
            else
                next[index] = replacement;
            changed = true;
        }

        if (changed)
            AncientGeneratedOptionsField.SetValue(ancient, next);
        return next;
    }

    private static void ReconcileRewards(ContentBanTarget target, ICollection<ContentBanOfferReconciliation> output)
    {
        Prune();
        foreach (WeakReference<RewardsSet> reference in RewardSets.ToList())
        {
            if (!reference.TryGetTarget(out RewardsSet? set))
                continue;
            if (IsCompleted(set))
                continue;

            for (int rewardIndex = set.Rewards.Count - 1; rewardIndex >= 0; rewardIndex--)
            {
                Reward reward = set.Rewards[rewardIndex];
                if (reward.SuccessfullySelected)
                    continue;

                if (reward is CardReward cardReward && target.Kind == ContentBanKind.Card)
                    ReconcileCardReward(set, rewardIndex, cardReward, target, output);
                else if (reward is RelicReward relicReward && target.Kind == ContentBanKind.Relic
                         && relicReward.Relic is { } relic && Matches(target, relic.Id))
                    ReconcileRelicReward(set, rewardIndex, relicReward, output);
                else if (reward is PotionReward potionReward && target.Kind == ContentBanKind.Potion
                         && potionReward.Potion is { } potion && Matches(target, potion.Id))
                    ReconcilePotionReward(set, rewardIndex, potionReward, output);
            }
        }
    }

    private static void ReconcileCardReward(
        RewardsSet set,
        int rewardIndex,
        CardReward reward,
        ContentBanTarget target,
        ICollection<ContentBanOfferReconciliation> output)
    {
        List<CardCreationResult> cards = (List<CardCreationResult>)CardRewardCardsField.GetValue(reward)!;
        CardCreationOptions options = (CardCreationOptions)CardRewardOptionsField.GetValue(reward)!;
        ContentBanOfferReconciliation? lastReconciliation = null;
        for (int slot = cards.Count - 1; slot >= 0; slot--)
        {
            CardModel rejected = cards[slot].Card;
            if (!Matches(target, rejected.Id))
                continue;

            CardModel? replacement = null;
            try
            {
                CardModel[] blacklist = cards.Where((_, index) => index != slot)
                    .Select(result => result.Card.CanonicalInstance)
                    .ToArray();
                replacement = (CardModel?)CreateSingleRewardCardMethod.Invoke(null, [set.Player, blacklist, options]);
            }
            catch
            {
                // No legal option means this slot is omitted.
            }

            RemoveUnownedCard(rejected);
            if (replacement is null || ContentBanService.IsBanned(replacement))
                cards.RemoveAt(slot);
            else
                cards[slot] = new CardCreationResult(replacement);

            lastReconciliation = new ContentBanOfferReconciliation
            {
                Kind = RewardCardKind,
                OwnerNetId = set.Player.NetId,
                ContainerIndex = set.Id,
                EntryIndex = rewardIndex,
                OptionIndex = slot,
                Payload = replacement is null ? string.Empty : Serialize(replacement.ToSerializable()),
                RejectedId = rejected.Id.ToString()
            };
            output.Add(lastReconciliation);
        }
        if (cards.Count == 0 && lastReconciliation is not null)
        {
            lastReconciliation.RemoveContainer = true;
            RemoveReward(set, rewardIndex, reward);
        }
        else
        {
            RefreshCardReward(reward, cards);
        }
    }

    private static void ReconcileRelicReward(
        RewardsSet set,
        int rewardIndex,
        RelicReward reward,
        ICollection<ContentBanOfferReconciliation> output)
    {
        RelicModel rejected = reward.Relic!;
        MegaCrit.Sts2.Core.Random.Rng? rngOverride = (MegaCrit.Sts2.Core.Random.Rng?)RewardRngOverrideField.GetValue(reward);
        RelicModel? replacement = rejected.Rarity == RelicRarity.None && rngOverride is not null
            ? RelicFactory.PullNextRelicFromFront(set.Player, rngOverride)?.ToMutable()
            : rejected.Rarity == RelicRarity.None
                ? RelicFactory.PullNextRelicFromFront(set.Player)?.ToMutable()
                : RelicFactory.PullNextRelicFromFront(set.Player, rejected.Rarity)?.ToMutable();
        if (replacement is not null && ContentBanService.IsBanned(replacement))
            replacement = null;
        RelicRewardField.SetValue(reward, replacement);
        if (PredeterminedRelicRewardField.GetValue(reward) is not null)
            PredeterminedRelicRewardField.SetValue(reward, replacement);
        ContentBanOfferReconciliation reconciliation = new()
        {
            Kind = RewardRelicKind,
            OwnerNetId = set.Player.NetId,
            ContainerIndex = set.Id,
            EntryIndex = rewardIndex,
            Payload = replacement is null ? string.Empty : Serialize(replacement.ToSerializable()),
            RejectedId = rejected.Id.ToString(),
            RemoveContainer = replacement is null
        };
        output.Add(reconciliation);
        if (replacement is null)
            RemoveReward(set, rewardIndex, reward);
        else
            RefreshRewardButton(reward);
    }

    private static void ReconcilePotionReward(
        RewardsSet set,
        int rewardIndex,
        PotionReward reward,
        ICollection<ContentBanOfferReconciliation> output)
    {
        PotionModel rejected = reward.Potion!;
        MegaCrit.Sts2.Core.Random.Rng rng = (MegaCrit.Sts2.Core.Random.Rng?)RewardRngOverrideField.GetValue(reward)
                                           ?? set.Player.PlayerRng.Rewards;
        PotionModel? replacement = PotionFactory.CreateRandomPotionOutOfCombat(set.Player, rng)?.ToMutable();
        PotionRewardProperty.SetValue(reward, replacement);
        PotionRewardIconField.SetValue(reward, null);
        ContentBanOfferReconciliation reconciliation = new()
        {
            Kind = RewardPotionKind,
            OwnerNetId = set.Player.NetId,
            ContainerIndex = set.Id,
            EntryIndex = rewardIndex,
            Payload = replacement is null ? string.Empty : Serialize(replacement.ToSerializable(-1)),
            RejectedId = rejected.Id.ToString(),
            RemoveContainer = replacement is null
        };
        output.Add(reconciliation);
        if (replacement is null)
            RemoveReward(set, rewardIndex, reward);
        else
            RefreshRewardButton(reward);
    }

    private static void ReconcileMerchant(ContentBanTarget target, ICollection<ContentBanOfferReconciliation> output)
    {
        if (!TryGetCurrentRun(out RunState? runState) || runState!.CurrentRoom is not MerchantRoom room)
            return;

        for (int inventoryIndex = 0; inventoryIndex < room.Inventories.Count; inventoryIndex++)
        {
            MerchantInventory inventory = room.Inventories[inventoryIndex];
            if (target.Kind == ContentBanKind.Card)
            {
                List<MerchantCardEntry> entries = inventory.CardEntries.ToList();
                for (int index = 0; index < entries.Count; index++)
                {
                    MerchantCardEntry entry = entries[index];
                    CardModel? old = entry.CreationResult?.Card;
                    if (old is null || !Matches(target, old.Id))
                        continue;
                    RemoveUnownedCard(old);
                    MerchantCardResultField.SetValue(entry, null);
                    try { entry.Populate(); } catch { MerchantCardResultField.SetValue(entry, null); }
                    entry.OnMerchantInventoryUpdated();
                    output.Add(Reconciliation(MerchantCardKind, inventory.Player.NetId, inventoryIndex, index,
                        entry.CreationResult is null ? string.Empty : Serialize(entry.CreationResult.Card.ToSerializable()),
                        entry.CreationResult is null ? -1 : entry.Cost));
                }
            }
            else if (target.Kind == ContentBanKind.Relic)
            {
                for (int index = 0; index < inventory.RelicEntries.Count; index++)
                {
                    MerchantRelicEntry entry = inventory.RelicEntries[index];
                    RelicModel? old = entry.Model;
                    if (old is null || !Matches(target, old.Id))
                        continue;
                    MerchantRelicModelField.SetValue(entry, null);
                    HashSet<RelicModel> blacklist = inventory.RelicEntries.Where(candidate => !ReferenceEquals(candidate, entry))
                        .Select(candidate => candidate.Model?.CanonicalInstance).OfType<RelicModel>().ToHashSet();
                    try { MerchantRelicFillMethod.Invoke(entry, [old.Rarity, blacklist]); } catch { MerchantRelicModelField.SetValue(entry, null); }
                    entry.OnMerchantInventoryUpdated();
                    output.Add(Reconciliation(MerchantRelicKind, inventory.Player.NetId, inventoryIndex, index,
                        entry.Model is null ? string.Empty : Serialize(entry.Model.ToSerializable()),
                        entry.Model is null ? -1 : entry.Cost));
                }
            }
            else
            {
                for (int index = 0; index < inventory.PotionEntries.Count; index++)
                {
                    MerchantPotionEntry entry = inventory.PotionEntries[index];
                    PotionModel? old = entry.Model;
                    if (old is null || !Matches(target, old.Id))
                        continue;
                    MerchantPotionModelField.SetValue(entry, null);
                    HashSet<PotionModel> blacklist = inventory.PotionEntries.Where(candidate => !ReferenceEquals(candidate, entry))
                        .Select(candidate => candidate.Model?.CanonicalInstance).OfType<PotionModel>().ToHashSet();
                    try { MerchantPotionFillMethod.Invoke(entry, [blacklist]); } catch { MerchantPotionModelField.SetValue(entry, null); }
                    entry.OnMerchantInventoryUpdated();
                    output.Add(Reconciliation(MerchantPotionKind, inventory.Player.NetId, inventoryIndex, index,
                        entry.Model is null ? string.Empty : Serialize(entry.Model.ToSerializable(-1)),
                        entry.Model is null ? -1 : entry.Cost));
                }
            }
        }
    }

    private static void ReconcileCurrentAncient(ContentBanTarget target, ICollection<ContentBanOfferReconciliation> output)
    {
        if (target.Kind != ContentBanKind.Relic || !TryGetCurrentRun(out RunState? runState)
            || runState!.CurrentRoom is not EventRoom)
            return;

        foreach (EventModel eventModel in RunManager.Instance.EventSynchronizer.Events)
        {
            if (eventModel is not AncientEventModel ancient)
                continue;
            List<EventOption>? generated = (List<EventOption>?)AncientGeneratedOptionsField.GetValue(ancient);
            if (generated is null || generated.Any(option => option.WasChosen)
                                  || !generated.SequenceEqual(ancient.CurrentOptions))
                continue;

            for (int index = generated.Count - 1; index >= 0; index--)
            {
                RelicModel? relic = generated[index].Relic;
                if (relic is null || !Matches(target, relic.Id))
                    continue;
                EventOption? replacement = FindAncientReplacement(ancient, generated);
                string replacementId = replacement?.Relic?.Id.ToString() ?? string.Empty;
                if (replacement is null)
                    generated.RemoveAt(index);
                else
                    generated[index] = replacement;
                output.Add(Reconciliation(AncientKind, ancient.Owner!.NetId, 0, index, replacementId));
            }
            SetEventStateMethod.Invoke(ancient, [ancient.Description, generated]);
        }
    }

    private static bool ApplyReward(ContentBanOfferReconciliation reconciliation)
    {
        RewardsSet? set = FindRewardsSet(reconciliation.OwnerNetId, reconciliation.ContainerIndex);
        if (set is null || reconciliation.EntryIndex < 0)
            return false;

        if (reconciliation.Kind == RewardCardKind)
        {
            if (reconciliation.EntryIndex >= set.Rewards.Count
                || set.Rewards[reconciliation.EntryIndex] is not CardReward reward)
                return false;
            List<CardCreationResult> cards = (List<CardCreationResult>)CardRewardCardsField.GetValue(reward)!;
            int slot = reconciliation.OptionIndex;
            if (slot >= 0 && slot < cards.Count)
            {
                if (!string.Equals(cards[slot].Card.Id.ToString(), reconciliation.RejectedId, StringComparison.Ordinal))
                    return true;
                RemoveUnownedCard(cards[slot].Card);
                if (string.IsNullOrEmpty(reconciliation.Payload))
                    cards.RemoveAt(slot);
                else
                    cards[slot] = new CardCreationResult(DeserializeCard(reconciliation.Payload, set.Player));
            }
            else
            {
                return true;
            }
            if (reconciliation.RemoveContainer)
                RemoveReward(set, reconciliation.EntryIndex, reward);
            else
                RefreshCardReward(reward, cards);
            return true;
        }

        if (reconciliation.EntryIndex >= set.Rewards.Count)
            return false;
        Reward targetReward = set.Rewards[reconciliation.EntryIndex];
        if (targetReward is RelicReward relicReward && reconciliation.Kind == RewardRelicKind)
        {
            if (relicReward.Relic is not { } current
                || !string.Equals(current.Id.ToString(), reconciliation.RejectedId, StringComparison.Ordinal))
                return true;
            RelicModel? relic = string.IsNullOrEmpty(reconciliation.Payload) ? null : Deserialize<SerializableRelic, RelicModel>(reconciliation.Payload, RelicModel.FromSerializable);
            RelicRewardField.SetValue(relicReward, relic);
            if (PredeterminedRelicRewardField.GetValue(relicReward) is not null)
                PredeterminedRelicRewardField.SetValue(relicReward, relic);
            if (reconciliation.RemoveContainer)
                RemoveReward(set, reconciliation.EntryIndex, relicReward);
            else
                RefreshRewardButton(relicReward);
            return true;
        }
        else if (targetReward is PotionReward potionReward && reconciliation.Kind == RewardPotionKind)
        {
            if (potionReward.Potion is not { } current
                || !string.Equals(current.Id.ToString(), reconciliation.RejectedId, StringComparison.Ordinal))
                return true;
            PotionModel? potion = string.IsNullOrEmpty(reconciliation.Payload) ? null : Deserialize<SerializablePotion, PotionModel>(reconciliation.Payload, PotionModel.FromSerializable);
            PotionRewardProperty.SetValue(potionReward, potion);
            PotionRewardIconField.SetValue(potionReward, null);
            if (reconciliation.RemoveContainer)
                RemoveReward(set, reconciliation.EntryIndex, potionReward);
            else
                RefreshRewardButton(potionReward);
            return true;
        }
        return false;
    }

    private static void QueuePendingReward(ContentBanOfferReconciliation reconciliation)
    {
        PendingRewardReconciliations.RemoveAll(existing => existing.Kind == reconciliation.Kind
            && existing.OwnerNetId == reconciliation.OwnerNetId
            && existing.ContainerIndex == reconciliation.ContainerIndex
            && existing.EntryIndex == reconciliation.EntryIndex
            && existing.OptionIndex == reconciliation.OptionIndex);
        PendingRewardReconciliations.Add(reconciliation);
    }

    private static void ApplyPendingRewards()
    {
        for (int index = 0; index < PendingRewardReconciliations.Count;)
        {
            if (!ApplyReward(PendingRewardReconciliations[index]))
            {
                index++;
                continue;
            }
            PendingRewardReconciliations.RemoveAt(index);
        }
    }

    internal static void ApplyPendingContexts()
    {
        for (int index = 0; index < PendingContextReconciliations.Count;)
        {
            ContentBanOfferReconciliation reconciliation = PendingContextReconciliations[index];
            bool applied = reconciliation.Kind == AncientKind
                ? ApplyAncient(reconciliation)
                : ApplyMerchant(reconciliation);
            if (!applied)
            {
                index++;
                continue;
            }
            PendingContextReconciliations.RemoveAt(index);
        }
    }

    private static bool ApplyMerchant(ContentBanOfferReconciliation reconciliation)
    {
        if (!TryGetCurrentRun(out RunState? runState) || runState!.CurrentRoom is not MerchantRoom room)
            return false;
        MerchantInventory? inventory = room.Inventories.FirstOrDefault(candidate => candidate.Player.NetId == reconciliation.OwnerNetId);
        if (inventory is null)
            return false;

        if (reconciliation.Kind == MerchantCardKind)
        {
            MerchantCardEntry? entry = inventory.CardEntries.ElementAtOrDefault(reconciliation.EntryIndex);
            if (entry is null)
                return false;
            if (entry.CreationResult?.Card is { } old)
                RemoveUnownedCard(old);
            CardModel? card = string.IsNullOrEmpty(reconciliation.Payload) ? null : DeserializeCard(reconciliation.Payload, inventory.Player);
            MerchantCardResultField.SetValue(entry, card is null ? null : new CardCreationResult(card));
            if (reconciliation.Cost >= 0)
                MerchantCostField.SetValue(entry, reconciliation.Cost);
            entry.OnMerchantInventoryUpdated();
            return true;
        }
        else if (reconciliation.Kind == MerchantRelicKind)
        {
            MerchantRelicEntry? entry = inventory.RelicEntries.ElementAtOrDefault(reconciliation.EntryIndex);
            if (entry is null)
                return false;
            RelicModel? relic = string.IsNullOrEmpty(reconciliation.Payload) ? null : Deserialize<SerializableRelic, RelicModel>(reconciliation.Payload, RelicModel.FromSerializable);
            MerchantRelicModelField.SetValue(entry, relic);
            if (reconciliation.Cost >= 0)
                MerchantCostField.SetValue(entry, reconciliation.Cost);
            entry.OnMerchantInventoryUpdated();
            return true;
        }
        else
        {
            MerchantPotionEntry? entry = inventory.PotionEntries.ElementAtOrDefault(reconciliation.EntryIndex);
            if (entry is null)
                return false;
            PotionModel? potion = string.IsNullOrEmpty(reconciliation.Payload) ? null : Deserialize<SerializablePotion, PotionModel>(reconciliation.Payload, PotionModel.FromSerializable);
            MerchantPotionModelField.SetValue(entry, potion);
            if (reconciliation.Cost >= 0)
                MerchantCostField.SetValue(entry, reconciliation.Cost);
            entry.OnMerchantInventoryUpdated();
            return true;
        }
    }

    private static bool ApplyAncient(ContentBanOfferReconciliation reconciliation)
    {
        EventModel? eventModel = RunManager.Instance.EventSynchronizer.Events
            .FirstOrDefault(candidate => candidate.Owner?.NetId == reconciliation.OwnerNetId);
        if (eventModel is not AncientEventModel ancient)
            return false;
        List<EventOption>? generated = (List<EventOption>?)AncientGeneratedOptionsField.GetValue(ancient);
        if (generated is null || generated.Any(option => option.WasChosen)
                              || reconciliation.EntryIndex < 0 || reconciliation.EntryIndex >= generated.Count)
            return false;
        if (string.IsNullOrEmpty(reconciliation.Payload))
            generated.RemoveAt(reconciliation.EntryIndex);
        else
        {
            EventOption? replacement = ancient.AllPossibleOptions.FirstOrDefault(option => option.Relic is { } relic
                && string.Equals(relic.Id.ToString(), reconciliation.Payload, StringComparison.Ordinal));
            if (replacement is null)
                return false;
            generated[reconciliation.EntryIndex] = replacement;
        }
        SetEventStateMethod.Invoke(ancient, [ancient.Description, generated]);
        return true;
    }

    private static void QueuePendingContext(ContentBanOfferReconciliation reconciliation)
    {
        PendingContextReconciliations.RemoveAll(existing => existing.Kind == reconciliation.Kind
            && existing.OwnerNetId == reconciliation.OwnerNetId
            && existing.EntryIndex == reconciliation.EntryIndex);
        PendingContextReconciliations.Add(reconciliation);
    }

    private static EventOption? FindAncientReplacement(AncientEventModel ancient, IReadOnlyCollection<EventOption> current)
    {
        HashSet<string> used = current.Select(option => option.Relic?.Id.ToString())
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        return ancient.AllPossibleOptions.FirstOrDefault(option => option.Relic is { } relic
            && !ContentBanService.IsBanned(relic)
            && !used.Contains(relic.Id.ToString()));
    }

    private static void RefreshCardReward(CardReward reward, IReadOnlyList<CardCreationResult> cards)
    {
        object? screen = CardRewardScreenField.GetValue(reward);
        if (screen is not null)
        {
            AccessTools.Method(screen.GetType(), "RefreshOptions")?.Invoke(
                screen,
                [cards, CardRewardAlternative.Generate(reward)]);
        }
    }

    private static void RefreshRewardButton(Reward reward)
    {
        if (!RewardButtons.TryGetValue(reward, out WeakReference<NRewardButton>? reference)
            || !reference.TryGetTarget(out NRewardButton? button)
            || !GodotObject.IsInstanceValid(button)
            || !button.IsNodeReady())
            return;

        Control icon = button.GetNode<Control>("%Icon");
        foreach (Node child in icon.GetChildren())
            child.QueueFree();
        AccessTools.Method(typeof(NRewardButton), "Reload")?.Invoke(button, null);
    }

    private static void RemoveReward(RewardsSet set, int index, Reward reward)
    {
        if (index >= 0 && index < set.Rewards.Count && ReferenceEquals(set.Rewards[index], reward))
            set.Rewards.RemoveAt(index);
        if (RewardButtons.Remove(reward, out WeakReference<NRewardButton>? reference)
            && reference.TryGetTarget(out NRewardButton? button)
            && GodotObject.IsInstanceValid(button))
            button.QueueFree();
    }

    private static RewardsSet? FindRewardsSet(ulong ownerNetId, int setId)
    {
        Prune();
        return RewardSets.Select(reference => reference.TryGetTarget(out RewardsSet? set) ? set : null)
            .FirstOrDefault(set => set is not null && set.Player.NetId == ownerNetId && set.Id == setId);
    }

    private static bool TryGetCurrentRun(out RunState? runState)
    {
        try
        {
            runState = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
            return runState is not null;
        }
        catch
        {
            runState = null;
            return false;
        }
    }

    private static bool Matches(ContentBanTarget target, ModelId id)
        => string.Equals(target.Id, id.ToString(), StringComparison.Ordinal);

    private static ContentBanOfferReconciliation Reconciliation(
        string kind, ulong owner, int container, int entry, string payload, int cost = -1) => new()
    {
        Kind = kind,
        OwnerNetId = owner,
        ContainerIndex = container,
        EntryIndex = entry,
        Payload = payload,
        Cost = cost
    };

    private static void RemoveUnownedCard(CardModel card)
    {
        if (card.Pile is null && !card.HasBeenRemovedFromState)
            card.RemoveFromState();
    }

    private static CardModel DeserializeCard(string payload, MegaCrit.Sts2.Core.Entities.Players.Player owner)
    {
        CardModel card = Deserialize<SerializableCard, CardModel>(payload, CardModel.FromSerializable);
        owner.RunState.AddCard(card, owner);
        return card;
    }

    private static string Serialize<T>(T value) where T : IPacketSerializable
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        writer.Write(value);
        writer.ZeroByteRemainder();
        byte[] bytes = new byte[writer.BytePosition];
        Array.Copy(writer.Buffer, bytes, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static TResult Deserialize<TSave, TResult>(string payload, Func<TSave, TResult> factory)
        where TSave : IPacketSerializable, new()
    {
        PacketReader reader = new();
        reader.Reset(Convert.FromBase64String(payload));
        return factory(reader.Read<TSave>());
    }

    private static void Prune()
    {
        RewardSets.RemoveAll(reference => !reference.TryGetTarget(out RewardsSet? set) || IsCompleted(set));
        foreach (Reward reward in RewardButtons.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToList())
            RewardButtons.Remove(reward);
    }

    private static bool IsCompleted(RewardsSet set)
    {
        if (set.Id < 0)
            return false;
        try
        {
            return ((RewardsSetSynchronizer)RewardsSetSynchronizerField.GetValue(set)!).IsRewardsSetCompleted(set);
        }
        catch
        {
            return false;
        }
    }
}
