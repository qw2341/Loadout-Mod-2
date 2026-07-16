#nullable enable

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

public static class LoadoutKeywordMechanics
{
    private static readonly FieldInfo? EnergyCostField = AccessTools.Field(typeof(CardModel), "_energyCost");

    public static void SynchronizeEnergyCost(CardModel card, IReadOnlyDictionary<string, bool> overrides, int? modifiedCost)
    {
        bool enabled = overrides.TryGetValue(LoadoutKeywords.XCostKey, out bool requested)
            ? requested
            : LoadoutKeywords.Has(card, LoadoutKeywords.XCost);

        CardModel? canonical = ModelDb.AllCards.FirstOrDefault(candidate => candidate.Id.Equals(card.Id));
        bool canonicalCostsX = canonical?.EnergyCost.CostsX ?? false;
        bool explicitlyDisabled = overrides.TryGetValue(LoadoutKeywords.XCostKey, out requested) && !requested;
        bool shouldCostX = enabled || (canonicalCostsX && !explicitlyDisabled);

        if (card.EnergyCost.CostsX == shouldCostX)
        {
            if (!shouldCostX && modifiedCost.HasValue)
                card.EnergyCost.SetCustomBaseCost(modifiedCost.Value);
            return;
        }

        if (EnergyCostField is null)
            throw new MissingFieldException(typeof(CardModel).FullName, "_energyCost");

        int normalCost = modifiedCost
                         ?? (canonicalCostsX ? 0 : canonical?.EnergyCost.Canonical)
                         ?? card.EnergyCost.Canonical;
        EnergyCostField.SetValue(card, new CardEnergyCost(card, shouldCostX ? 0 : normalCost, shouldCostX));
        card.InvokeEnergyCostChanged();
    }
}

[HarmonyPatch]
public static class InfiniteUpgradeMaxLevelPatch
{
    [ThreadStatic]
    private static int _deserializingMaxLevel;

    public static void BeginDeserialization(int maxLevel)
    {
        _deserializingMaxLevel = Math.Max(_deserializingMaxLevel, maxLevel);
    }

    public static void EndDeserialization()
    {
        _deserializingMaxLevel = 0;
    }

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(CardModel).Assembly
            .GetTypes()
            .Where(type => typeof(CardModel).IsAssignableFrom(type))
            .Select(type => type.GetProperty(
                nameof(CardModel.MaxUpgradeLevel),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?.GetMethod)
            .Where(method => method is not null)
            .Distinct()!;
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref int __result)
    {
        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.InfiniteUpgrade))
        {
            __result = int.MaxValue;
            return;
        }

        __result = Math.Max(__result, Math.Max(__instance.CurrentUpgradeLevel, _deserializingMaxLevel));
    }
}

public readonly struct InfiniteUpgradeContextState
{
    public InfiniteUpgradeContextState(CardModel? activeCard, bool isApplyingNativeUpgrade)
    {
        ActiveCard = activeCard;
        IsApplyingNativeUpgrade = isApplyingNativeUpgrade;
    }

    public CardModel? ActiveCard { get; }
    public bool IsApplyingNativeUpgrade { get; }
}

[HarmonyPatch(typeof(CardModel), "UpgradeInternal")]
public static class InfiniteUpgradeContextPatch
{
    [ThreadStatic]
    internal static CardModel? ActiveCard;

    [ThreadStatic]
    internal static bool IsApplyingNativeUpgrade;

