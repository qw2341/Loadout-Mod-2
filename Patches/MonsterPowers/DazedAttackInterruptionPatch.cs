#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

[HarmonyPatch(typeof(AttackCommand), "GetPossibleTargets")]
public static class DazedAttackInterruptionPatch
{
    private sealed class ActiveAttack
    {
        public AttackCommand? Command { get; set; }
    }

    private sealed class InterruptMarker;

    private static readonly ConditionalWeakTable<Creature, ActiveAttack>
        ActiveAttacks = new();

    private static readonly ConditionalWeakTable<AttackCommand, InterruptMarker>
        InterruptedAttacks = new();

    [HarmonyPrefix]
    public static bool Prefix(
        AttackCommand __instance,
        ref IReadOnlyList<Creature> __result)
    {
        Creature? attacker = __instance.Attacker;
        if (attacker?.IsMonster != true
            || !InterruptedAttacks.TryGetValue(__instance, out _))
            return true;

        InterruptedAttacks.Remove(__instance);
        if (ActiveAttacks.TryGetValue(attacker, out ActiveAttack? active)
            && ReferenceEquals(active.Command, __instance))
        {
            active.Command = null;
        }

        __result = Array.Empty<Creature>();
        return false;
    }

    [HarmonyPostfix]
    public static void Postfix(
        AttackCommand __instance,
        IReadOnlyList<Creature> __result)
    {
        Creature? attacker = __instance.Attacker;
        if (attacker?.IsMonster != true)
            return;

        if (HasEntomancerPersonalHiveTarget(__result))
        {
            ActiveAttacks.GetValue(
                attacker,
                static _ => new ActiveAttack()).Command = __instance;
            return;
        }

        if (ActiveAttacks.TryGetValue(attacker, out ActiveAttack? active)
            && ReferenceEquals(active.Command, __instance))
        {
            active.Command = null;
        }
    }

    public static void InterruptCurrentAttack(Creature attacker)
    {
        if (!ActiveAttacks.TryGetValue(attacker, out ActiveAttack? active)
            || active.Command is null)
        {
            return;
        }

        InterruptedAttacks.GetValue(
            active.Command,
            static _ => new InterruptMarker());
    }

    private static bool HasEntomancerPersonalHiveTarget(
        IReadOnlyList<Creature> targets)
    {
        foreach (Creature target in targets)
        {
            if (!target.IsPlayer
                || target.IsDead
                || target.GetPower<PersonalHivePower>() is not { Amount: > 0 } power
                || !PersonalHivePowerPatch.UsesPlayerEntomancerBehavior(power))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
