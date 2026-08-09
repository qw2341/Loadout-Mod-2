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
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Logging;

namespace Loadout.Patches.Core;

[HarmonyPatch(typeof(PreloadManager), nameof(PreloadManager.LoadMainMenuEssentials))]
public static class UIAttach
{
    private static readonly object StartupLock = new();

    private static bool _servicesRegistered;
    private static Task _startupTask;

    [HarmonyPostfix]
    private static void Postfix(ref Task __result)
    {
        __result = CompleteStartupLoadingAsync(__result);
    }

    private static async Task CompleteStartupLoadingAsync(Task nativeLoadingTask)
    {
        await nativeLoadingTask;
        await GetOrStartStartupTask();
    }

    private static Task GetOrStartStartupTask()
    {
        lock (StartupLock)
        {
            return _startupTask ??= InitializeAndPrewarmAsync();
        }
    }

    private static async Task InitializeAndPrewarmAsync()
    {
        NLoadoutPanelRoot root = null;
        bool completed = false;

        try
        {
            Log.Info("[Loadout] Main menu essentials loaded. Initializing UI and prewarming select screens.");
            RegisterServicesOnce();

            SceneTree tree = Engine.GetMainLoop() as SceneTree;

            if (tree == null)
            {
                Log.Error("[Loadout] Failed to initialize startup UI: SceneTree was null.");
                return;
            }

            root = NLoadoutPanelRoot.GetOrAttach(tree);

            if (!IsValid(root))
            {
                Log.Error("[Loadout] Failed to initialize startup UI: root was null or invalid.");
                return;
            }

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

                try
                {
                    entry.Screen.Name = entry.Name;
                    root.RegisterScreen(entry.Screen);
                    attachedScreens++;

                    if (!await WaitForNextFrame(root))
                        return;

                    await entry.Screen.PrewarmForFirstOpenAsync();
                    if (!IsValid(root))
                        return;

                    if (entry.Screen.IsFirstOpenPrewarmed)
                        prewarmedScreens++;
                }
                catch (Exception e)
                {
                    Log.Error($"[Loadout] Failed to register or prewarm select screen '{entry.Name}': {e}");
                }

                if (!await WaitForNextFrame(root))
                    return;
            }

            if (!IsValid(root))
                return;

            Log.Info(
                $"[Loadout] Registered {attachedScreens} and prewarmed {prewarmedScreens} generic select screens before first use.");
            completed = true;
        }
        catch (Exception e)
        {
            Log.Error($"[Loadout] Failed to initialize startup UI and prewarm select screens: {e}");
        }
        finally
        {
            if (IsValid(root))
            {
                try
                {
                    root.CloseAllScreens();
                }
                catch (Exception e)
                {
                    completed = false;
                    Log.Error($"[Loadout] Failed to close startup select screens: {e}");
                }
            }
        }

        if (completed)
            Log.Info("[Loadout] Startup UI initialization and select-screen prewarm complete.");
    }

    private static int GetSelectScreenPreloadPriority(NLoadoutPanel.SelectScreenPreloadEntry entry)
    {
        string name = entry.Name.ToString();

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
