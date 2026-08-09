#nullable enable

namespace Loadout.Patches.Core;

using System;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

[HarmonyPatch(typeof(ConditionalBranchState), nameof(ConditionalBranchState.GetNextState))]
public static class LoadoutSummonMonsterConditionalBranchPatch
{
    [HarmonyFinalizer]
    public static Exception? Finalizer(
        Creature __0,
        ref string __result,
        Exception? __exception)
    {
        if (__exception is null)
            return null;

        if (!LoadoutSummonMonsterService.TryGetDefaultIntentStateId(
                __0,
                __exception,
                out string stateId))
        {
            return __exception;
        }

        __result = stateId;
        return null;
    }
}
