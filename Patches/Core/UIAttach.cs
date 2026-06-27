namespace Loadout.Patches.Core;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;


[HarmonyPatch(typeof(NGame), "LaunchMainMenu")]
public class UIAttach
{
    private static bool _done;
    [HarmonyPostfix]
    public static void Postfix(Node __instance)
    {
        if (_done) return;
        Loadout.UI.NLoadoutPanelRoot.AttachToTree(__instance.GetTree());
        _done = true;
    }
}