    [HarmonyPrefix]
    public static void Prefix(CardModel __instance, out InfiniteUpgradeContextState __state)
    {
        __state = new InfiniteUpgradeContextState(ActiveCard, IsApplyingNativeUpgrade);
        ActiveCard = LoadoutKeywords.Has(__instance, LoadoutKeywords.InfiniteUpgrade)
            ? __instance
            : null;
        IsApplyingNativeUpgrade = ActiveCard is not null;
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(InfiniteUpgradeContextState __state, Exception? __exception)
    {
        ActiveCard = __state.ActiveCard;
        IsApplyingNativeUpgrade = __state.IsApplyingNativeUpgrade;
        return __exception;
    }
}

[HarmonyPatch(typeof(DynamicVarSet), nameof(DynamicVarSet.RecalculateForUpgradeOrEnchant))]
public static class InfiniteUpgradeRecalculationBoundaryPatch
{
    [HarmonyPrefix]
    public static void Prefix(DynamicVarSet __instance)
    {
        CardModel? activeCard = InfiniteUpgradeContextPatch.ActiveCard;
        if (InfiniteUpgradeContextPatch.IsApplyingNativeUpgrade
            && activeCard is not null
            && ReferenceEquals(activeCard.DynamicVars, __instance))
        {
            // UpgradeInternal has finished OnUpgrade at this point. Do not scale
            // recalculation, enchantment, or Upgraded-event mutations.
            InfiniteUpgradeContextPatch.IsApplyingNativeUpgrade = false;
        }
    }
}

[HarmonyPatch(typeof(DynamicVar), nameof(DynamicVar.UpgradeValueBy))]
public static class InfiniteUpgradeDynamicValuePatch
{
    [HarmonyPrefix]
    public static void Prefix(DynamicVar __instance, ref decimal addend)
    {
        if (!InfiniteUpgradeContextPatch.IsApplyingNativeUpgrade)
            return;

        CardModel? card = InfiniteUpgradeContextPatch.ActiveCard;
        if (card is null
            || !card.DynamicVars.Any(pair => ReferenceEquals(pair.Value, __instance)))
        {
            return;
        }

        // UpgradeInternal increments CurrentUpgradeLevel before OnUpgrade.
        // +1: native amount; +2: native + 1; +3: native + 2; etc.
        int extraValue = card.CurrentUpgradeLevel - 1;
        if (extraValue > 0)
            addend += extraValue;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable), typeof(SerializableCard))]
public static class InfiniteUpgradeDeserializationPatch
{
    [HarmonyPrefix]
    public static void Prefix(SerializableCard save)
    {
        InfiniteUpgradeMaxLevelPatch.BeginDeserialization(save.CurrentUpgradeLevel);
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception)
    {
        InfiniteUpgradeMaxLevelPatch.EndDeserialization();
        return __exception;
    }
}

[HarmonyPatch]
public static class XCostPlayCountPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(CardModel),
            "GeneratePlayCount",
            [typeof(MegaCrit.Sts2.Core.Combat.ICombatState), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)]);
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref Task<int> __result)
    {
        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.XCost))
            __result = MultiplyByXAsync(__instance, __result);
    }

    private static async Task<int> MultiplyByXAsync(CardModel card, Task<int> original)
    {
        int nativePlayCount = await original;
        int x = Math.Max(0, card.ResolveEnergyXValue());
        return checked(nativePlayCount * x);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayResultPileTypeAndPosition))]
public static class StickyPlayedCardResultPilePatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, ref ValueTuple<PileType, CardPilePosition> __result)
    {
        if (LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
            __result = (PileType.Hand, CardPilePosition.Bottom);
    }
}

[HarmonyPatch(
    typeof(CardCmd),
    nameof(CardCmd.DiscardAndDraw),
    typeof(PlayerChoiceContext),
    typeof(IEnumerable<CardModel>),
    typeof(int))]
