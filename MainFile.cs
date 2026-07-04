using System.Reflection;
using Godot;
using HarmonyLib;
using Loadout.Services.Input;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Modding;

namespace Loadout;


[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Loadout"; //At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        Logger.Info("[Loadout] Build marker DLL: 2026-07-04-card-printer-native-batch-v5");

        var assembly = Assembly.GetExecutingAssembly();
        Logger.Info($"[Loadout] Assembly location: {assembly.Location}");
        Logger.Info($"[Loadout] Assembly version: {assembly.GetName().Version}");

        Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(assembly);
        LogPckBuildMarker();
        InputCompatibilityService.Register();
            
        Harmony harmony = new(ModId);
        harmony.PatchAll();

        Logger.Info("[Loadout] Harmony PatchAll complete.");

        LocMan.Load();

        Logger.Info("[Loadout] Initialize complete.");
    }
    
    private static void LogPckBuildMarker()
    {
        const string path = "res://Loadout/build_marker.txt";

        if (!FileAccess.FileExists(path))
        {
            Logger.Warn("[Loadout] PCK build marker missing.");
            return;
        }

        using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        Logger.Info("[Loadout] " + file.GetAsText().Trim());
    }
}
