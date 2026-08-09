#nullable enable

namespace Loadout.Patches.Compatibility;

using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;

[HarmonyPatch]
internal static class HextechRunesCreaturePreviewCompatibilityPatch
{
    private const string TargetTypeName = "HextechRunes.HextechNearDeathFeastVisual";

    private static MethodBase? TargetMethod()
    {
        Type? targetType = AccessTools.TypeByName(TargetTypeName);
        return targetType is null
            ? null
            : AccessTools.DeclaredMethod(targetType, "TryAttach", [typeof(NCreature)]);
    }

    [HarmonyPrepare]
    private static bool Prepare()
    {
        return TargetMethod() is not null;
    }

    [HarmonyPrefix]
    private static bool Prefix(NCreature __0)
    {
        Creature? creature = __0.Entity;
        return creature?.CombatState is not NullCombatState;
    }
}
