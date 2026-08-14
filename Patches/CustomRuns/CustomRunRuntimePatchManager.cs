#nullable enable

namespace Loadout.Patches.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Runtime;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

public static class CustomRunRuntimePatchManager
{
    private const string HarmonyId = "Loadout.CustomRuns.Runtime";
    private static readonly object Gate = new();
    private static readonly Harmony RuntimeHarmony = new(HarmonyId);
    private static string? _snapshotHash;
    private static bool _pendingLaunch;
    private static bool _hasPatches;

    public static bool IsActive { get; private set; }

    public static void ActivateForSnapshot(ResolvedCustomRunSnapshot snapshot, bool pendingLaunch)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (Gate)
        {
            if (IsActive
                && _pendingLaunch == pendingLaunch
                && string.Equals(_snapshotHash, snapshot.SnapshotHash, StringComparison.Ordinal))
            {
                return;
            }

            UnpatchAll();
            try
            {
                InstallCorePatches(snapshot, pendingLaunch);
                InstallRulePatches(snapshot);
                _snapshotHash = snapshot.SnapshotHash;
                _pendingLaunch = pendingLaunch;
                IsActive = true;
            }
            catch
            {
                UnpatchAll();
                throw;
            }
        }
    }

    public static void Deactivate()
    {
        lock (Gate)
            UnpatchAll();
    }

    private static void InstallCorePatches(ResolvedCustomRunSnapshot snapshot, bool pendingLaunch)
    {
        Patch(
            RequiredMethod(typeof(RunManager), nameof(RunManager.Launch)),
            typeof(CustomRunRuleRuntimeLaunchPatch),
            prefix: true,
            postfix: true);
        Patch(
            RequiredMethod(typeof(RunManager), nameof(RunManager.CleanUp)),
            typeof(CustomRunRuleRuntimeCleanupPatch),
            prefix: true,
            postfix: true);

        if (pendingLaunch)
        {
            Patch(
                RequiredMethod(typeof(RunState), nameof(RunState.CreateForNewRun)),
                typeof(CustomRunStateCreationPatch),
                prefix: true,
                postfix: true);
            Patch(
                RequiredMethod(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp)),
                typeof(CustomRunLobbyCleanupPatch),
                postfix: true);
            if (snapshot.Players.Any(NeedsPlayerCreationPatch))
            {
                Patch(
                    CustomRunPlayerCreationPatch.TargetMethod(),
                    typeof(CustomRunPlayerCreationPatch),
                    postfix: true);
            }
            if (snapshot.Players.Any(player => player.PotionSlots.HasValue || player.OverridePotions))
            {
                Patch(
                    CustomRunPostAscensionSetupPatch.TargetMethod(),
                    typeof(CustomRunPostAscensionSetupPatch),
                    postfix: true);
            }
        }

        if (snapshot.Players.Any(player => player.CardsDrawnPerTurn.HasValue))
        {
            Patch(
                RequiredMethod(typeof(Hook), nameof(Hook.ModifyHandDraw)),
                typeof(CustomRunHandDrawPatch),
                prefix: true);
        }
    }

    private static void InstallRulePatches(ResolvedCustomRunSnapshot snapshot)
    {
        IReadOnlySet<string> triggers = CustomRunRulePlan.GetTriggerIds(snapshot.Rules);
        PatchTrigger(triggers, "Loadout2:RunEnd", typeof(RunManager), nameof(RunManager.OnEnded),
            typeof(CustomRunRuleRuntimeRunEndPatch), prefix: true);
        PatchTrigger(triggers, "Loadout2:CardPlayed", typeof(Hook), nameof(Hook.AfterCardPlayed),
            typeof(CustomRunAfterCardPlayedPatch), postfix: true);
        if (triggers.Contains("Loadout2:RoomEntered") || triggers.Contains("Loadout2:EventEntered"))
        {
            Patch(RequiredMethod(typeof(Hook), nameof(Hook.AfterRoomEntered)),
                typeof(CustomRunAfterRoomEnteredPatch), postfix: true);
        }
        PatchTrigger(triggers, "Loadout2:RoomCompleted", typeof(NMapScreen), nameof(NMapScreen.Open),
            typeof(CustomRunRoomCompletedPatch), prefix: true, postfix: true);

        if (CustomRunRulePlan.NeedsCombatLifecycle(snapshot))
        {
            Patch(RequiredMethod(typeof(Hook), nameof(Hook.BeforeCombatStart)),
                typeof(CustomRunBeforeCombatStartPatch), postfix: true);
        }

        PatchTrigger(triggers, "Loadout2:CardDrawn", typeof(Hook), nameof(Hook.AfterCardDrawn),
            typeof(CustomRunAfterCardDrawnPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:CardDiscarded", typeof(Hook), nameof(Hook.AfterCardDiscarded),
            typeof(CustomRunAfterCardDiscardedPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:CardExhausted", typeof(Hook), nameof(Hook.AfterCardExhausted),
            typeof(CustomRunAfterCardExhaustedPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:CardGenerated", typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat),
            typeof(CustomRunAfterCardGeneratedPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:PlayerTakesDamage", typeof(Hook), nameof(Hook.AfterDamageReceived),
            typeof(CustomRunAfterDamageReceivedPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:PlayerGainsBlock", typeof(Hook), nameof(Hook.AfterBlockGained),
            typeof(CustomRunAfterBlockGainedPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:PowerReceived", typeof(Hook), nameof(Hook.AfterPowerAmountChanged),
            typeof(CustomRunAfterPowerChangedPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:PotionUsed", typeof(Hook), nameof(Hook.AfterPotionUsed),
            typeof(CustomRunAfterPotionUsedPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:PotionObtained", typeof(Hook), nameof(Hook.AfterPotionProcured),
            typeof(CustomRunAfterPotionProcuredPatch), postfix: true);
        PatchTrigger(triggers, "Loadout2:EnergySpent", typeof(Hook), nameof(Hook.AfterEnergySpent),
            typeof(CustomRunAfterEnergySpentPatch), postfix: true);

        if (triggers.Contains("Loadout2:CardObtained"))
            Patch(CustomRunCardObtainedPatch.TargetMethod(), typeof(CustomRunCardObtainedPatch), postfix: true);
        if (triggers.Contains("Loadout2:RelicObtained"))
        {
            Patch(
                RequiredMethod(
                    typeof(RelicCmd),
                    nameof(RelicCmd.Obtain),
                    [typeof(RelicModel), typeof(Player), typeof(int)]),
                typeof(CustomRunRelicObtainedPatch),
                postfix: true);
        }
        if (triggers.Contains("Loadout2:MonsterSpawned"))
        {
            Patch(
                RequiredMethod(
                    typeof(CreatureCmd),
                    nameof(CreatureCmd.Add),
                    [typeof(MonsterModel), typeof(ICombatState), typeof(CombatSide), typeof(string)]),
                typeof(CustomRunMonsterSpawnedPatch),
                postfix: true);
        }
        PatchTrigger(triggers, "Loadout2:MonsterMoveStarted", typeof(MonsterModel), nameof(MonsterModel.PerformMove),
            typeof(CustomRunMonsterMoveStartedPatch), prefix: true);

        if (snapshot.Rules.Any(rule => rule.Actions.Any(action => action.TypeId == "Loadout2:SetNextEvent")))
        {
            Patch(
                CustomRunPendingEventRoomPatch.TargetMethod(),
                typeof(CustomRunPendingEventRoomPatch),
                prefix: true,
                postfix: true);
        }
    }

    private static bool NeedsPlayerCreationPatch(ResolvedPlayerSetup player)
    {
        return player.StartingGold.HasValue
               || player.StartingCurrentHp.HasValue
               || player.StartingMaxHp.HasValue
               || player.BaseEnergyPerTurn.HasValue
               || player.OverrideDeck
               || player.OverrideRelics;
    }

    private static void PatchTrigger(
        IReadOnlySet<string> triggers,
        string triggerId,
        Type originalType,
        string originalName,
        Type patchType,
        bool prefix = false,
        bool postfix = false)
    {
        if (triggers.Contains(triggerId))
            Patch(RequiredMethod(originalType, originalName), patchType, prefix, postfix);
    }

    private static MethodBase RequiredMethod(Type type, string name, Type[]? parameters = null)
    {
        return AccessTools.Method(type, name, parameters)
               ?? throw new MissingMethodException(type.FullName, name);
    }

    private static void Patch(
        MethodBase original,
        Type patchType,
        bool prefix = false,
        bool postfix = false)
    {
        HarmonyMethod? prefixMethod = prefix ? GetPatchMethod(patchType, "Prefix") : null;
        HarmonyMethod? postfixMethod = postfix ? GetPatchMethod(patchType, "Postfix") : null;
        RuntimeHarmony.Patch(original, prefix: prefixMethod, postfix: postfixMethod);
        _hasPatches = true;
    }

    private static HarmonyMethod GetPatchMethod(Type patchType, string methodName)
    {
        MethodInfo method = AccessTools.DeclaredMethod(patchType, methodName)
                            ?? throw new MissingMethodException(patchType.FullName, methodName);
        return new HarmonyMethod(method);
    }

    private static void UnpatchAll()
    {
        if (_hasPatches)
            RuntimeHarmony.UnpatchAll(HarmonyId);
        _hasPatches = false;
        _snapshotHash = null;
        _pendingLaunch = false;
        IsActive = false;
    }
}
