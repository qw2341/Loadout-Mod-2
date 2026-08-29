#nullable enable

using MegaCrit.Sts2.Core.Combat;

namespace Loadout.Patches.Core;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

[HarmonyPatch(typeof(InfestedPower), nameof(InfestedPower.ShouldStopCombatFromEnding))]
public static class LoadoutPlayerInfestedCombatEndPatch
{
    [HarmonyPostfix]
    public static void Postfix(InfestedPower __instance, ref bool __result)
    {
        if (__instance.Owner.IsPlayer)
            __result = false;
    }
}

[HarmonyPatch(typeof(InfestedPower), nameof(InfestedPower.AfterDeath))]
public static class LoadoutPlayerInfestedDeathPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        InfestedPower __instance,
        Creature target,
        bool wasRemovalPrevented,
        float deathAnimLength,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer)
            return true;

        __result = AfterPlayerDeath(
            __instance,
            target,
            wasRemovalPrevented,
            deathAnimLength);
        return false;
    }

    private static async Task AfterPlayerDeath(
        InfestedPower infestedPower,
        Creature target,
        bool wasRemovalPrevented,
        float deathAnimLength)
    {
        if (wasRemovalPrevented || infestedPower.Owner != target)
            return;

        for (int index = 0; index < 4; index++)
        {
            string slotName = PhrogParasiteElite.GetWrigglerSlotName(index);
            Wriggler wriggler = (Wriggler)ModelDb.Monster<Wriggler>().ToMutable();
            wriggler.StartStunned = true;
            IReadOnlyList<NCreature> existingEnemyNodes =
                LoadoutSummonMonsterService.GetCurrentEnemyNodes();
            Creature creature = await CreatureCmd.Add(
                wriggler,
                infestedPower.CombatState,
                CombatSide.Enemy,
                null);
            creature.SlotName = slotName;
            LoadoutSummonMonsterService.PositionUnslottedNestedSummon(
                creature,
                existingEnemyNodes);
        }
    }
}

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
