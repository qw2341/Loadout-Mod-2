#nullable enable

namespace Loadout.Patches.ContentBans;

using Godot;
using HarmonyLib;
using Loadout.Services.ContentBans;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeginRewardsSet))]
internal static class ContentBanRewardsSetTrackingPatch
{
    [HarmonyPostfix]
    internal static void Postfix(RewardsSet set) => ContentBanLiveOfferService.TrackRewardsSet(set);
}

[HarmonyPatch(typeof(MerchantRoom), nameof(MerchantRoom.EnterInternal))]
internal static class ContentBanMerchantContextPatch
{
    [HarmonyPostfix]
    internal static void Postfix(ref Task __result) => __result = ApplyAfterEnterAsync(__result);

    private static async Task ApplyAfterEnterAsync(Task nativeTask)
    {
        await nativeTask;
        ContentBanLiveOfferService.ApplyPendingContexts();
    }
}

[HarmonyPatch(typeof(EventRoom), nameof(EventRoom.EnterInternal))]
internal static class ContentBanEventContextPatch
{
    [HarmonyPostfix]
    internal static void Postfix(ref Task __result) => __result = ApplyAfterEnterAsync(__result);

    private static async Task ApplyAfterEnterAsync(Task nativeTask)
    {
        await nativeTask;
        ContentBanLiveOfferService.ApplyPendingContexts();
    }
}

[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.GenerateWithoutOffering))]
internal static class ContentBanEmptyRewardRemovalPatch
{
    [HarmonyPostfix]
    internal static void Postfix(RewardsSet __instance, ref Task __result)
        => __result = RemoveEmptyContentRewardsAsync(__instance, __result);

    private static async Task RemoveEmptyContentRewardsAsync(RewardsSet set, Task nativeTask)
    {
        await nativeTask;
        set.Rewards.RemoveAll(reward => reward is (CardReward or RelicReward or PotionReward) && !reward.IsPopulated);
    }
}

[HarmonyPatch(typeof(NRewardButton), nameof(NRewardButton.Create))]
internal static class ContentBanRewardButtonTrackingPatch
{
    [HarmonyPostfix]
    internal static void Postfix(Reward reward, NRewardButton __result)
        => ContentBanLiveOfferService.TrackRewardButton(reward, __result);
}

[HarmonyPatch(typeof(CardReward), nameof(CardReward.Populate))]
internal static class ContentBanCardRewardPopulatePatch
{
    private static readonly FieldInfo CardsField = AccessTools.Field(typeof(CardReward), "_cards");
    private static readonly FieldInfo ManualField = AccessTools.Field(typeof(CardReward), "_cardsWereManuallySet");
    private static readonly FieldInfo OptionsField = AccessTools.Field(typeof(CardReward), "<Options>k__BackingField");

    [HarmonyPrefix]
    internal static bool Prefix(CardReward __instance)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Card))
            return true;
        List<CardCreationResult> cards = (List<CardCreationResult>)CardsField.GetValue(__instance)!;
        for (int index = cards.Count - 1; index >= 0; index--)
        {
            if (!ContentBanService.IsBanned(cards[index].Card))
                continue;
            CardModel card = cards[index].Card;
            cards.RemoveAt(index);
            if (card.Pile is null && !card.HasBeenRemovedFromState)
                card.RemoveFromState();
        }

        CardCreationOptions options = (CardCreationOptions)OptionsField.GetValue(__instance)!;
        return (bool)ManualField.GetValue(__instance)! || options.GetPossibleCards(__instance.Player).Any();
    }

    [HarmonyFinalizer]
    internal static Exception? Finalizer(CardReward __instance, Exception? __exception)
    {
        if (__exception is not InvalidOperationException exception
            || !exception.Message.StartsWith("Tried to create a card for a reward", StringComparison.Ordinal)
            || !ContentBanService.HasAnyBans(ContentBanKind.Card))
            return __exception;
        return null;
    }
}

[HarmonyPatch(typeof(MerchantCardEntry), nameof(MerchantCardEntry.Populate))]
internal static class ContentBanMerchantCardPopulatePatch
{
    private static readonly FieldInfo ResultField = AccessTools.Field(typeof(MerchantCardEntry), "<CreationResult>k__BackingField");

