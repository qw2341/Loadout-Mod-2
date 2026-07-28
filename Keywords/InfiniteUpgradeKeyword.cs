#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class InfiniteUpgradeKeyword : LoadoutKeywordModel
{
    public static InfiniteUpgradeKeyword Instance { get; } = new();

    private InfiniteUpgradeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.InfiniteUpgrade;

    public override string StorageKey => LoadoutKeywords.InfiniteUpgradeKey;

    public override string TitleLocKey => "LOADOUT-INFINITE_UPGRADE.title";
}

public static class InfiniteUpgradeMaxLevelPatch
{
    [ThreadStatic]
    private static int _deserializingMaxLevel;

    [ThreadStatic]
    private static bool? _deserializingInfiniteUpgradeValues;

    public static InfiniteUpgradeDeserializationState BeginDeserialization(
        int maxLevel,
        bool? useInfiniteUpgradeValues = null)
    {
        InfiniteUpgradeDeserializationState previous = new(
            _deserializingMaxLevel,
            _deserializingInfiniteUpgradeValues);
        _deserializingMaxLevel = Math.Max(_deserializingMaxLevel, maxLevel);
        if (useInfiniteUpgradeValues.HasValue)
            _deserializingInfiniteUpgradeValues = useInfiniteUpgradeValues;
        return previous;
    }

    public static void EndDeserialization(InfiniteUpgradeDeserializationState previous)
    {
        _deserializingMaxLevel = previous.MaxLevel;
        _deserializingInfiniteUpgradeValues = previous.UseInfiniteUpgradeValues;
    }

    public static bool? InfiniteUpgradeValuesOverride => _deserializingInfiniteUpgradeValues;

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

public readonly record struct InfiniteUpgradeDeserializationState(
    int MaxLevel,
    bool? UseInfiniteUpgradeValues);

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
        bool useInfiniteUpgradeValues = InfiniteUpgradeMaxLevelPatch.InfiniteUpgradeValuesOverride
                                        ?? LoadoutKeywords.Has(__instance, LoadoutKeywords.InfiniteUpgrade);
        ActiveCard = useInfiniteUpgradeValues
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
