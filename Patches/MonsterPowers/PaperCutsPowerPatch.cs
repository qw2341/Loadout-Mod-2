#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

[HarmonyPatch(typeof(PaperCutsPower), nameof(PaperCutsPower.AfterDamageGiven))]
public static class PaperCutsPowerPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        PaperCutsPower __instance,
        PlayerChoiceContext choiceContext,
        Creature? dealer,
        DamageResult result,
        ValueProp props,
        Creature target,
        ref Task __result)
    {
        if (__instance.Owner.IsPlayer
            && dealer == __instance.Owner
            && target.IsMonster &&
            props.IsPoweredAttack()
            && result.UnblockedDamage > 0)
        {
            __result = ApplyToMonsterAfterNative(__result, choiceContext, target, __instance.Amount);
        }
    }

    private static async Task ApplyToMonsterAfterNative(
        Task nativeTask,
        PlayerChoiceContext choiceContext,
        Creature target,
        int amount)
    {
        await nativeTask;
        await CreatureCmd.LoseMaxHp(
            choiceContext,
            target,
            amount,
            isFromCard: false);
    }
}
