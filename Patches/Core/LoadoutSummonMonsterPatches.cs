#nullable enable

namespace Loadout.Patches.Core;

using System;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

[HarmonyPatch(typeof(ConditionalBranchState), nameof(ConditionalBranchState.GetNextState))]
public static class LoadoutSummonMonsterConditionalBranchPatch
{
    [HarmonyFinalizer]
    public static Exception? Finalizer(
        ConditionalBranchState __instance,
        ref string __result,
        Exception? __exception)
    {
        if (__exception is null)
            return null;

        if (!LoadoutSummonMonsterService.TryGetDefaultIntentStateId(
                __instance,
                __exception,
                out string stateId))
        {
            return __exception;
        }

        __result = stateId;
        return null;
    }
}
