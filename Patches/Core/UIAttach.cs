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
        _done = true;

        AttachAfterMainMenuFrame(__instance);
    }

    private static async void AttachAfterMainMenuFrame(Node node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return;

        var tree = node.GetTree();
        if (tree == null)
            return;

        await node.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        if (GodotObject.IsInstanceValid(node))
            Loadout.UI.NLoadoutPanelRoot.AttachToTree(tree);
    }
}
