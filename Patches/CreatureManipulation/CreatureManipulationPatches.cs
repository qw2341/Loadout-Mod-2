#nullable enable

namespace Loadout.Patches.CreatureManipulation;

using Godot;
using HarmonyLib;
using Loadout.Services.CreatureManipulation;
using Loadout.Services.Loadouts;
using Loadout.UI.CreatureManipulation;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
public static class CreatureManipulationCreatureReadyPatch
{
    private static readonly StringName BoundMeta = new("loadout_creature_manipulation_bound");

    [HarmonyPostfix]
    private static void Postfix(NCreature __instance)
    {
        CreatureManipulationStateService.BindCreatureNode(__instance);
        if (__instance.Hitbox is null || __instance.Hitbox.HasMeta(BoundMeta))
            return;

        __instance.Hitbox.SetMeta(BoundMeta, true);
        __instance.Hitbox.GuiInput += inputEvent =>
        {
            if (inputEvent is not InputEventMouseButton
                {
                    ButtonIndex: MouseButton.Right,
                    Pressed: false
                } mouseButton)
            {
                return;
            }

            if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
                return;

            NQuickCreatureManipulationPanel.ShowFor(__instance, mouseButton.GlobalPosition);
            __instance.Hitbox.AcceptEvent();
        };
    }
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature.SetAnimationTrigger))]
public static class CreatureManipulationAnimationPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NCreature __instance, ref string trigger)
    {
        return CreatureManipulationStateService.TryMapMorphAnimation(__instance, ref trigger);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class CreatureManipulationRunLaunchPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        CreatureManipulationStateService.OnRunLaunched();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class CreatureManipulationRunCleanupPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        NQuickCreatureManipulationPanel.CloseCurrent();
        CreatureManipulationStateService.OnRunCleaningUp();
    }
}
