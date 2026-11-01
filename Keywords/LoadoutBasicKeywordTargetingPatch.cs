#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Reflection;
using BaseLib.Patches.Features;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

internal static class LoadoutBasicKeywordTargetTypePatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return LoadoutKeywordModel.GetCardPropertyGetters(
            nameof(CardModel.TargetType));
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref TargetType __result)
    {
        if (!LoadoutKeywordRegistry.ChangesTargeting(__instance))
            return;

        if (LoadoutPowerKeywordState.HasEffectiveEntries(
                __instance,
                LoadoutKeywords.ApplyToAnotherPlayerKey)
            || LoadoutKeywordRegistry.RequiresAnotherPlayerTarget(__instance))
        {
            __result = TargetType.AnyAlly;
            return;
        }

        TargetType? multiTargetType = null;
        bool requiresSingleTarget = false;
        foreach (LoadoutKeywordModel model in LoadoutKeywordRegistry.EnumerateLiveModels(
                     __instance, static model => model.ChangesTargeting))
        {
            if (model.MultiTargetType is not { } targetType)
            {
                requiresSingleTarget = true;
                break;
            }

            if (multiTargetType != TargetType.AllEnemies)
                multiTargetType = targetType;
        }

        if (!requiresSingleTarget && multiTargetType is { } multiTarget)
        {
            if (__result is TargetType.None or TargetType.Self
                or TargetType.AllEnemies or TargetType.RandomEnemy
                || LoadoutKeywordRegistry.SuppressesOriginalOnPlay(__instance))
            {
                __result = multiTarget;
            }
            return;
        }

        if (__result == TargetType.Self)
        {
            __result = CustomTargetType.Anyone;
            return;
        }

        if (__result is TargetType.AnyEnemy
            or TargetType.AnyPlayer
            or TargetType.AnyAlly
            or TargetType.Osty
            || CustomTargetType.IsCustomSingleTargetType(__result))
        {
            return;
        }

        if (__result is TargetType.None
            or TargetType.AllEnemies
            or TargetType.RandomEnemy
            or TargetType.AllAllies
            or TargetType.TargetedNoCreature
            || CustomTargetType.IsCustomMultiTargetType(__result))
        {
            __result = TargetType.AnyEnemy;
        }
    }
}
