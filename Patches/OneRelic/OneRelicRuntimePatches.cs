#nullable enable

namespace Loadout.Patches.OneRelic;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.OneRelic;
using Loadout.Services.RelicReplacement;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

internal static class OneRelicFactoryPatch
{
    internal static void Postfix(Player __0, ref RelicModel __result)
    {
        if (__result is null
            || !OneRelicModeService.ShouldReplaceFactoryPull(__0)
            || !OneRelicModeService.TryGetSelectedRelic(__0, out RelicModel selected))
            return;
        if (RelicReplacementProvenance.IsForced(RelicReplacementSource.OneRelic, __result)
            && __result.CanonicalInstance.Id == selected.Id)
        {
            return;
        }
        RelicModel baseline = __result.IsMutable ? __result : __result.ToMutable();
        __result = RelicReplacementProvenance.CreateOccurrence(
            RelicReplacementSource.OneRelic,
            selected,
            baseline);
    }
}

internal static class OneRelicToMutablePatch
{
    internal static bool Prefix(RelicModel __instance, ref RelicModel __result)
    {
        if (!RelicReplacementProvenance.TryReuseOccurrence(
                RelicReplacementSource.OneRelic,
                __instance,
                out RelicModel occurrence))
        {
            return true;
        }
        __result = occurrence;
        return false;
    }
}

internal static class OneRelicObtainPatch
{
    internal static void Prefix(Player player, ref RelicModel relic, out IDisposable? __state)
    {
        OneRelicModeService.TryPrepareObtain(player, ref relic, out __state);
    }

    internal static void Postfix(IDisposable? __state) => __state?.Dispose();

    internal static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}

internal static class OneRelicTypedObtainPatch
{
    internal static bool Prefix<T>(Player player, ref Task<T> __result)
        where T : RelicModel
    {
        if (!OneRelicModeService.ShouldReplaceTypedObtain(player))
            return true;
        __result = OneRelicModeService.ObtainTypedReplacementAsync<T>(player);
        return false;
    }
}

internal static class OneRelicAncientInitialOptionsPatch
{
    internal static void Postfix(
        AncientEventModel __instance,
        ref IReadOnlyList<EventOption> __result)
    {
        __result = OneRelicLiveOfferService.ReconcileAncientInitial(__instance, __result);
    }
}

internal static class OneRelicNestedRewardCommandPatch
{
    internal static void Prefix(Player player, List<Reward> rewards)
    {
        OneRelicModeService.MarkNestedRelicRewards(player, rewards);
    }
}

internal static class OneRelicNestedRewardSelectionPatch
{
    internal static void Prefix(RelicReward __instance, out IDisposable? __state)
    {
        __state = OneRelicLiveOfferService.BeginNestedGrantSelection(__instance);
    }

    internal static void Postfix(IDisposable? __state) => __state?.Dispose();

    internal static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}

[HarmonyPatch(typeof(LoadRunLobby))]
internal static class OneRelicLoadRunLobbyConstructorPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
        => AccessTools.GetDeclaredConstructors(typeof(LoadRunLobby));

    [HarmonyPostfix]
    internal static void Postfix(LoadRunLobby __instance)
        => OneRelicModeService.RegisterLoadLobby(__instance);
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.CleanUp))]
internal static class OneRelicLoadRunLobbyCleanupPatch
{
    [HarmonyPrefix]
    internal static void Prefix(LoadRunLobby __instance, bool disconnectSession)
        => OneRelicModeService.UnregisterLoadLobby(__instance, disconnectSession);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class OneRelicRunLaunchPatch
{
    [HarmonyPrefix]
    internal static void Prefix() => OneRelicModeService.PrepareRunLaunch();

    [HarmonyPostfix]
    internal static void Postfix() => OneRelicModeService.OnRunLaunched();
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class OneRelicRunCleanupPatch
{
    [HarmonyPrefix]
    internal static void Prefix() => OneRelicModeService.OnRunCleaningUp();
}

[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeginRewardsSet))]
internal static class OneRelicRewardsSetTrackingPatch
{
    [HarmonyPostfix]
    internal static void Postfix(RewardsSet set) => OneRelicModeService.OnRewardsSetTracked(set);
}
