#nullable enable

namespace Loadout.Patches.CreatureManipulation;

using HarmonyLib;
using Loadout.Services.CreatureManipulation;
using Loadout.UI.CreatureManipulation;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
public static class CreatureManipulationCreatureReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCreature __instance) =>
        CreatureManipulationUiService.OnCreatureReady(__instance);
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature._ExitTree))]
public static class CreatureManipulationCreatureExitPatch
{
    [HarmonyPrefix]
    public static void Prefix(NCreature __instance) =>
        CreatureManipulationUiService.OnCreatureExit(__instance);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class CreatureManipulationRunLaunchPatch
{
    [HarmonyPostfix]
    public static void Postfix() =>
        CreatureManipulationStateService.OnRunLaunched();
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class CreatureManipulationRunCleanupPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        CreatureManipulationUiService.Clear();
        CreatureManipulationStateService.OnRunCleaningUp();
    }
}
