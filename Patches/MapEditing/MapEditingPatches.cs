#nullable enable

namespace Loadout.Patches.MapEditing;

using System;
using System.Reflection;
using BaseLib.Patches.Saves;
using Godot;
using HarmonyLib;
using Loadout.Services.MapEditing;
using Loadout.UI.MapEditing;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

[HarmonyPatch]
public static class MapEditingExtendedSavePatch
{
    private const string EmbeddedSaveKey = "Loadout.map_editor.archive_v2";
    private static bool _registered;

    public static MethodBase TargetMethod()
    {
        Type type = AccessTools.TypeByName("BaseLib.Patches.PostModInitPatch")
                    ?? throw new TypeLoadException("BaseLib.Patches.PostModInitPatch");
        return AccessTools.Method(type, "LatePostInit")
               ?? throw new MissingMethodException(type.FullName, "LatePostInit");
    }

    [HarmonyPrefix]
    public static void Prefix()
    {
        if (_registered)
            return;

        _registered = true;
        ExtendedSaveHandlers<IRunState, SerializableRun>.RegisterSave<RunState, string>(
            EmbeddedSaveKey,
            MapEditingService.GetSerializedRunState,
            MapEditingService.LoadSerializedRunState,
            static (payload, writer) => writer.WriteString(payload ?? string.Empty),
            static reader => reader.ReadString());
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.ToSave))]
public static class MapEditingNativeMapSavePatch
{
    [HarmonyPostfix]
    public static void Postfix(RunManager __instance, SerializableRun __result)
        => MapEditingService.WriteMapsToNativeSave(__instance.DebugOnlyGetState()!, __result);
}

[HarmonyPatch(typeof(NMapScreen), "_Ready")]
public static class MapEditingMapReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMapScreen __instance)
    {
        MapEditingUiService.Attach(__instance);
        MapEditingUiService.OnMapSet(__instance);
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
public static class MapEditingMapSetPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMapScreen __instance) => MapEditingUiService.OnMapSet(__instance);
}

[HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl._GuiInput))]
public static class MapEditingClickableInputPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(NClickableControl __instance, InputEvent inputEvent)
        => !MapEditingUiService.HandleClickableInput(__instance, inputEvent);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._GuiInput))]
public static class MapEditingScreenInputPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(NMapScreen __instance, InputEvent inputEvent)
        => !MapEditingUiService.HandleScreenInput(__instance, inputEvent);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class MapEditingRunLaunchPatch
{
    [HarmonyPrefix]
    public static void Prefix() => MapEditingService.PrepareRunLaunch();

    [HarmonyPostfix]
    public static void Postfix() => MapEditingService.OnRunLaunched();
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class MapEditingRunCleanupPatch
{
    [HarmonyPrefix]
    public static void Prefix() => MapEditingService.OnRunCleaningUp();
}

[HarmonyPatch(typeof(NNormalMapPoint), "_Ready")]
public static class MapEditingOrdinaryBossIconPatch
{
    private static readonly AccessTools.FieldRef<NNormalMapPoint, TextureRect> IconField =
        AccessTools.FieldRefAccess<NNormalMapPoint, TextureRect>("_icon");
    private static readonly AccessTools.FieldRef<NNormalMapPoint, TextureRect> OutlineField =
        AccessTools.FieldRefAccess<NNormalMapPoint, TextureRect>("_outline");

    [HarmonyPostfix]
    public static void Postfix(NNormalMapPoint __instance)
    {
        if (__instance.Point.PointType != MapPointType.Boss)
            return;

        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is null || !MapEditingService.IsCurrentActEdited(runState))
                return;
            string? iconPath = ImageHelper.GetRoomIconPath(
                MapPointType.Boss,
                RoomType.Boss,
                runState.Act.BossEncounter.Id);
            string? outlinePath = ImageHelper.GetRoomIconOutlinePath(
                MapPointType.Boss,
                RoomType.Boss,
                runState.Act.BossEncounter.Id);
            if (!string.IsNullOrWhiteSpace(iconPath))
                IconField(__instance).Texture = ResourceLoader.Load<Texture2D>(iconPath);
            if (!string.IsNullOrWhiteSpace(outlinePath))
                OutlineField(__instance).Texture = ResourceLoader.Load<Texture2D>(outlinePath);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout map editor: could not render ordinary boss node. {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(NNormalMapPoint), "UpdateIcon")]
public static class MapEditingOrdinaryBossNativeIconGuardPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NNormalMapPoint __instance)
    {
        if (__instance.Point.PointType != MapPointType.Boss)
            return true;

        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            return runState is null || !MapEditingService.IsCurrentActEdited(runState);
        }
        catch
        {
            return true;
        }
    }
}
