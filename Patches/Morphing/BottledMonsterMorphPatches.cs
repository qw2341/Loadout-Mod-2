#nullable enable

namespace Loadout.Patches.Morphing;

using HarmonyLib;
using Loadout.Services.Morphing;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;

[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
public static class BottledMonsterMorphCreatureReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCreature __instance)
    {
        BottledMonsterMorphService.OnCreatureReady(__instance);
    }
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature.SetAnimationTrigger))]
public static class BottledMonsterMorphAnimationPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NCreature __instance, string trigger)
    {
        return !BottledMonsterMorphService.TryHandleAnimation(__instance, trigger);
    }
}

[HarmonyPatch(typeof(NMerchantRoom), "AfterRoomIsLoaded")]
public static class BottledMonsterMorphMerchantRoomReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMerchantRoom __instance)
    {
        BottledMonsterMorphService.OnMerchantRoomReady(__instance);
    }
}

[HarmonyPatch(typeof(NFakeMerchant), "AfterRoomIsLoaded")]
public static class BottledMonsterMorphFakeMerchantReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NFakeMerchant __instance)
    {
        BottledMonsterMorphService.OnFakeMerchantReady(__instance);
    }
}

[HarmonyPatch(typeof(NMerchantCharacter), nameof(NMerchantCharacter.PlayAnimation))]
public static class BottledMonsterMorphMerchantAnimationPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NMerchantCharacter __instance, string anim, bool loop)
    {
        if (__instance is not NMorphedMerchantCharacter morphedCharacter)
            return true;

        morphedCharacter.PlayMorphAnimation(anim, loop);
        return false;
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class BottledMonsterMorphRunLaunchPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        BottledMonsterMorphService.OnRunLaunched();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class BottledMonsterMorphRunCleanupPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        BottledMonsterMorphService.OnRunCleaningUp();
    }
}
