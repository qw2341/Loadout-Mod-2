#nullable enable

namespace Loadout.Patches.Core;

using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.DevConsole;

/// <summary>
/// Reuses the game's already-networked ConsoleCmdGameAction envelope for a
/// private Loadout action payload. This avoids registering a new console command
/// or a new net-action subtype while still executing card creation in the
/// synchronized game-action queue on every peer.
/// </summary>
[HarmonyPatch(typeof(ConsoleCmdGameAction), "ExecuteAction")]
public static class LoadoutConsoleCmdGameActionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ConsoleCmdGameAction __instance, ref Task __result)
    {
        try
        {
            if (!LoadoutImmediateMutationService.TryHandleSynchronizedConsoleAction(__instance, out Task result))
                return true;

            __result = result;
            return false;
        }
        catch (Exception exception) when ((__instance.Cmd ?? string.Empty).StartsWith("__loadout_", StringComparison.Ordinal))
        {
            GD.PushError($"Loadout synchronized console action failed before execution: {exception}");
            __result = Task.CompletedTask;
            return false;
        }
    }
}