    [HarmonyFinalizer]
    internal static Exception? Finalizer(MerchantCardEntry __instance, Exception? __exception)
    {
        if (__exception is not InvalidOperationException exception
            || (!exception.Message.StartsWith("Can't generate valid rarity for merchant card", StringComparison.Ordinal)
                && !exception.Message.StartsWith("Sequence contains no elements", StringComparison.Ordinal))
            || !ContentBanService.HasAnyBans(ContentBanKind.Card))
            return __exception;
        if (ResultField.GetValue(__instance) is CardCreationResult { Card: { } card }
            && card.Pile is null && !card.HasBeenRemovedFromState)
            card.RemoveFromState();
        ResultField.SetValue(__instance, null);
        return null;
    }
}

[HarmonyPatch(typeof(RelicReward), nameof(RelicReward.Populate))]
internal static class ContentBanRelicRewardPopulatePatch
{
    private static readonly FieldInfo RelicField = AccessTools.Field(typeof(RelicReward), "_relic");
    private static readonly FieldInfo PredeterminedField = AccessTools.Field(typeof(RelicReward), "_predeterminedRelic");
    private static readonly FieldInfo RngOverrideField = AccessTools.Field(typeof(Reward), "_rngOverride");

    [HarmonyPrefix]
    internal static bool Prefix(RelicReward __instance)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Relic))
            return true;
        RelicModel? current = __instance.Relic;
        if (current is not null && !ContentBanService.IsBanned(current))
            return true;

        RelicRarity rarity = current?.Rarity ?? __instance.Rarity;
        MegaCrit.Sts2.Core.Random.Rng? rngOverride = (MegaCrit.Sts2.Core.Random.Rng?)RngOverrideField.GetValue(__instance);
        RelicModel? replacement = rarity == RelicRarity.None && rngOverride is not null
            ? RelicFactory.PullNextRelicFromFront(__instance.Player, rngOverride)?.ToMutable()
            : rarity == RelicRarity.None
                ? RelicFactory.PullNextRelicFromFront(__instance.Player)?.ToMutable()
                : RelicFactory.PullNextRelicFromFront(__instance.Player, rarity)?.ToMutable();
        if (replacement is not null && ContentBanService.IsBanned(replacement))
            replacement = null;
        RelicField.SetValue(__instance, replacement);
        if (PredeterminedField.GetValue(__instance) is not null)
            PredeterminedField.SetValue(__instance, replacement);
        return false;
    }
}

[HarmonyPatch(typeof(PotionReward), nameof(PotionReward.Populate))]
internal static class ContentBanPotionRewardPopulatePatch
{
    private static readonly PropertyInfo PotionProperty = AccessTools.Property(typeof(PotionReward), nameof(PotionReward.Potion));
    private static readonly FieldInfo RngOverrideField = AccessTools.Field(typeof(Reward), "_rngOverride");

    [HarmonyPrefix]
    internal static bool Prefix(PotionReward __instance)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Potion))
            return true;
        if (__instance.Potion is { } current && !ContentBanService.IsBanned(current))
            return true;
        MegaCrit.Sts2.Core.Random.Rng rng = (MegaCrit.Sts2.Core.Random.Rng?)RngOverrideField.GetValue(__instance)
                                                   ?? __instance.Player.PlayerRng.Rewards;
        PotionModel? replacement = PotionFactory.CreateRandomPotionOutOfCombat(
            __instance.Player,
            rng)?.ToMutable();
        PotionProperty.SetValue(__instance, replacement);
        return false;
    }
}

[HarmonyPatch(typeof(MerchantRelicEntry), "FillSlot")]
internal static class ContentBanMerchantRelicFillPatch
{
    private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(MerchantEntry), "_player");
    private static readonly FieldInfo ModelField = AccessTools.Field(typeof(MerchantRelicEntry), "<Model>k__BackingField");
    private static readonly MethodInfo SetModelMethod = AccessTools.Method(typeof(MerchantRelicEntry), "SetModel");

    [HarmonyPrefix]
    internal static bool Prefix(MerchantRelicEntry __instance, RelicRarity rarity, IEnumerable<RelicModel>? blacklist)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Relic))
            return true;
        MegaCrit.Sts2.Core.Entities.Players.Player player = (MegaCrit.Sts2.Core.Entities.Players.Player)PlayerField.GetValue(__instance)!;
        HashSet<RelicModel> blocked = (blacklist ?? []).ToHashSet();
        RelicModel? model = RelicFactory.PullNextRelicFromBack(
            player,
            rarity,
            relic => !blocked.Contains(relic) && relic.IsAllowedInShops)?.ToMutable();
        if (model is not null && ContentBanService.IsBanned(model))
            model = null;
        if (model is null)
            ModelField.SetValue(__instance, null);
        else
            SetModelMethod.Invoke(__instance, [model]);
        return false;
    }
}

