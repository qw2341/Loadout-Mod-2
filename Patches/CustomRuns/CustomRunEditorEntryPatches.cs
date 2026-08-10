#nullable enable

namespace Loadout.Patches.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Saves;
using Loadout.UI.CustomRuns;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuOpened))]
public static class CharacterSelectCustomRunEditorOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCharacterSelectScreen __instance)
    {
        NCustomRunEditorEntry.AttachTo(__instance, __instance.Lobby);
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuClosed))]
public static class CharacterSelectCustomRunEditorClosedPatch
{
    [HarmonyPrefix]
    public static void Prefix(NCharacterSelectScreen __instance)
    {
        NCustomRunEditorEntry.DetachFrom(__instance, __instance.Lobby);
    }
}

[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.OnSubmenuOpened))]
public static class NativeCustomRunEditorOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCustomRunScreen __instance)
    {
        NCustomRunEditorEntry.AttachTo(__instance, __instance.Lobby);
    }
}

[HarmonyPatch(typeof(StartRunLobby))]
public static class StartRunLobbyCustomRunConstructorPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredConstructors(typeof(StartRunLobby));
    }

    [HarmonyPostfix]
    public static void Postfix(StartRunLobby __instance)
    {
        CustomRunLobbyService.RegisterLobby(__instance);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
public static class StartRunLobbyCustomRunCleanupPatch
{
    [HarmonyPrefix]
    public static void Prefix(StartRunLobby __instance)
    {
        CustomRunLobbyService.UnregisterLobby(__instance);
    }
}

[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.OnSubmenuClosed))]
public static class NativeCustomRunEditorClosedPatch
{
    [HarmonyPrefix]
    public static void Prefix(NCustomRunScreen __instance)
    {
        NCustomRunEditorEntry.DetachFrom(__instance, __instance.Lobby);
    }
}

[HarmonyPatch]
public static class CharacterSelectCustomRunEmbarkPatch
{
    private static readonly HashSet<StartRunLobby> Preparing = [];
    private static readonly HashSet<StartRunLobby> Bypass = [];

    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
                   typeof(NCharacterSelectScreen),
                   "OnEmbarkPressed",
                   [typeof(NButton)])
               ?? throw new MissingMethodException(typeof(NCharacterSelectScreen).FullName, "OnEmbarkPressed");
    }

    [HarmonyPrefix]
    public static bool Prefix(NCharacterSelectScreen __instance)
    {
        return TryIntercept(__instance, __instance.Lobby, honorTutorialPrompt: true);
    }

    internal static bool TryIntercept(
        Control screen,
        StartRunLobby lobby,
        bool honorTutorialPrompt)
    {
        if (Bypass.Remove(lobby))
            return true;

        if (CustomRunLobbyService.GetLoadedDefinition(lobby) is null
            || lobby.NetService.Type == NetGameType.Client
            || (honorTutorialPrompt && !SaveManager.Instance.SeenFtue("accept_tutorials_ftue")))
        {
            return true;
        }

        if (!Preparing.Add(lobby))
            return false;

        NConfirmButton? confirm = screen.GetNodeOrNull<NConfirmButton>("ConfirmButton");
        confirm?.Disable();
        TaskHelper.RunSafely(PrepareAndEmbarkAsync(screen, lobby, confirm));
        return false;
    }

    private static async Task PrepareAndEmbarkAsync(
        Control screen,
        StartRunLobby lobby,
        NConfirmButton? confirm)
    {
        bool handedOffToNativeEmbark = false;
        try
        {
            CustomRunDefinition? loaded = CustomRunLobbyService.GetLoadedDefinition(lobby);
            if (loaded is null)
                return;

            CustomRunDefinition effective = CustomRunDefinitionResolver.WithEnabledPermanentRules(loaded);
            CustomRunCompileResult compiled = CustomRunCompiler.Compile(effective, lobby);
            if (!compiled.IsValid || compiled.Snapshot is null)
            {
                CustomRunValidationIssue? issue = compiled.Issues
                    .FirstOrDefault(candidate => candidate.Severity == CustomRunValidationSeverity.Error);
                ShowError(screen, issue is null
                    ? "This Custom Run could not be compiled."
                    : $"{issue.Section}: {issue.Message}");
                return;
            }

            CustomRunPreparationResult result =
                await CustomRunLobbyService.PrepareHostRunAsync(lobby, compiled.Snapshot);
            if (!result.Succeeded)
            {
                ShowError(screen, result.Error);
                return;
            }

            if (!GodotObject.IsInstanceValid(screen)
                || confirm is null
                || !GodotObject.IsInstanceValid(confirm))
            {
                CustomRunLobbyService.CancelPreparedRun(lobby);
                return;
            }

            Bypass.Add(lobby);
            confirm.Enable();
            confirm.ForceClick();
            handedOffToNativeEmbark = true;
        }
        finally
        {
            Preparing.Remove(lobby);
            if (confirm is not null
                && GodotObject.IsInstanceValid(confirm)
                && !handedOffToNativeEmbark)
            {
                confirm.Enable();
            }
        }
    }

    private static void ShowError(Control screen, string error)
    {
        screen.GetNodeOrNull<NCustomRunCharacterSelectOverlay>(NCustomRunEditorEntry.OverlayNodeName)
            ?.ShowError(error);
    }
}

[HarmonyPatch]
public static class NativeCustomRunLoadedEmbarkPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
                   typeof(NCustomRunScreen),
                   "OnEmbarkPressed",
                   [typeof(NButton)])
               ?? throw new MissingMethodException(typeof(NCustomRunScreen).FullName, "OnEmbarkPressed");
    }

    [HarmonyPrefix]
    public static bool Prefix(NCustomRunScreen __instance)
    {
        return CharacterSelectCustomRunEmbarkPatch.TryIntercept(
            __instance,
            __instance.Lobby,
            honorTutorialPrompt: false);
    }
}
