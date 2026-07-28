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
        bool Livid,
        IReadOnlyList<KeywordEffectState> KeywordEffects);

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
    public static void Prefix(
        CardModel __instance,
        CardPlay __1,
        out object? __state)
    {
        CardPlay cardPlay = __1;
        bool livid = LoadoutKeywords.Has(__instance, LoadoutKeywords.Livid);
        List<KeywordEffectState>? effects = null;

        foreach (LoadoutKeywordModel model in LoadoutSpecialKeywords.All)
        {
            if (!model.HasOnPlayEffect || !model.IsEnabled(__instance))
                continue;

            (effects ??= []).Add(new KeywordEffectState(
                model,
                model.CaptureBeforeOnPlay(__instance, cardPlay)));
        }

        __state = !livid && effects is null
            ? null
            : new DispatchState(livid, effects ?? []);
    }

    [HarmonyPostfix]
    public static void Postfix(
        CardModel __instance,
        CardPlay __1,
        object? __state,
        ref Task __result)
    {
        if (__state is not DispatchState state)
            return;

        __result = Apply(__result, __instance, __1, state);
    }

    private static async Task Apply(
        Task originalOnPlay,
        CardModel source,
        CardPlay cardPlay,
        DispatchState state)
    {
        await originalOnPlay;

        foreach (KeywordEffectState effect in state.KeywordEffects)
        {
            await effect.Model.AfterOnPlay(
                source,
                cardPlay,
                effect.CapturedState);
        }

        if (state.Livid)
            await LividKeyword.Apply(source);
    }
}