[HarmonyPatch(typeof(MerchantPotionEntry), "FillSlot")]
internal static class ContentBanMerchantPotionFillPatch
{
    private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(MerchantEntry), "_player");
    private static readonly FieldInfo ModelField = AccessTools.Field(typeof(MerchantPotionEntry), "<Model>k__BackingField");

    [HarmonyPrefix]
    internal static bool Prefix(MerchantPotionEntry __instance, IEnumerable<PotionModel> blacklist)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Potion))
            return true;
        MegaCrit.Sts2.Core.Entities.Players.Player player = (MegaCrit.Sts2.Core.Entities.Players.Player)PlayerField.GetValue(__instance)!;
        PotionModel? model = PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Shops, blacklist)?.ToMutable();
        ModelField.SetValue(__instance, model);
        if (model is not null)
        {
            __instance.CalcCost();
            SaveManager.Instance.MarkPotionAsSeen(model);
        }
        return false;
    }
}

[HarmonyPatch]
internal static class ContentBanAncientInitialOptionsPatch
{
    internal static MethodBase TargetMethod()
        => AccessTools.Method(typeof(AncientEventModel), "GenerateInitialOptionsWrapper")
           ?? throw new MissingMethodException(typeof(AncientEventModel).FullName, "GenerateInitialOptionsWrapper");

    [HarmonyPostfix]
    internal static void Postfix(AncientEventModel __instance, ref IReadOnlyList<EventOption> __result)
        => __result = ContentBanLiveOfferService.ReconcileAncientInitial(__instance, __result);
}

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
internal static class ContentBanEventOptionClickPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(NEventRoom __instance, EventOption option)
    {
        if (option.Relic is not { } relic || !ContentBanService.IsBanned(relic))
            return true;
        NEventOptionButton? button = __instance.Layout?.OptionButtons.FirstOrDefault(candidate => ReferenceEquals(candidate.Option, option));
        if (button is not null)
            ContentBanVisuals.Wiggle(button);
        return false;
    }
}

[HarmonyPatch(typeof(EventOption), nameof(EventOption.Chosen))]
internal static class ContentBanEventOptionChosenPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(EventOption __instance, ref System.Threading.Tasks.Task __result)
    {
        if (__instance.Relic is not { } relic || !ContentBanService.IsBanned(relic))
            return true;
        __result = System.Threading.Tasks.Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(Reward), nameof(Reward.SelectUnsynchronized))]
internal static class ContentBanRewardSelectionPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(Reward __instance, ref System.Threading.Tasks.Task<bool> __result)
    {
        bool blocked = __instance switch
        {
            RelicReward { Relic: { } relic } => ContentBanService.IsBanned(relic),
            PotionReward { Potion: { } potion } => ContentBanService.IsBanned(potion),
            _ => false
        };
        if (!blocked)
            return true;
        __result = System.Threading.Tasks.Task.FromResult(false);
        return false;
    }
}

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
internal static class ContentBanMerchantPurchasePatch
{
    [HarmonyPrefix]
    internal static bool Prefix(MerchantEntry __instance, ref System.Threading.Tasks.Task<bool> __result)
    {
        bool blocked = __instance switch
        {
            MerchantCardEntry { CreationResult.Card: { } card } => ContentBanService.IsBanned(card),
            MerchantRelicEntry { Model: { } relic } => ContentBanService.IsBanned(relic),
            MerchantPotionEntry { Model: { } potion } => ContentBanService.IsBanned(potion),
            _ => false
        };
        if (!blocked)
            return true;
        __result = System.Threading.Tasks.Task.FromResult(false);
        return false;
    }
}
