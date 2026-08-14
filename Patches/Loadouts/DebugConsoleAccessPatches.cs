#nullable enable

namespace Loadout.Patches.Loadouts;

using Godot;
using HarmonyLib;
using Loadout.Services.Loadouts;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Nodes.Debug;

public static class DebugConsoleAccessGate
{
    private static NDevConsole? _console;

    public static void Attach(NDevConsole console)
    {
        _console = console;
        LoadoutPanelAccessService.DebugConsoleAccessChanged -= Refresh;
        LoadoutPanelAccessService.DebugConsoleAccessChanged += Refresh;
        Refresh();
    }

    public static void Detach(NDevConsole console)
    {
        if (!ReferenceEquals(_console, console))
            return;

        LoadoutPanelAccessService.DebugConsoleAccessChanged -= Refresh;
        _console = null;
    }

    public static void Refresh()
    {
        if (_console is null
            || !GodotObject.IsInstanceValid(_console)
            || LoadoutPanelAccessService.CanLocalPlayerUseDebugConsole())
        {
            return;
        }

        _console.HideConsole();
    }
}

[HarmonyPatch(typeof(NDevConsole), nameof(NDevConsole._Ready))]
public static class DebugConsoleAccessReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NDevConsole __instance)
    {
        DebugConsoleAccessGate.Attach(__instance);
    }
}

[HarmonyPatch(typeof(NDevConsole), nameof(NDevConsole._ExitTree))]
public static class DebugConsoleAccessExitPatch
{
    [HarmonyPrefix]
    public static void Prefix(NDevConsole __instance)
    {
        DebugConsoleAccessGate.Detach(__instance);
    }
}

[HarmonyPatch(typeof(NDevConsole), nameof(NDevConsole.ShowConsole))]
public static class DebugConsoleAccessShowPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        return LoadoutPanelAccessService.CanLocalPlayerUseDebugConsole();
    }
}

[HarmonyPatch(typeof(DevConsole), nameof(DevConsole.ProcessCommand), [typeof(string)])]
public static class DebugConsoleAccessCommandPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref CmdResult __result)
    {
        if (LoadoutPanelAccessService.CanLocalPlayerUseDebugConsole())
            return true;

        __result = new CmdResult(success: false, "Debug console access is disabled by the host.");
        return false;
    }
}
