#nullable enable

namespace Loadout.Patches.CustomRuns;

using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using Loadout.UI;
using Loadout.UI.CustomRuns;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;

[HarmonyPatch]
public static class CustomRunDeckPreviewBackPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(NCardsViewScreen), "OnReturnButtonPressed")
               ?? throw new MissingMethodException(typeof(NCardsViewScreen).FullName, "OnReturnButtonPressed");
    }

    [HarmonyPrefix]
    public static bool Prefix(NCardsViewScreen __instance)
    {
        if (!__instance.HasMeta(CustomRunEditorPreviewService.PreviewMeta))
            return true;

        NLoadoutPanelRoot.CloseTopLoadoutScreen();
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(__instance))
                __instance.QueueFree();
        }).CallDeferred();
        return false;
    }
}
