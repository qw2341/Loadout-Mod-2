#nullable enable

namespace Loadout.Patches.Core;

using System.Threading.Tasks;
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
        if (!LoadoutImmediateMutationService.TryHandleSynchronizedConsoleAction(__instance, out Task result))
            return true;

        __result = result;
        return false;
    }
}
