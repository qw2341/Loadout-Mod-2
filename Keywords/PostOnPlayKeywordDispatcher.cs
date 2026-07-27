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
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] parameterTypes = [typeof(PlayerChoiceContext), typeof(CardPlay)];
        HashSet<MethodBase> targets = [];

        foreach (Type type in ModelDb.AllCards
                     .Select(card => card.GetType())
                     .Append(typeof(CardModel))
                     .Distinct())
        {
            MethodInfo? onPlay = AccessTools.Method(type, "OnPlay", parameterTypes);
            if (onPlay is not null && !onPlay.IsStatic && onPlay.ReturnType == typeof(Task))
                targets.Add(onPlay);
        }

        return targets;
    }

    [HarmonyPostfix]
    public static void Postfix(
        CardModel __instance,
        CardPlay __1,
        ref Task __result)
    {
        CardPlay cardPlay = __1;
        bool livid = LoadoutKeywords.Has(__instance, LoadoutKeywords.Livid);
        bool lessonLearned =
            LoadoutKeywords.Has(__instance, LoadoutKeywords.LessonLearned);
        if (!livid && !lessonLearned)
            return;

        __result = Apply(__result, __instance, cardPlay, livid, lessonLearned);
    }

    private static async Task Apply(
        Task originalOnPlay,
        CardModel source,
        CardPlay cardPlay,
        bool livid,
        bool lessonLearned)
    {
        try
        {
            await originalOnPlay;

            if (lessonLearned && LessonLearnedFatalPatch.ConsumeIfTriggered(cardPlay))
                LessonLearnedKeyword.Apply(source);

            if (livid)
                await LividKeyword.Apply(source);
        }
        finally
        {
            if (lessonLearned)
                LessonLearnedFatalPatch.Clear(cardPlay);
        }
    }

}
