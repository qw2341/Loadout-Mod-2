#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Powers;

[HarmonyPatch(
    typeof(Hook),
    nameof(Hook.AfterAttack),
    typeof(ICombatState),
    typeof(PlayerChoiceContext),
    typeof(AttackCommand))]
public static class ThieveryPowerPatch
{
    [HarmonyPostfix]
    public static void Postfix(AttackCommand command, ref Task __result)
    {
        Creature? attacker = command.Attacker;
        Player? player = attacker?.Player;
        if (attacker?.IsPlayer != true
            || player is null
            || !command.Results.Any(hit => hit.Count > 0))
        {
            return;
        }

        List<ThieveryPower> powers =
            attacker.GetPowerInstances<ThieveryPower>().ToList();
        if (powers.Count > 0)
            __result = GainGoldAfterNative(__result, player, powers);
    }

    private static async Task GainGoldAfterNative(
        Task nativeTask,
        Player player,
        IEnumerable<ThieveryPower> powers)
    {
        await nativeTask;

        foreach (ThieveryPower power in powers)
            await PlayerCmd.GainGold(power.Amount, player);
    }
}
