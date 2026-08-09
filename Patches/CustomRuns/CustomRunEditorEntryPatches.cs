#nullable enable

namespace Loadout.Patches.CustomRuns;

using HarmonyLib;
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

[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.OnSubmenuClosed))]
public static class NativeCustomRunEditorClosedPatch
{
    [HarmonyPrefix]
    public static void Prefix(NCustomRunScreen __instance)
    {
        NCustomRunEditorEntry.DetachFrom(__instance, __instance.Lobby);
    }
}
