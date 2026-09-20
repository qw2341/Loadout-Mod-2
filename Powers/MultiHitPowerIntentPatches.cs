#nullable enable

namespace Loadout.Powers;

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Loadout.Keywords;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.ValueProps;

public static class MultiHitPowerIntent
{
    public static bool IsActive(Creature owner) =>
        owner.IsMonster && owner.GetPower<MultiHitPower>() is { Amount: > 0 };

    public static int BaseHits(AttackIntent intent) =>
        MultiHitKeyword.GetHitCount(intent.DamageCalc?.Invoke() ?? 0m);

    public static int TotalHits(AttackIntent intent) =>
        (int)Math.Clamp((long)BaseHits(intent) * intent.Repeats, 0L, int.MaxValue);

    public static IEnumerable<MethodBase> Methods(string name)
    {
        yield return AccessTools.Method(typeof(SingleAttackIntent), name);
        yield return AccessTools.Method(typeof(MultiAttackIntent), name);
    }
}

[HarmonyPatch(typeof(AttackIntent), nameof(AttackIntent.GetSingleDamage))]
public static class MultiHitPowerIntentDamagePatch
{
    public static bool Prefix(Creature owner, ref int __result)
    {
        if (!MultiHitPowerIntent.IsActive(owner))
            return true;
        var player = LocalContext.GetMe(owner.CombatState);
        decimal damage = player is null ? 1m : Sts2Compatibility.ModifyDamage(
            player.RunState, owner.CombatState, player.Creature, owner, 1m,
            ValueProp.Move, null, null, ModifyDamageHookType.All, CardPreviewMode.None);
        __result = (int)Math.Clamp(decimal.Floor(damage), 0m, int.MaxValue);
        return false;
    }
}

[HarmonyPatch]
public static class MultiHitPowerIntentTotalPatch
{
    public static IEnumerable<MethodBase> TargetMethods() =>
        MultiHitPowerIntent.Methods(nameof(AttackIntent.GetTotalDamage));

    public static bool Prefix(AttackIntent __instance, IEnumerable<Creature> targets,
        Creature owner, ref int __result)
    {
        if (!MultiHitPowerIntent.IsActive(owner))
            return true;
        __result = (int)Math.Clamp(
            (long)__instance.GetSingleDamage(targets, owner) * MultiHitPowerIntent.TotalHits(__instance),
            0L, int.MaxValue);
        return false;
    }
}

[HarmonyPatch]
public static class MultiHitPowerIntentLabelPatch
{
    public static IEnumerable<MethodBase> TargetMethods() =>
        MultiHitPowerIntent.Methods(nameof(AttackIntent.GetIntentLabel));

    public static bool Prefix(AttackIntent __instance, IEnumerable<Creature> targets,
        Creature owner, ref LocString __result)
    {
        if (!MultiHitPowerIntent.IsActive(owner))
            return true;
        __result = new LocString("powers", __instance.Repeats == 1
            ? "LOADOUT-MULTI_HIT_POWER.intentSingle"
            : "LOADOUT-MULTI_HIT_POWER.intentMulti");
        __result.Add("Damage", __instance.GetSingleDamage(targets, owner));
        __result.Add("Hits", MultiHitPowerIntent.BaseHits(__instance));
        __result.Add("Repeat", __instance.Repeats);
        return false;
    }
}

[HarmonyPatch(typeof(AttackIntent), "GetIntentDescription")]
public static class MultiHitPowerIntentDescriptionPatch
{
    public static void Postfix(AttackIntent __instance, Creature owner, LocString __result)
    {
        if (MultiHitPowerIntent.IsActive(owner))
            __result.Add("Repeat", MultiHitPowerIntent.TotalHits(__instance));
    }
}
