#nullable enable

namespace Loadout.Patches.Morphing;

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using Loadout.Services.Morphing;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;

[HarmonyPatch(typeof(Creature), nameof(Creature.Name), MethodType.Getter)]
public static class BottledMonsterMorphCreatureDisplayNamePatch
{
    [HarmonyPostfix]
    public static void Postfix(Creature __instance, ref string __result)
    {
        __result = BottledMonsterMorphService.ResolveMorphCreatureDisplayName(__instance, __result);
    }
}

[HarmonyPatch(typeof(PowerModel), nameof(PowerModel.HoverTips), MethodType.Getter)]
public static class BottledMonsterMorphPowerDescriptionPatch
{
    private static readonly MethodInfo AddLocStringMethod = AccessTools.Method(
        typeof(LocString),
        nameof(LocString.Add),
        [typeof(string), typeof(LocString)]);

    private static readonly MethodInfo AddStringMethod = AccessTools.Method(
        typeof(LocString),
        nameof(LocString.Add),
        [typeof(string), typeof(string)]);

    private static readonly MethodInfo ResolveOwnerMethod = AccessTools.Method(
        typeof(BottledMonsterMorphService),
        nameof(BottledMonsterMorphService.ResolveMorphOwnerDescriptionName));

    private static readonly MethodInfo ResolveApplierMethod = AccessTools.Method(
        typeof(BottledMonsterMorphService),
        nameof(BottledMonsterMorphService.ResolveMorphApplierDescriptionName));

    private static readonly MethodInfo ResolveTargetMethod = AccessTools.Method(
        typeof(BottledMonsterMorphService),
        nameof(BottledMonsterMorphService.ResolveMorphTargetDescriptionName));

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> original = instructions.ToList();
        int ownerCall = FindVariableAdd(original, "OwnerName", AddLocStringMethod);
        int applierCall = FindVariableAdd(original, "ApplierName", AddStringMethod);
        int targetCall = FindVariableAdd(original, "TargetName", AddStringMethod);

        if (ownerCall < 0 || applierCall < 0 || targetCall < 0)
        {
            GD.PushWarning(
                "BottledMonsterMorph: current game build changed PowerModel.HoverTips; morph-aware description names were left disabled.");
            return original;
        }

        List<CodeInstruction> patched = original.ToList();
        foreach ((int callIndex, MethodInfo resolver) in new[]
                 {
                     (targetCall, ResolveTargetMethod),
                     (applierCall, ResolveApplierMethod),
                     (ownerCall, ResolveOwnerMethod)
                 }.OrderByDescending(entry => entry.Item1))
        {
            InsertResolverCall(patched, callIndex, resolver);
        }

        return patched;
    }

    private static int FindVariableAdd(
        IReadOnlyList<CodeInstruction> instructions,
        string variableName,
        MethodInfo addMethod)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].opcode != OpCodes.Ldstr
                || instructions[index].operand is not string value
                || !string.Equals(value, variableName, System.StringComparison.Ordinal))
            {
                continue;
            }

            for (int callIndex = index + 1; callIndex < instructions.Count; callIndex++)
            {
                CodeInstruction instruction = instructions[callIndex];
                if (instruction.Calls(addMethod))
                    return callIndex;

                if (instruction.operand is MethodInfo method
                    && method.DeclaringType == typeof(LocString)
                    && method.Name == nameof(LocString.Add))
                {
                    break;
                }
            }
        }

        return -1;
    }

    private static void InsertResolverCall(
        List<CodeInstruction> instructions,
        int callIndex,
        MethodInfo resolver)
    {
        CodeInstruction originalCall = instructions[callIndex];
        CodeInstruction loadPower = new(OpCodes.Ldarg_0);
        loadPower.labels.AddRange(originalCall.labels);
        loadPower.blocks.AddRange(originalCall.blocks);
        originalCall.labels.Clear();
        originalCall.blocks.Clear();

        instructions.Insert(callIndex, loadPower);
        instructions.Insert(callIndex + 1, new CodeInstruction(OpCodes.Call, resolver));
    }
}

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
