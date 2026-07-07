#nullable enable

namespace Loadout.Patches.Loadouts;

using System.Reflection;
using HarmonyLib;
using Loadout.UI;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuOpened))]
public static class CharacterSelectLoadoutAccessToggleOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCharacterSelectScreen __instance)
    {
        NHostLobbyLoadoutAccessToggle.AttachTo(__instance, __instance.Lobby);
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuClosed))]
public static class CharacterSelectLoadoutAccessToggleClosedPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCharacterSelectScreen __instance)
    {
        NHostLobbyLoadoutAccessToggle.DetachFrom(__instance);
    }
}

[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.OnSubmenuOpened))]
public static class CustomRunLoadoutAccessToggleOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCustomRunScreen __instance)
    {
        NHostLobbyLoadoutAccessToggle.AttachTo(__instance, __instance.Lobby);
    }
}

[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.OnSubmenuClosed))]
public static class CustomRunLoadoutAccessToggleClosedPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCustomRunScreen __instance)
    {
        NHostLobbyLoadoutAccessToggle.DetachFrom(__instance);
    }
}

[HarmonyPatch(typeof(NDailyRunScreen), "AfterLobbyInitialized")]
public static class DailyRunLoadoutAccessToggleInitializedPatch
{
    private static readonly FieldInfo? LobbyField = AccessTools.Field(typeof(NDailyRunScreen), "_lobby");

    [HarmonyPostfix]
    public static void Postfix(NDailyRunScreen __instance)
    {
        NHostLobbyLoadoutAccessToggle.AttachTo(__instance, LobbyField?.GetValue(__instance) as StartRunLobby);
    }
}

[HarmonyPatch(typeof(NDailyRunScreen), nameof(NDailyRunScreen.OnSubmenuClosed))]
public static class DailyRunLoadoutAccessToggleClosedPatch
{
    [HarmonyPostfix]
    public static void Postfix(NDailyRunScreen __instance)
    {
        NHostLobbyLoadoutAccessToggle.DetachFrom(__instance);
    }
}
