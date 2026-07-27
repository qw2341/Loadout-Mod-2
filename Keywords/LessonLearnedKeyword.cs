#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

internal static class LessonLearnedKeyword
{
    internal const string CardsVar = "LoadoutLessonLearnedCards";

    internal static LoadoutSpecialKeywordDefinition CreateDefinition()
    {
        return new LoadoutSpecialKeywordDefinition(
            LoadoutKeywords.LessonLearned,
            LoadoutKeywords.LessonLearnedKey,
            LoadoutKeywordPresentation.DescriptionLine,
            LoadoutKeywordTextPosition.After,
            "LOADOUT-LESSON_LEARNED.title",
            "LOADOUT-LESSON_LEARNED.cardText",
            [
                new LoadoutKeywordDynamicVarDefinition(
                    CardsVar,
                    1m,
                    0,
                    int.MaxValue,
                    "DYNAMIC_VAR_LOADOUT_LESSON_LEARNED_CARDS")
            ]);
    }

    internal static MethodInfo GetDescriptionTarget()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo? method = typeof(CardModel)
            .GetMethods(flags)
            .SingleOrDefault(candidate =>
            {
                if (!string.Equals(
                        candidate.Name,
                        nameof(CardModel.GetDescriptionForPile),
                        StringComparison.Ordinal)
                    || candidate.ReturnType != typeof(string))
                {
                    return false;
                }

                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 3
                       && parameters[0].ParameterType == typeof(PileType)
                       && parameters[2].ParameterType == typeof(Creature);
            });

        return method ?? throw new MissingMethodException(
            typeof(CardModel).FullName,
            "private GetDescriptionForPile(PileType, DescriptionPreviewType, Creature)");
    }

    internal static void Apply(CardModel source)
    {
        if (!LoadoutSpecialKeywords.TryGetValue(
                source,
                CardsVar,
                out DynamicVar countVar))
        {
            return;
        }

        int count = Math.Max(0, countVar.IntValue);
        for (int upgradeIndex = 0; upgradeIndex < count; upgradeIndex++)
        {
            List<CardModel> candidates = PileType.Deck
                .GetPile(source.Owner)
                .Cards
                .Where(card => card.IsUpgradable)
                .ToList();
            CardModel? selected =
                source.Owner.RunState.Rng.CombatCardSelection.NextItem(candidates);
            if (selected is null)
                break;

            int previousUpgradeLevel = selected.CurrentUpgradeLevel;
            CardCmd.Upgrade(selected, CardPreviewStyle.HorizontalLayout);
            if (selected.CurrentUpgradeLevel <= previousUpgradeLevel
                || !LocalContext.IsMine(selected))
            {
                continue;
            }

            // CardCmd.Upgrade supplies the native NCardUpgradeVfx. The base
            // game's card-smith sound is presentation-only, so only the owning
            // local peer plays it.
            NDebugAudioManager.Instance?.Play(
                TmpSfx.cardSmith,
                1f,
                PitchVariance.Small);
        }
    }
}

public static class LessonLearnedDescriptionPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref string __result)
    {
        __result = LoadoutSpecialKeywords.AddDescriptionLines(__instance, __result);
    }
}

public static class LessonLearnedHoverTipsPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        __result = LoadoutSpecialKeywords.RemoveDescriptionKeywordHoverTips(
            __instance,
            __result);
    }
}

public static class LessonLearnedFatalPatch
{
    private sealed record FatalSnapshot(Creature Target);

    private static readonly ConditionalWeakTable<CardPlay, FatalSnapshot> Snapshots = new();

    [HarmonyPostfix]
    public static void Postfix(CardPlay __1, ref Task __result)
    {
        CardPlay cardPlay = __1;
        if (!LoadoutKeywords.Has(cardPlay.Card, LoadoutKeywords.LessonLearned))
            return;

        __result = CaptureAfterBeforeCardPlayed(__result, cardPlay);
    }

    public static bool ConsumeIfTriggered(CardPlay cardPlay)
    {
        if (!Snapshots.TryGetValue(cardPlay, out FatalSnapshot? snapshot))
            return false;

        Snapshots.Remove(cardPlay);
        return snapshot.Target.IsDead;
    }

    public static void Clear(CardPlay cardPlay)
    {
        Snapshots.Remove(cardPlay);
    }

    private static async Task CaptureAfterBeforeCardPlayed(Task original, CardPlay cardPlay)
    {
        await original;

        Creature? target = cardPlay.Target;
        if (target is null
            || target.IsDead
            || target.Powers.Any(power => !power.ShouldOwnerDeathTriggerFatal()))
        {
            return;
        }

        Snapshots.Remove(cardPlay);
        Snapshots.Add(cardPlay, new FatalSnapshot(target));
    }
}
