using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.CardModification;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.RelicModification;
using Loadout.Services.LastActions;
using Loadout.Services.Loadouts;
using Loadout.Services.PowerGiver;
using Loadout.Services.TildeKey;
using Loadout.UI;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace Loadout.Patches.Core;

[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.InitIds))]
public static class UIAttach
{
    private static bool _servicesRegistered;
    private static bool _startupScheduled;
    private static bool _startupAttempted;
    private static bool _preloadScheduled;
    private static bool _preloadAttempted;
    private static bool _preloaded;

    [HarmonyPostfix]
    private static void Postfix()
    {
        if (_startupScheduled || _startupAttempted)
            return;

        _startupScheduled = true;

        Log.Info("[Loadout] ModelDb IDs initialized. Scheduling UI attach and select-screen preload.");

        Callable.From(AttachUiDeferred).CallDeferred();
    }

    private static void AttachUiDeferred()
    {
        _startupScheduled = false;
        if (_startupAttempted)
            return;

        _startupAttempted = true;

        try
        {
            RegisterServicesOnce();

            SceneTree tree = Engine.GetMainLoop() as SceneTree;

            if (tree == null)
            {
                Log.Error("[Loadout] Failed to attach UI after ModelDb initialization: SceneTree was null.");
                return;
            }

            NLoadoutPanelRoot root = NLoadoutPanelRoot.GetOrAttach(tree);

            if (root == null || !GodotObject.IsInstanceValid(root))
            {
                Log.Error("[Loadout] Failed to attach UI after ModelDb initialization: root was null or invalid.");
                return;
            }

            Log.Info("[Loadout] Attached UI root after ModelDb IDs initialized.");
            ScheduleSelectScreenPreload(root);
        }
        catch (Exception e)
        {
            Log.Error($"[Loadout] Failed to attach UI after ModelDb initialization: {e}");
        }
    }

    private static void ScheduleSelectScreenPreload(NLoadoutPanelRoot root)
    {
        if (_preloaded || _preloadScheduled || _preloadAttempted)
            return;

        _preloadAttempted = true;
        _preloadScheduled = true;
        _ = PreloadSelectScreensAsync(root);
    }

    private static async Task PreloadSelectScreensAsync(NLoadoutPanelRoot root)
    {
        try
        {
            if (!IsValid(root))
                throw new InvalidOperationException("LoadoutPanelRoot became invalid before select-screen preload started.");

            NLoadoutPanel panel = root.GetNodeOrNull<NLoadoutPanel>("LoadoutPanel");
            if (!IsValid(panel))
                throw new InvalidOperationException("LoadoutPanel was not found under LoadoutPanelRoot.");

            // Let the newly attached root and panel complete their first layout frame
            // before creating the ModelDb-backed panel items.
            if (!await WaitForNextFrame(root))
                return;

            if (!panel.TryInitializeLoadoutItems())
                return;

            // Give the panel-item nodes their own frame before materializing the
            // first catalog screen, avoiding a combined initialization spike.
            if (!await WaitForNextFrame(root))
                return;

            IReadOnlyList<NLoadoutPanel.SelectScreenPreloadEntry> screens = panel.GetSelectScreensForPreload();
            IReadOnlyList<NLoadoutPanel.SelectScreenPreloadEntry> prioritizedScreens = screens
                .OrderBy(GetSelectScreenPreloadPriority)
                .ThenBy(entry => entry.Name.ToString(), StringComparer.Ordinal)
                .ToList();
            int attachedScreens = 0;
            int prewarmedScreens = 0;

            foreach (NLoadoutPanel.SelectScreenPreloadEntry entry in prioritizedScreens)
            {
                if (!IsValid(root) || !IsValid(entry.Screen))
                    continue;

                entry.Screen.Name = entry.Name;
                root.RegisterScreen(entry.Screen);
                attachedScreens++;

                // AddChild only initializes the shell while the screen is hidden.
                // Materialize its first-use viewport now, in small per-frame batches,
                // so expensive event portraits and monster creature previews are ready
                // before the player opens those tools.
                if (!await WaitForNextFrame(root))
                    return;

                await entry.Screen.PrewarmForFirstOpenAsync();
                if (!IsValid(root))
                    return;

                if (entry.Screen.IsFirstOpenPrewarmed)
                    prewarmedScreens++;

                if (!await WaitForNextFrame(root))
                    return;
            }

            if (!IsValid(root))
                return;

            root.CloseAllScreens();
            _preloaded = true;

            Log.Info(
                $"[Loadout] Registered {attachedScreens} and prewarmed {prewarmedScreens} generic select screens before first use.");
        }
        catch (Exception e)
        {
            Log.Error($"[Loadout] Failed to preload generic select screens: {e}");
        }
        finally
        {
            _preloadScheduled = false;
        }
    }

    private static int GetSelectScreenPreloadPriority(NLoadoutPanel.SelectScreenPreloadEntry entry)
    {
        string name = entry.Name.ToString();

        // These screens have the most expensive first-use view factories. Warm
        // them first so normal startup/menu time absorbs their construction.
        if (name.Contains("EventfulCompass", StringComparison.Ordinal))
            return 0;

        if (name.Contains("BottledMonster", StringComparison.Ordinal))
        {
            return name.EndsWith("_Primary", StringComparison.Ordinal) ? 1 : 2;
        }

        if (name.Contains("CardPrinter", StringComparison.Ordinal)
            || name.Contains("CardShredder", StringComparison.Ordinal)
            || name.Contains("CardModifier", StringComparison.Ordinal))
        {
            return 3;
        }

        return 4;
    }

    private static async Task<bool> WaitForNextFrame(NLoadoutPanelRoot root)
    {
        if (!IsValid(root) || !root.IsInsideTree())
            return false;

        SceneTree tree = root.GetTree();
        if (tree == null)
            return false;

        await root.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        return IsValid(root) && root.IsInsideTree();
    }

    private static bool IsValid(GodotObject instance)
    {
        return instance != null && GodotObject.IsInstanceValid(instance);
    }

    private static void RegisterServicesOnce()
    {
        if (_servicesRegistered)
            return;

        _servicesRegistered = true;

        PowerGiverStateService.Register();
        LastActionService.Register();
        CardModificationRuntime.Register();
        RelicModificationStateService.Register();
        LoadoutStorageService.Register();
        LoadoutHostSharingService.Register();
        TildeKeyStateService.Register();

        Log.Info("[Loadout] Services registered.");
    }
}
