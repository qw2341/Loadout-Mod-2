#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Powers;
using Loadout.Services.Morphing;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

[HarmonyPatch(typeof(PainfulStabsPower), nameof(PainfulStabsPower.AfterAttack))]
public static class PainfulStabsPowerPatch
{
    public const string PlayerDescriptionKey =
        "LOADOUT-PAINFUL_STABS_POWER.playerDescription";

    private static readonly Action<PowerModel> FlashPower =
        AccessTools.MethodDelegate<Action<PowerModel>>(
            AccessTools.DeclaredMethod(typeof(PowerModel), "Flash")
            ?? throw new MissingMethodException(typeof(PowerModel).FullName, "Flash"));

    [HarmonyPrefix]
    public static bool Prefix(
        PainfulStabsPower __instance,
        PlayerChoiceContext choiceContext,
        AttackCommand command,
        ref Task __result)
    {
        if (!UsesPlayerTestSubjectBehavior(__instance))
            return true;

        __result = ApplyWoundedAfterAttack(__instance, choiceContext, command);
        return false;
    }

    public static bool UsesPlayerTestSubjectBehavior(PowerModel power)
    {
        return power is PainfulStabsPower
               && power.IsMutable
               && power.Owner.IsPlayer
               && BottledMonsterMorphService.IsPlayerMorphedAs<TestSubject>(
                   power.Owner.Player);
    }

    private static async Task ApplyWoundedAfterAttack(
        PainfulStabsPower power,
        PlayerChoiceContext choiceContext,
        AttackCommand command)
    {
        Creature owner = power.Owner;
        if (command.Attacker != owner
            || command.TargetSide == owner.Side
            || !command.DamageProps.IsPoweredAttack())
        {
            return;
        }

        List<Creature> woundedTargets = [];
        List<int> woundedAmounts = [];
        foreach (IReadOnlyList<DamageResult> hit in command.Results)
        {
            foreach (DamageResult result in hit)
            {
                Creature target = result.Receiver;
                if (result.UnblockedDamage > 0
                    && target.IsMonster
                    && target.Side != owner.Side
                    && !target.IsDead
                    && !target.IsStunned)
                {
                    int targetIndex = woundedTargets.IndexOf(target);
                    if (targetIndex < 0)
                    {
                        woundedTargets.Add(target);
                        woundedAmounts.Add(1);
                    }
                    else
                    {
                        woundedAmounts[targetIndex]++;
                    }
                }
            }
        }

        for (int index = 0; index < woundedTargets.Count; index++)
        {
            await PowerCmd.Apply<WoundedPower>(
                choiceContext,
                woundedTargets[index],
                woundedAmounts[index],
                owner,
                command.ModelSource as CardModel);
        }

        if (woundedTargets.Count > 0)
            FlashPower(power);
    }
}

[HarmonyPatch]
public static class PainfulStabsPowerDescriptionPatch
{
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.Description), MethodType.Getter)]
    [HarmonyPostfix]
    public static void DescriptionPostfix(
        PowerModel __instance,
        ref LocString __result)
    {
        if (PainfulStabsPowerPatch.UsesPlayerTestSubjectBehavior(__instance))
        {
            __result = new LocString(
                PowerModel.locTable,
                PainfulStabsPowerPatch.PlayerDescriptionKey);
        }
    }

    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.SmartDescription), MethodType.Getter)]
    [HarmonyPostfix]
    public static void SmartDescriptionPostfix(
        PowerModel __instance,
        ref LocString __result)
    {
        if (PainfulStabsPowerPatch.UsesPlayerTestSubjectBehavior(__instance))
        {
            __result = new LocString(
                PowerModel.locTable,
                PainfulStabsPowerPatch.PlayerDescriptionKey);
        }
    }
}

[HarmonyPatch(typeof(PainfulStabsPower), "ExtraHoverTips", MethodType.Getter)]
public static class PainfulStabsPowerHoverTipsPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        PainfulStabsPower __instance,
        ref IEnumerable<IHoverTip> __result)
    {
        if (PainfulStabsPowerPatch.UsesPlayerTestSubjectBehavior(__instance))
            __result = [HoverTipFactory.FromPower<WoundedPower>()];
    }
}
