namespace Loadout.Patches.Core;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public class UIAttach
{
    private static bool _attachScheduled;
    private static bool _attached;

    [HarmonyPostfix]
    public static void Postfix(NMainMenu __instance)
    {
        if (_attached || _attachScheduled)
            return;

        if (__instance == null || !GodotObject.IsInstanceValid(__instance))
            return;

        _attachScheduled = true;
        Log.Info("[Loadout] Scheduling menu-ready UI attach.");
        Callable.From<Node>(AttachAfterMainMenuFrame).CallDeferred(__instance);
    }

    private static async void AttachAfterMainMenuFrame(Node node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            _attachScheduled = false;
            return;
        }

        SceneTree tree = node.GetTree();
        if (tree == null)
        {
            _attachScheduled = false;
            return;
        }

        await node.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            _attachScheduled = false;
            return;
        }

        var root = Loadout.UI.NLoadoutPanelRoot.GetOrAttach(tree);
        if (root == null || !GodotObject.IsInstanceValid(root))
        {
            _attachScheduled = false;
            return;
        }

        _attached = true;
        _attachScheduled = false;
        Log.Info("[Loadout] Attached UI root after main menu ready.");
    }
}
