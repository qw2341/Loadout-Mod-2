#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Morphing;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

[HarmonyPatch(typeof(VitalSparkPower), nameof(VitalSparkPower.AfterCardPlayed))]
public static class VitalSparkPowerPatch
{
    private static readonly Action<PowerModel> FlashPower =
        AccessTools.MethodDelegate<Action<PowerModel>>(
            AccessTools.DeclaredMethod(typeof(PowerModel), "Flash")
            ?? throw new MissingMethodException(typeof(PowerModel).FullName, "Flash"));

    [HarmonyPrefix]
    public static bool Prefix(
        VitalSparkPower __instance,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer
            || !BottledMonsterMorphService.IsPlayerMorphedAs<InfestedPrism>(
                __instance.Owner.Player))
        {
            return true;
        }

        __result = AfterPlayerCardPlayed(__instance, choiceContext, cardPlay);
        return false;
    }

    private static async Task AfterPlayerCardPlayed(
        VitalSparkPower power,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay)
    {
        if (cardPlay.Card.Affliction is not Tainted)
            return;

        FlashPower(power);
        await PowerCmd.Apply<TaintedPower>(
            choiceContext,
            power.CombatState.HittableEnemies,
            power.Amount,
            null,
            null);
    }
}
