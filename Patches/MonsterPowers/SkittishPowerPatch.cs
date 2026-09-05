#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

[HarmonyPatch(typeof(SkittishPower))]
public static class SkittishPowerPatch
{
    private static readonly Action<SkittishPower, bool> SetHasGainedBlockThisTurn =
        AccessTools.MethodDelegate<Action<SkittishPower, bool>>(
            AccessTools.PropertySetter(
                typeof(SkittishPower),
                nameof(SkittishPower.HasGainedBlockThisTurn))
            ?? throw new MissingMethodException(
                typeof(SkittishPower).FullName,
                $"set_{nameof(SkittishPower.HasGainedBlockThisTurn)}"));

    [HarmonyPatch(nameof(SkittishPower.AfterAttack))]
    [HarmonyPrefix]
    public static bool BeforeAfterAttack(
        SkittishPower __instance,
        AttackCommand command,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer)
            return true;

        __result = ApplyAfterPlayerHit(__instance, command);
        return false;
    }

    [HarmonyPatch(nameof(SkittishPower.AfterSideTurnEnd))]
    [HarmonyPrefix]
    public static bool BeforeAfterSideTurnEnd(
        SkittishPower __instance,
        CombatSide side,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer)
            return true;

        if (side != __instance.Owner.Side)
            SetHasGainedBlockThisTurn(__instance, false);

        __result = Task.CompletedTask;
        return false;
    }

    private static Task ApplyAfterPlayerHit(
        SkittishPower power,
        AttackCommand command)
    {
        if (power.HasGainedBlockThisTurn
            || (command.DamageProps & ValueProp.Move) == 0
            || !WasHitForUnblockedDamage(power.Owner, command))
        {
            return Task.CompletedTask;
        }

        SetHasGainedBlockThisTurn(power, true);
        return CreatureCmd.GainBlock(
            power.Owner,
            power.Amount,
            ValueProp.Unpowered,
            null);
    }

    private static bool WasHitForUnblockedDamage(
        Creature owner,
        AttackCommand command)
    {
        foreach (List<DamageResult> hit in command.Results)
        {
            foreach (DamageResult result in hit)
            {
                if (result.Receiver == owner && result.UnblockedDamage != 0)
                    return true;
            }
        }

        return false;
    }
}
