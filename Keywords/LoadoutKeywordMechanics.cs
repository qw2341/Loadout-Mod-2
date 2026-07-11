#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
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
            __result = int.MaxValue;
        else
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
    public static void Prefix(ref IEnumerable<CardModel> cardsToDiscard)
    {
        cardsToDiscard = cardsToDiscard
            .Where(card => !LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
            .ToList();
    }
}

[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    typeof(CardModel),
    typeof(CardPile),
    typeof(CardPilePosition),
    typeof(AbstractModel),
    typeof(bool))]
public static class StickyHandMovementPatch
{
    [HarmonyPrefix]
    public static void Prefix(CardModel card, ref CardPile newPile)
    {
        if (card.Pile?.Type == PileType.Hand
            && newPile.Type is not PileType.Hand and not PileType.Play and not PileType.Exhaust
            && LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
        {
            newPile = PileType.Hand.GetPile(card.Owner);
        }
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
    public static void Postfix(CardModel card, ref Task __result)
    {
        if (LoadoutKeywords.Has(card, LoadoutKeywords.Inevitable))
            __result = ReturnToHandAsync(card, __result);
    }

    private static async Task ReturnToHandAsync(CardModel card, Task original)
    {
        await original;
        if (card.Pile?.Type == PileType.Exhaust)
            await CardPileCmd.Add(card, PileType.Hand);
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
