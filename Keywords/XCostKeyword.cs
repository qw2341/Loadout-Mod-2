#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class XCostKeyword : LoadoutKeywordModel
{
    public static XCostKeyword Instance { get; } = new();

    private XCostKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.XCost;

    public override string StorageKey => LoadoutKeywords.XCostKey;

    public override string TitleLocKey => "LOADOUT-X_COST.title";
}

public static class XCostKeywordMechanics
{
    private static readonly FieldInfo? EnergyCostField =
        AccessTools.Field(typeof(CardModel), "_energyCost");

    public static void SynchronizeEnergyCost(
        CardModel card,
        IReadOnlyDictionary<string, bool> overrides,
        int? modifiedCost)
    {
        bool enabled = overrides.TryGetValue(LoadoutKeywords.XCostKey, out bool requested)
            ? requested
            : LoadoutKeywords.Has(card, LoadoutKeywords.XCost);

        CardModel? canonical =
            ModelDb.AllCards.FirstOrDefault(candidate => candidate.Id.Equals(card.Id));
        bool canonicalCostsX = canonical?.EnergyCost.CostsX ?? false;
        bool explicitlyDisabled =
            overrides.TryGetValue(LoadoutKeywords.XCostKey, out requested) && !requested;
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
        EnergyCostField.SetValue(
            card,
            new CardEnergyCost(card, shouldCostX ? 0 : normalCost, shouldCostX));
        card.InvokeEnergyCostChanged();
    }
}

public static class XCostOnPlayPatch
{
    private sealed record RepeatScope(
        CardModel Card,
        CardPlay CardPlay,
        RepeatScope? Parent);

    private static readonly AsyncLocal<RepeatScope?> CurrentRepeat = new();
    private static readonly Dictionary<MethodBase, FastInvokeHandler> OnPlayInvokers = [];

    internal static bool IsRepeating(CardModel card, CardPlay cardPlay)
    {
        for (RepeatScope? scope = CurrentRepeat.Value;
             scope is not null;
             scope = scope.Parent)
        {
            if (ReferenceEquals(scope.Card, card)
                && ReferenceEquals(scope.CardPlay, cardPlay))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodBase target in PostOnPlayKeywordDispatcher.TargetMethods())
        {
            if (target is not MethodInfo method)
                continue;

            OnPlayInvokers[target] = HarmonyLib.MethodInvoker.GetHandler(method);
            yield return target;
        }
    }

    [HarmonyPrefix]
    public static bool Prefix(
        CardModel __instance,
        CardPlay __1,
        ref Task __result,
        out int __state)
    {
        __state = 0;
        if (IsRepeating(__instance, __1)
            || !LoadoutKeywords.Has(__instance, LoadoutKeywords.XCost))
        {
            return true;
        }

        int executionCount = Math.Max(0, __instance.ResolveEnergyXValue());
        if (executionCount == 0)
        {
            __result = Task.CompletedTask;
            return false;
        }

        __state = executionCount - 1;
        return true;
    }

    [HarmonyPostfix]
    public static void Postfix(
        CardModel __instance,
        PlayerChoiceContext __0,
        CardPlay __1,
        MethodBase __originalMethod,
        int __state,
        ref Task __result)
    {
        if (__state <= 0)
            return;

        if (!OnPlayInvokers.TryGetValue(
                __originalMethod,
                out FastInvokeHandler? invokeOnPlay))
        {
            throw new MissingMethodException(
                __originalMethod.DeclaringType?.FullName,
                __originalMethod.Name);
        }

        __result = RepeatOnPlay(
            __result,
            invokeOnPlay,
            __instance,
            __0,
            __1,
            __state);
    }

    private static async Task RepeatOnPlay(
        Task originalOnPlay,
        FastInvokeHandler invokeOnPlay,
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        int additionalExecutions)
    {
        await originalOnPlay;

        RepeatScope? previousRepeat = CurrentRepeat.Value;
        CurrentRepeat.Value = new RepeatScope(card, cardPlay, previousRepeat);
        try
        {
            object[] arguments = [choiceContext, cardPlay];
            for (int i = 0; i < additionalExecutions; i++)
            {
                if (invokeOnPlay(card, arguments) is not Task repeatedOnPlay)
                {
                    throw new InvalidOperationException(
                        $"{card.GetType().FullName}.OnPlay did not return a Task.");
                }

                await repeatedOnPlay;
            }
        }
        finally
        {
            CurrentRepeat.Value = previousRepeat;
        }
    }
}
