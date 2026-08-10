#nullable enable

namespace Loadout.Patches.CustomRuns;

using System;
using System.Collections.Generic;
using System.Reflection;
using BaseLib.Patches.Saves;
using HarmonyLib;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

[HarmonyPatch]
public static class CustomRunPlayerCreationPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
                   typeof(Player),
                   nameof(Player.CreateForNewRun),
                   [typeof(CharacterModel), typeof(UnlockState), typeof(ulong)])
               ?? throw new MissingMethodException(typeof(Player).FullName, nameof(Player.CreateForNewRun));
    }

    [HarmonyPostfix]
    public static void Postfix(Player __result)
    {
        if (!CustomRunRuntimeSnapshotService.TryGetPendingPlayerSetup(__result.NetId, out ResolvedPlayerSetup setup))
            return;

        if (setup.StartingMaxHp.HasValue)
            __result.Creature.SetMaxHpInternal(setup.StartingMaxHp.Value);

        if (setup.StartingCurrentHp.HasValue)
            __result.Creature.SetCurrentHpInternal(Math.Min(setup.StartingCurrentHp.Value, __result.Creature.MaxHp));
        else if (setup.StartingMaxHp.HasValue && __result.Creature.CurrentHp > __result.Creature.MaxHp)
            __result.Creature.SetCurrentHpInternal(__result.Creature.MaxHp);

        if (setup.PotionSlots.HasValue)
        {
            int target = Math.Max(0, setup.PotionSlots.Value);
            int delta = target - __result.MaxPotionCount;
            if (delta > 0)
                __result.AddToMaxPotionCount(delta);
            else if (delta < 0)
                __result.SubtractFromMaxPotionCount(-delta);
        }

        if (setup.StartingGold.HasValue)
            __result.Gold = setup.StartingGold.Value;
        if (setup.BaseEnergyPerTurn.HasValue)
            __result.MaxEnergy = setup.BaseEnergyPerTurn.Value;
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
public static class CustomRunStateCreationPatch
{
    [HarmonyPostfix]
    public static void Postfix(RunState __result)
    {
        CustomRunRuntimeSnapshotService.AttachPending(__result);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
public static class CustomRunLobbyCleanupPatch
{
    [HarmonyPostfix]
    public static void Postfix(bool disconnectSession)
    {
        if (disconnectSession)
            CustomRunRuntimeSnapshotService.ClearPending();
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHandDraw))]
public static class CustomRunHandDrawPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player __1, ref decimal __2)
    {
        if (CustomRunRuntimeSnapshotService.TryGetPlayerSetup(__1, out ResolvedPlayerSetup setup)
            && setup.CardsDrawnPerTurn.HasValue)
        {
            __2 = setup.CardsDrawnPerTurn.Value;
        }
    }
}

[HarmonyPatch]
public static class CustomRunExtendedSavePatch
{
    private const string EmbeddedSaveKey = "Loadout.custom_run.snapshot_v1";
    private static bool _registered;

    public static MethodBase TargetMethod()
    {
        Type type = AccessTools.TypeByName("BaseLib.Patches.PostModInitPatch")
                    ?? throw new TypeLoadException("BaseLib.Patches.PostModInitPatch");
        return AccessTools.Method(type, "LatePostInit")
               ?? throw new MissingMethodException(type.FullName, "LatePostInit");
    }

    [HarmonyPrefix]
    public static void Prefix()
    {
        if (_registered)
            return;

        _registered = true;
        ExtendedSaveHandlers<IRunState, SerializableRun>.RegisterSave<RunState, string>(
            EmbeddedSaveKey,
            CustomRunRuntimeSnapshotService.GetSerializedSnapshotForSave,
            CustomRunRuntimeSnapshotService.LoadSerializedSnapshot,
            static (payload, writer) => writer.WriteString(payload ?? string.Empty),
            static reader => reader.ReadString());
    }
}
