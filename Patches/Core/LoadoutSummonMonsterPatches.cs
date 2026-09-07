#nullable enable

namespace Loadout.Patches.Core;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

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

[HarmonyPatch(typeof(DecimillipedeSegment), nameof(DecimillipedeSegment.AfterAddedToRoom))]
public static class LoadoutSummonDecimillipedeSegmentPatch
{
    [HarmonyPrefix]
    public static bool Prefix(DecimillipedeSegment __instance, ref Task __result)
    {
        return !LoadoutSummonMonsterService.TryHandleDecimillipedeSegmentAdded(
            __instance,
            out __result);
    }
}

[HarmonyPatch(typeof(Queen), nameof(Queen.AfterAddedToRoom))]
public static class LoadoutSummonQueenPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Queen __instance, ref Task __result)
    {
        return !LoadoutSummonMonsterService.TryHandleQueenAddedWithoutAmalgam(
            __instance,
            out __result);
    }
}

[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature))]
public static class LoadoutNestedMonsterSummonSlotPatch
{
    [HarmonyPrefix]
    public static void Prefix(
        Creature creature,
        out IReadOnlyList<NCreature>? __state)
    {
        __state = LoadoutSummonMonsterService.TryPrepareUnsupportedSummonSlot(
            creature,
            out IReadOnlyList<NCreature> existingEnemyNodes)
            ? existingEnemyNodes
            : null;
    }

    [HarmonyPostfix]
    public static void Postfix(
        Creature creature,
        IReadOnlyList<NCreature>? __state)
    {
        if (__state is not null)
        {
            LoadoutSummonMonsterService.PositionUnslottedNestedSummon(
                creature,
                __state);
        }
    }
}
