#nullable enable

namespace Loadout.Patches.Loadouts;

using System.Reflection;
using Godot;
using HarmonyLib;
using Loadout.Services.Loadouts;
using Loadout.UI;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Runs;

[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen._Ready))]
public static class DeckViewScreenLoadoutPanelReadyPatch
{
    private static readonly FieldInfo? PlayerField = AccessTools.Field(typeof(NDeckViewScreen), "_player");

    [HarmonyPostfix]
    public static void Postfix(NDeckViewScreen __instance)
    {
        Player? player = PlayerField?.GetValue(__instance) as Player;
        NDeckLoadoutPanel.AttachTo(__instance, player);
    }
}

[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen._ExitTree))]
public static class DeckViewScreenLoadoutPanelExitPatch
{
    [HarmonyPrefix]
    public static void Prefix(NDeckViewScreen __instance)
    {
        NDeckLoadoutPanel.DetachFrom(__instance);
    }
}

[HarmonyPatch(typeof(StartRunLobby))]
public static class StartRunLobbyLoadoutSharingConstructorPatch
{
    public static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredConstructors(typeof(StartRunLobby));
    }

    [HarmonyPostfix]
    public static void Postfix(StartRunLobby __instance)
    {
        LoadoutHostSharingService.RegisterLobby(__instance);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
public static class StartRunLobbyLoadoutSharingCleanUpPatch
{
    [HarmonyPrefix]
    public static void Prefix(StartRunLobby __instance, bool disconnectSession)
    {
        LoadoutHostSharingService.UnregisterLobby(__instance, disconnectSession);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class RunManagerLaunchLoadoutSharingPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        LoadoutHostSharingService.OnRunLaunched();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class RunManagerCleanUpLoadoutSharingPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        LoadoutHostSharingService.OnRunCleaningUp();
    }
}
