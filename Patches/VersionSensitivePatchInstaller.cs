#nullable enable

namespace Loadout.Patches;

using System;
using System.Reflection;
using HarmonyLib;
using Loadout.Patches.Core;
using Loadout.Patches.TildeKey;
using Loadout.Services.Compatibility;

internal static class VersionSensitivePatchInstaller
{
    private static bool _installed;

    internal static void Install(Harmony harmony)
    {
        if (_installed)
            return;

        try
        {
            PatchPostfix(
                harmony,
                Sts2Compatibility.BatchCardAddMethod,
                typeof(LoadoutNativeDeckAddContentPatch),
                nameof(LoadoutNativeDeckAddContentPatch.Postfix));

            PatchPostfix(
                harmony,
                Sts2Compatibility.ModifyDamageMethod,
                typeof(TildeKeyModifyDamagePatch),
                TildeKeyModifyDamagePatch.GetPostfixMethodName());

            PatchPrefix(
                harmony,
                Sts2Compatibility.MultiTargetDamageMethod,
                typeof(TildeKeyGodmodeDamagePatch),
                nameof(TildeKeyGodmodeDamagePatch.Prefix));

            _installed = true;
        }
        catch
        {
            _installed = false;
            throw;
        }
    }

    private static void PatchPostfix(
        Harmony harmony,
        MethodInfo target,
        Type patchType,
        string postfixName)
    {
        MethodInfo postfix = AccessTools.Method(patchType, postfixName)
                             ?? throw new MissingMethodException(patchType.FullName, postfixName);
        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    }

    private static void PatchPrefix(
        Harmony harmony,
        MethodInfo target,
        Type patchType,
        string prefixName)
    {
        MethodInfo prefix = AccessTools.Method(patchType, prefixName)
                            ?? throw new MissingMethodException(patchType.FullName, prefixName);
        harmony.Patch(target, prefix: new HarmonyMethod(prefix));
    }
}
