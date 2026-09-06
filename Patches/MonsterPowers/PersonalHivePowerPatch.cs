#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

[HarmonyPatch(typeof(PersonalHivePower), nameof(PersonalHivePower.AfterDamageReceived))]
public static class PersonalHivePowerPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        PersonalHivePower __instance,
        Creature target,
        Creature? dealer,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer
            || target != __instance.Owner
            || dealer is null
            || dealer.Player is not null)
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}