public static class StickyDiscardPatch
{
    [HarmonyPrefix]
    public static void Prefix(
        ref IEnumerable<CardModel> cardsToDiscard,
        out List<CardModel>? __state)
    {
        // Do not remove Sticky cards from the native discard operation.
        // The game must see them so that discard history, hooks, and Sly work.
        IReadOnlyList<CardModel> cards;

        if (cardsToDiscard is IReadOnlyList<CardModel> readOnlyList)
        {
            cards = readOnlyList;
        }
        else
        {
            List<CardModel> materialized = cardsToDiscard.ToList();
            cardsToDiscard = materialized;
            cards = materialized;
        }

        __state = null;

        for (int i = 0; i < cards.Count; i++)
        {
            CardModel card = cards[i];

            if (!LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
                continue;

            (__state ??= new List<CardModel>(1)).Add(card);
        }
    }

    [HarmonyPostfix]
    public static void Postfix(
        ref Task __result,
        List<CardModel>? __state)
    {
        if (__state is not { Count: > 0 })
            return;

        __result = ReturnStickyCardsAfterDiscard(__result, __state);
    }

    private static async Task ReturnStickyCardsAfterDiscard(
        Task originalDiscard,
        IReadOnlyList<CardModel> stickyCards)
    {
        // Wait for the entire native discard sequence:
        await originalDiscard;

        List<CardModel>? cardsToReturn = null;

        for (int i = 0; i < stickyCards.Count; i++)
        {
            CardModel card = stickyCards[i];

            // A Sticky + Sly card will normally already be back in hand
            // because Sly auto-plays it and Sticky changes its result pile.
            //
            // Only return cards that remain in the discard pile.
            if (card.Pile?.Type != PileType.Discard)
                continue;

            (cardsToReturn ??= new List<CardModel>(stickyCards.Count))
                .Add(card);
        }

        if (cardsToReturn is null)
            return;

        await CardPileCmd.Add(
            cardsToReturn,
            PileType.Hand,
            CardPilePosition.Bottom);
    }
}


[HarmonyPatch]
public static class StickyFlushPlayerHandPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
                   typeof(CombatManager),
                   "FlushPlayerHand",
                   [
                       typeof(Player),
                       typeof(HookPlayerChoiceContext)
                   ])
               ?? throw new MissingMethodException(
                   typeof(CombatManager).FullName,
                   "FlushPlayerHand(Player, HookPlayerChoiceContext)");
    }

    [HarmonyPrefix]
    public static void Prefix(
        Player player,
        out List<CardModel> __state)
    {
        __state = PileType.Hand
            .GetPile(player)
            .Cards
            .Where(card =>
                LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
            .ToList();
    }

    [HarmonyPostfix]
    public static void Postfix(
        ref Task __result,
        List<CardModel> __state)
    {
        if (__state.Count == 0)
            return;
        __result = ReturnStickyCardsAfterFlush(__result, __state);
    }

    private static async Task ReturnStickyCardsAfterFlush(
        Task originalFlush,
        IReadOnlyList<CardModel> stickyCards)
    {
        await originalFlush;

        List<CardModel> cardsToReturn = stickyCards
            .Where(card => card.Pile?.Type == PileType.Discard)
            .ToList();

        if (cardsToReturn.Count == 0)
            return;
        
        await CardPileCmd.Add(
            cardsToReturn,
            PileType.Hand,
            CardPilePosition.Bottom);
    }
}

[HarmonyPatch(
    typeof(CardCmd),
    nameof(CardCmd.Exhaust),
    typeof(PlayerChoiceContext),
    typeof(CardModel),
    typeof(bool),
    typeof(bool))]
public static class InevitableExhaustPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        CardModel card,
        ref Task __result)
    {
        if (!LoadoutKeywords.Has(card, LoadoutKeywords.Inevitable))
            return;

        __result = AddCopyToHandAfterExhaust(__result, card);
    }

    private static async Task AddCopyToHandAfterExhaust(
        Task originalExhaust,
        CardModel exhaustedCard)
    {
        
        await originalExhaust;

        // Do not produce a copy if another exhaust hook already moved or
        // removed the original card.
        if (exhaustedCard.Pile?.Type != PileType.Exhaust)
            return;
        
        CardModel copy = exhaustedCard.CreateClone();

        await CardPileCmd.AddGeneratedCardToCombat(
            copy,
            PileType.Hand,
            exhaustedCard.Owner,
            CardPilePosition.Bottom);
    }
}

[HarmonyPatch(
    typeof(CardCmd),
    nameof(CardCmd.Transform),
    typeof(IEnumerable<CardTransformation>),
    typeof(Rng),
    typeof(CardPreviewStyle))]
public static class InevitableTransformPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref IEnumerable<CardTransformation> transformations)
    {
        List<CardTransformation> rewritten = [];
        foreach (CardTransformation transformation in transformations)
        {
            if (!LoadoutKeywords.Has(transformation.Original, LoadoutKeywords.Inevitable))
            {
                rewritten.Add(transformation);
                continue;
            }

            if (transformation.Replacement is { IsCanonical: false } discardedReplacement)
                discardedReplacement.CardScope!.RemoveCard(discardedReplacement);

            CardModel replacement = transformation.Original.CardScope!.CloneCard(transformation.Original);
            rewritten.Add(new CardTransformation(transformation.Original, replacement));
        }

        transformations = rewritten;
    }
}
