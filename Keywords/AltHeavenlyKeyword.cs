#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

public sealed class AltHeavenlyKeyword : LoadoutKeywordModel
{
    public const string EnergyVar = "LoadoutAltHeavenlyEnergy";
    public const int FastAnimationMinimumResult = 100;

    private const int LargestExactFactorialInput = 12;

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                EnergyVar,
                4m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_ALT_HEAVENLY_ENERGY")
        ];

    public static AltHeavenlyKeyword Instance { get; } = new();

    private AltHeavenlyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AltHeavenly;

    public override string StorageKey => LoadoutKeywords.AltHeavenlyKey;

    public override string TitleLocKey => "LOADOUT-ALT_HEAVENLY.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey =>
        "LOADOUT-ALT_HEAVENLY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public static int Factorialize(int value)
    {
        if (value < 0)
            return value;
        if (value > LargestExactFactorialInput)
            return int.MaxValue;

        int result = 1;
        for (int factor = 2; factor <= value; factor++)
            result *= factor;

        return result;
    }
}

internal static class AltHeavenlyAnimationScope
{
    internal sealed class Scope(
        CardModel card,
        Scope? previous)
    {
        public CardModel Card { get; } = card;

        public Scope? Previous { get; } = previous;

        public bool UseInstantMode { get; set; }
    }

    private static readonly AsyncLocal<Scope?> Current = new();

    public static bool ShouldUseInstantMode
    {
        get
        {
            for (Scope? scope = Current.Value;
                 scope is not null;
                 scope = scope.Previous)
            {
                if (scope.UseInstantMode)
                    return true;
            }

            return false;
        }
    }

    public static Scope? Enter(CardModel card)
    {
        if (!card.EnergyCost.CostsX
            || !LoadoutKeywords.Has(card, LoadoutKeywords.AltHeavenly))
        {
            return null;
        }

        Scope scope = new(card, Current.Value);
        Current.Value = scope;
        return scope;
    }

    public static void MarkFactorialResult(CardModel card, int result)
    {
        if (result < AltHeavenlyKeyword.FastAnimationMinimumResult)
            return;

        for (Scope? scope = Current.Value;
             scope is not null;
             scope = scope.Previous)
        {
            if (!ReferenceEquals(scope.Card, card))
                continue;

            scope.UseInstantMode = true;
            return;
        }
    }

    public static void Exit(Scope? scope)
    {
        if (scope is not null && ReferenceEquals(Current.Value, scope))
            Current.Value = scope.Previous;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
internal static class AltHeavenlyOnPlayAnimationScopePatch
{
    [HarmonyPrefix]
    private static void Prefix(
        CardModel __instance,
        out AltHeavenlyAnimationScope.Scope? __state)
    {
        __state = AltHeavenlyAnimationScope.Enter(__instance);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        AltHeavenlyAnimationScope.Scope? __state)
    {
        AltHeavenlyAnimationScope.Exit(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(PrefsSave), nameof(PrefsSave.FastMode), MethodType.Getter)]
internal static class AltHeavenlyFastModePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref FastModeType __result)
    {
        if (AltHeavenlyAnimationScope.ShouldUseInstantMode)
            __result = FastModeType.Instant;
    }
}
