#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

[HarmonyPatch]
public static class PlayerOwnedCombatEndingPowerPatch
{
    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods()
    {
        const BindingFlags flags =
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.DeclaredOnly;

        foreach (Type type in typeof(PowerModel).Assembly.GetTypes())
        {
            if (!typeof(PowerModel).IsAssignableFrom(type))
                continue;

            MethodInfo? method = type.GetMethod(
                nameof(PowerModel.ShouldStopCombatFromEnding),
                flags,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);
            if (method is not null && method.ReturnType == typeof(bool))
                yield return method;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(PowerModel __instance, ref bool __result)
    {
        if (__instance.Owner.IsPlayer)
            __result = false;
    }
}
