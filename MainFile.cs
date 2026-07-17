using System.Reflection;
using BaseLib.Config;
using Godot;
using HarmonyLib;
using Loadout.Config;
using Loadout.Patches;
using Loadout.Services.Compatibility;
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
    
    private static readonly Harmony Harmony = new("loadout");

    public static void Initialize()
    {
        Logger.Info("[Loadout] Intercepting Sentry to prevent crashing when launching from workshop.");
        SentryStartupCrashInterceptor.Install(Harmony);
        // Logger.Info("[Loadout] Build marker DLL: 2026-07-04-card-printer-native-batch-v5");

        var assembly = Assembly.GetExecutingAssembly();
        Logger.Info($"[Loadout] Assembly location: {assembly.Location}");
        Logger.Info($"[Loadout] Assembly version: {assembly.GetName().Version}");

        Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(assembly);
        LogPckBuildMarker();
        Sts2Compatibility.Initialize();
        InputCompatibilityService.Register();
             
        Harmony harmony = new(ModId);
        VersionSensitivePatchInstaller.Install(harmony);
        harmony.PatchAll();

        Logger.Info("[Loadout] Harmony PatchAll complete.");

        LocMan.Load();
        ModConfigRegistry.Register(ModId, new LoadoutModConfig());

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
