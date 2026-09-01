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

    [ThreadStatic]
    private static bool? _deserializingUpgradedInfiniteUpgradeValues;

    [ThreadStatic]
    private static bool? _deserializingJokeInfiniteUpgradeValues;

    [ThreadStatic]
    private static bool? _deserializingUpgradedJokeInfiniteUpgradeValues;

    public static InfiniteUpgradeDeserializationState BeginDeserialization(
        int maxLevel,
        bool? useInfiniteUpgradeValues = null,
        bool? useUpgradedInfiniteUpgradeValues = null,
        bool? useJokeInfiniteUpgradeValues = null,
        bool? useUpgradedJokeInfiniteUpgradeValues = null)
    {
        InfiniteUpgradeDeserializationState previous = new(
            _deserializingMaxLevel,
            _deserializingInfiniteUpgradeValues,
            _deserializingUpgradedInfiniteUpgradeValues,
            _deserializingJokeInfiniteUpgradeValues,
            _deserializingUpgradedJokeInfiniteUpgradeValues);
        _deserializingMaxLevel = Math.Max(_deserializingMaxLevel, maxLevel);
        if (useInfiniteUpgradeValues.HasValue)
            _deserializingInfiniteUpgradeValues = useInfiniteUpgradeValues;
        if (useUpgradedInfiniteUpgradeValues.HasValue)
            _deserializingUpgradedInfiniteUpgradeValues =
                useUpgradedInfiniteUpgradeValues;
        if (useJokeInfiniteUpgradeValues.HasValue)
            _deserializingJokeInfiniteUpgradeValues =
                useJokeInfiniteUpgradeValues;
        if (useUpgradedJokeInfiniteUpgradeValues.HasValue)
            _deserializingUpgradedJokeInfiniteUpgradeValues =
                useUpgradedJokeInfiniteUpgradeValues;
        return previous;
    }

    public static void EndDeserialization(InfiniteUpgradeDeserializationState previous)
    {
        _deserializingMaxLevel = previous.MaxLevel;
        _deserializingInfiniteUpgradeValues = previous.UseInfiniteUpgradeValues;
        _deserializingUpgradedInfiniteUpgradeValues =
            previous.UseUpgradedInfiniteUpgradeValues;
        _deserializingJokeInfiniteUpgradeValues =
            previous.UseJokeInfiniteUpgradeValues;
        _deserializingUpgradedJokeInfiniteUpgradeValues =
            previous.UseUpgradedJokeInfiniteUpgradeValues;
    }

    public static InfiniteUpgradeScalingMode ResolveScalingMode(CardModel card)
    {
        bool useJokeInfiniteUpgrade = ResolveKeywordState(
            card,
            LoadoutKeywords.JokeInfiniteUpgrade,
            _deserializingJokeInfiniteUpgradeValues,
            _deserializingUpgradedJokeInfiniteUpgradeValues);
        if (useJokeInfiniteUpgrade)
            return InfiniteUpgradeScalingMode.Double;

        bool useInfiniteUpgrade = ResolveKeywordState(
            card,
            LoadoutKeywords.InfiniteUpgrade,
            _deserializingInfiniteUpgradeValues,
            _deserializingUpgradedInfiniteUpgradeValues);
        return useInfiniteUpgrade
            ? InfiniteUpgradeScalingMode.Incremental
            : InfiniteUpgradeScalingMode.None;
    }

    private static bool ResolveKeywordState(
        CardModel card,
        CardKeyword keyword,
        bool? baseOverride,
        bool? upgradedOverride) =>
        card.CurrentUpgradeLevel > 0 && upgradedOverride.HasValue
            ? upgradedOverride.Value
            : baseOverride ?? LoadoutKeywords.Has(card, keyword);

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
        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.InfiniteUpgrade)
            || LoadoutKeywords.Has(
                __instance,
                LoadoutKeywords.JokeInfiniteUpgrade))
        {
            __result = int.MaxValue;
            return;
        }

        __result = Math.Max(__result, Math.Max(__instance.CurrentUpgradeLevel, _deserializingMaxLevel));
    }
}

public readonly record struct InfiniteUpgradeDeserializationState(
    int MaxLevel,
    bool? UseInfiniteUpgradeValues,
    bool? UseUpgradedInfiniteUpgradeValues,
    bool? UseJokeInfiniteUpgradeValues,
    bool? UseUpgradedJokeInfiniteUpgradeValues);

public enum InfiniteUpgradeScalingMode
{
    None,
    Incremental,
    Double
}

public static class InfiniteUpgradeValueScaling
{
    public static InfiniteUpgradeScalingMode Resolve(CardModel card) =>
        InfiniteUpgradeMaxLevelPatch.ResolveScalingMode(card);

    public static bool AppliesTo(CardModel card) =>
        Resolve(card) != InfiniteUpgradeScalingMode.None;

    public static int GetCurrentUpgradeBonus(int currentUpgradeLevel) =>
        currentUpgradeLevel > 1
            ? currentUpgradeLevel - 1
            : 0;

    public static long GetCumulativeUpgradeBonus(int currentUpgradeLevel)
    {
        long level = Math.Max(0L, currentUpgradeLevel);
        return level * (level - 1L) / 2L;
    }
}

public readonly struct InfiniteUpgradeContextState
{
    public InfiniteUpgradeContextState(
        CardModel? activeCard,
        bool isApplyingNativeUpgrade,
        InfiniteUpgradeScalingMode scalingMode)
    {
        ActiveCard = activeCard;
        IsApplyingNativeUpgrade = isApplyingNativeUpgrade;
        ScalingMode = scalingMode;
    }

    public CardModel? ActiveCard { get; }
    public bool IsApplyingNativeUpgrade { get; }
    public InfiniteUpgradeScalingMode ScalingMode { get; }
}

public static class InfiniteUpgradeContextPatch
{
    [ThreadStatic]
    internal static CardModel? ActiveCard;

    [ThreadStatic]
    internal static bool IsApplyingNativeUpgrade;

    [ThreadStatic]
    internal static InfiniteUpgradeScalingMode ScalingMode;

    [HarmonyPrefix]
    public static void Prefix(CardModel __instance, out InfiniteUpgradeContextState __state)
    {
        __state = new InfiniteUpgradeContextState(
            ActiveCard,
            IsApplyingNativeUpgrade,
            ScalingMode);
        ScalingMode = InfiniteUpgradeValueScaling.Resolve(__instance);
        ActiveCard = ScalingMode != InfiniteUpgradeScalingMode.None
            ? __instance
            : null;
        IsApplyingNativeUpgrade = ActiveCard is not null;
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(InfiniteUpgradeContextState __state, Exception? __exception)
    {
        ActiveCard = __state.ActiveCard;
        IsApplyingNativeUpgrade = __state.IsApplyingNativeUpgrade;
        ScalingMode = __state.ScalingMode;
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

        if (InfiniteUpgradeContextPatch.ScalingMode
            == InfiniteUpgradeScalingMode.Double)
        {
            addend = __instance.BaseValue;
            return;
        }

        // UpgradeInternal increments CurrentUpgradeLevel before OnUpgrade.
        int extraValue = InfiniteUpgradeValueScaling.GetCurrentUpgradeBonus(
            card.CurrentUpgradeLevel);
        if (extraValue > 0)
            addend += extraValue;
    }
}
