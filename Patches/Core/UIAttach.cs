using System;
using Godot;
using HarmonyLib;
using Loadout.Services.CardModification;
using Loadout.Services.LastActions;
using Loadout.Services.Loadouts;
using Loadout.Services.PowerGiver;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Logging;

namespace Loadout.Patches.Core;

[HarmonyPatch(typeof(AssetLoadingSession), nameof(AssetLoadingSession.Process))]
public static class UIAttach
{
    private static bool _servicesRegistered;
    private static bool _attachScheduled;
    private static bool _attached;

    [HarmonyPostfix]
    private static void Postfix(AssetLoadingSession __instance)
    {
        if (_attached || _attachScheduled)
            return;

        if (__instance == null || !__instance.IsCompleted)
            return;

        string sessionName = GetSessionName(__instance);

        if (!string.Equals(sessionName, "Common", StringComparison.Ordinal))
            return;

        RegisterServicesOnce();

        _attachScheduled = true;

        Log.Info("[Loadout] Common preload complete. Scheduling UI attach.");

        Callable.From(AttachUiDeferred).CallDeferred();
    }

    private static void AttachUiDeferred()
    {
        try
        {
            SceneTree tree = Engine.GetMainLoop() as SceneTree;

            if (tree == null)
            {
                Log.Error("[Loadout] Failed to attach UI: SceneTree was null.");
                _attachScheduled = false;
                return;
            }

            var root = Loadout.UI.NLoadoutPanelRoot.GetOrAttach(tree);

            if (root == null || !GodotObject.IsInstanceValid(root))
            {
                Log.Error("[Loadout] Failed to attach UI: root was null or invalid.");
                _attachScheduled = false;
                return;
            }

            _attached = true;
            _attachScheduled = false;

            Log.Info("[Loadout] Attached UI root after Common preload complete.");
        }
        catch (Exception e)
        {
            _attached = false;
            _attachScheduled = false;
            Log.Error($"[Loadout] Failed to attach UI after Common preload: {e}");
        }
    }

    private static void RegisterServicesOnce()
    {
        if (_servicesRegistered)
            return;

        _servicesRegistered = true;

        PowerGiverStateService.Register();
        LastActionService.Register();
        CardModificationStateService.Register();
        LoadoutStorageService.Register();
        LoadoutHostSharingService.Register();

        Log.Info("[Loadout] Services registered.");
    }

    private static string GetSessionName(AssetLoadingSession session)
    {
        object value = AccessTools
            .Field(typeof(AssetLoadingSession), "_name")
            .GetValue(session);

        return value as string ?? string.Empty;
    }
}
