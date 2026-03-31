namespace Loadout.Patches.Core;
using MegaCrit.Sts2.Core.Nodes;
using HarmonyLib;
using Godot;


[HarmonyPatch(typeof(NGame), "_Ready")]
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