#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

internal static class PostOnPlayKeywordDispatcher
{
    private sealed record KeywordEffectState(
        LoadoutKeywordModel Model,
        object? CapturedState);

    private sealed record DispatchState(
        IReadOnlyList<KeywordEffectState> KeywordEffects,
        int ExecutionCount);

    internal static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] parameterTypes = [typeof(PlayerChoiceContext), typeof(CardPlay)];
        HashSet<MethodBase> targets = [];

        foreach (Type type in ModelDb.AllCards
                     .Select(card => card.GetType())
                     .Append(typeof(CardModel))
                     .Distinct())
        {
            MethodInfo? onPlay = type.GetMethod(
                "OnPlay",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly,
                binder: null,
                parameterTypes,
                modifiers: null);
            if (onPlay is not null && !onPlay.IsStatic && onPlay.ReturnType == typeof(Task))
                targets.Add(onPlay);
        }

        return targets;
    }

    [HarmonyPrefix]
    public static bool Prefix(
        CardModel __instance,
        CardPlay __1,
        ref Task __result,
        out object? __state)
    {
        bool suppressOriginal =
            LoadoutKeywordRegistry.SuppressesOriginalOnPlay(__instance);
        if (XCostOnPlayPatch.IsRepeating(__instance, __1))
        {
            __state = null;
            if (!suppressOriginal)
                return true;

            __result = Task.CompletedTask;
            return false;
        }

        CardPlay cardPlay = __1;
        List<KeywordEffectState>? effects = null;
        int executionCount = suppressOriginal
                             && LoadoutKeywords.Has(
                                 __instance,
                                 LoadoutKeywords.XCost)
            ? XCostOnPlayPatch.ResolveExecutionCount(__instance)
            : 1;

        if (executionCount > 0)
        {
            foreach (LoadoutKeywordModel model in
                     LoadoutKeywordRegistry.WithPostOnPlayEffect)
            {
                if (!model.HasOnPlayEffect || !model.IsEnabled(__instance))
                    continue;

                (effects ??= []).Add(new KeywordEffectState(
                    model,
                    model.CaptureBeforeOnPlay(__instance, cardPlay)));
            }
        }

        __state = effects is null
            ? null
            : new DispatchState(effects, executionCount);

        if (!suppressOriginal)
            return true;

        __result = Task.CompletedTask;
        return false;
    }

    [HarmonyPostfix]
    public static void Postfix(
        CardModel __instance,
        PlayerChoiceContext __0,
        CardPlay __1,
        object? __state,
        ref Task __result)
    {
        if (__state is not DispatchState state)
            return;

        __result = Apply(__result, __instance, __0, __1, state);
    }

    private static async Task Apply(
        Task originalOnPlay,
        CardModel source,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        DispatchState state)
    {
        await originalOnPlay;

        for (int execution = 0; execution < state.ExecutionCount; execution++)
        {
            foreach (KeywordEffectState effect in state.KeywordEffects)
            {
                await effect.Model.AfterOnPlay(
                    source,
                    choiceContext,
                    cardPlay,
                    effect.CapturedState);
            }
        }
    }
}
