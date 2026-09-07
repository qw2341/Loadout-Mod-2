#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Morphing;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

[HarmonyPatch(typeof(PersonalHivePower), nameof(PersonalHivePower.AfterDamageReceived))]
public static class PersonalHivePowerPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        PersonalHivePower __instance,
        Creature target,
        Creature? dealer,
        ValueProp props,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer
            || target != __instance.Owner
            || dealer is null
            || dealer.Player is not null)
        {
            return true;
        }

        __result = props.IsPoweredAttack()
                   && dealer.IsMonster
                   && dealer.Side != __instance.Owner.Side
                   && BottledMonsterMorphService.IsPlayerMorphedAs<Entomancer>(
                       __instance.Owner.Player)
            ? StunForTurns(dealer, __instance.Amount)
            : Task.CompletedTask;
        return false;
    }

    private static Task StunForTurns(Creature monster, int turns)
    {
        if (turns <= 0 || monster.IsDead)
            return Task.CompletedTask;

        return CreatureCmd.Stun(
            monster,
            _ => StunForTurns(monster, turns - 1));
    }
}
