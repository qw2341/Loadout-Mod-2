#nullable enable

namespace Loadout.Powers;

using System;
using HarmonyLib;
using Loadout.Keywords;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

[HarmonyPatch(typeof(AttackCommand), nameof(AttackCommand.Execute))]
public static class MultiHitPowerAnimationPatch
{
    public static bool IsMultiHitMonsterAttack(AttackCommand attack) =>
        attack.ModelSource is not CardModel
        && attack.Attacker is { IsMonster: true } attacker
        && attacker.GetPower<MultiHitPower>() is { Amount: > 0 };

    [HarmonyPrefix]
    private static void Prefix(AttackCommand __instance,
        out CardEffectAnimationScope.Scope? __state)
    {
        __state = IsMultiHitMonsterAttack(__instance)
            ? CardEffectAnimationScope.EnterAttack(__instance)
            : null;
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception,
        CardEffectAnimationScope.Scope? __state)
    {
        CardEffectAnimationScope.Exit(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyAttackHitCount))]
public static class MultiHitPowerAnimationHitCountPatch
{
    [HarmonyPostfix]
    public static void Postfix(AttackCommand __1, decimal __result)
    {
        if (MultiHitPowerAnimationPatch.IsMultiHitMonsterAttack(__1))
            CardEffectAnimationScope.MarkRepeatedHits(__1, MultiHitKeyword.GetHitCount(__result));
    }
}
