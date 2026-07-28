#nullable enable

namespace Loadout.Patches.Core;

using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldAddToDeck))]
public static class LoadoutShouldAddToDeckPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref AbstractModel? preventer, ref bool __result)
    {
        if (!LoadoutContentAcquisitionRules.ShouldIgnoreModelRestrictions)
            return true;

        preventer = null;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldProcurePotion))]
public static class LoadoutShouldProcurePotionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool __result)
    {
        if (!LoadoutContentAcquisitionRules.ShouldIgnoreModelRestrictions)
            return true;

        __result = true;
        return false;
    }
}
