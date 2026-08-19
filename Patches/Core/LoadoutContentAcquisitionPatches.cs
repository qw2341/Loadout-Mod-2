#nullable enable

namespace Loadout.Patches.Core;

using HarmonyLib;
using Loadout.Patches.ContentBans;
using Loadout.Services.Actions;
using Loadout.Services.ContentBans;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldAddToDeck))]
public static class LoadoutShouldAddToDeckPatch
{
    private static ContentBanDeckPreventer BanPreventer =>
        ModelDb.GetById<ContentBanDeckPreventer>(
            ModelDb.GetId(typeof(ContentBanDeckPreventer)));

    [HarmonyPrefix]
    public static bool Prefix(CardModel card, ref AbstractModel? preventer, ref bool __result)
    {
        if (card.FloorAddedToDeck is null
            && ContentBanService.HasAnyBans(ContentBanKind.Card)
            && ContentBanService.IsBanned(card))
        {
            preventer = BanPreventer;
            __result = false;
            return false;
        }

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
    public static bool Prefix(PotionModel potion, ref bool __result)
    {
        if (ContentBanService.HasAnyBans(ContentBanKind.Potion)
            && ContentBanService.IsBanned(potion))
        {
            __result = false;
            return false;
        }

        if (!LoadoutContentAcquisitionRules.ShouldIgnoreModelRestrictions)
            return true;

        __result = true;
        return false;
    }
}
