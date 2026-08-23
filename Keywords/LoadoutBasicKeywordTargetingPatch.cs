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
        return LoadoutBasicKeywordModel.GetPropertyGetters(
            nameof(CardModel.TargetType));
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref TargetType __result)
    {
        if (!LoadoutKeywordRegistry.ChangesTargeting(__instance))
            return;

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
