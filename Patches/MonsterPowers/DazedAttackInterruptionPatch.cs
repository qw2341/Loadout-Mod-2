#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;

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
        if (attacker is null)
            return true;

        ActiveAttack active = ActiveAttacks.GetValue(
            attacker,
            static _ => new ActiveAttack());
        active.Command = __instance;

        if (!InterruptedAttacks.TryGetValue(__instance, out _))
            return true;

        InterruptedAttacks.Remove(__instance);
        active.Command = null;
        __result = Array.Empty<Creature>();
        return false;
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
}
