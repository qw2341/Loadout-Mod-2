#nullable enable

namespace Loadout.Patches.Morphing;

using HarmonyLib;
using Loadout.Services.Morphing;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.RestSite;
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

[HarmonyPatch(typeof(Creature), nameof(Creature.LoseHpInternal))]
public static class BottledMonsterMorphDamageSfxPatch
{
    [HarmonyPostfix]
    public static void Postfix(Creature __instance, DamageResult __result)
    {
        BottledMonsterMorphService.PlayMorphDamageSfx(__instance, __result);
    }
}

[HarmonyPatch(typeof(SfxCmd), nameof(SfxCmd.PlayDeath), typeof(Player))]
public static class BottledMonsterMorphDeathSfxPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Player player)
    {
        return !BottledMonsterMorphService.TryPlayMorphDeathSfx(player);
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

[HarmonyPatch(typeof(NRestSiteRoom), nameof(NRestSiteRoom._Ready))]
public static class BottledMonsterMorphRestSiteRoomReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRestSiteRoom __instance)
    {
        BottledMonsterMorphService.OnRestSiteRoomReady(__instance);
    }
}

[HarmonyPatch(typeof(NRestSiteCharacter), nameof(NRestSiteCharacter._Ready))]
public static class BottledMonsterMorphRestSiteCharacterReadyPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NRestSiteCharacter __instance)
    {
        return !BottledMonsterMorphService.ShouldSkipRestSiteVisualProxyReady(__instance);
    }
}

[HarmonyPatch(typeof(NRestSiteCharacter), nameof(NRestSiteCharacter.HideFlameGlow))]
public static class BottledMonsterMorphRestSiteFlamePatch
{
    [HarmonyPostfix]
    public static void Postfix(NRestSiteCharacter __instance)
    {
        BottledMonsterMorphService.OnRestSiteFlameGlowHidden(__instance);
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
