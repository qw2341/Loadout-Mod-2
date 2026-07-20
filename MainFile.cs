using System;
using System.Reflection;
using System.Threading;

using BaseLib.Config;
using Godot.Bridge;
using HarmonyLib;
using Loadout.Config;
using Loadout.Patches;
using Loadout.Services.Compatibility;
using Loadout.Services.Input;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MethodInfo = System.Reflection.MethodInfo;

namespace Loadout;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "Loadout";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    private static readonly Harmony BootstrapHarmony =
        new("Loadout.Bootstrap");

    private static readonly Harmony RuntimeHarmony =
        new(ModId);

    private static int _lateInitializationStarted;

    public static void Initialize()
    {
        Logger.Info("[Loadout] Beginning early bootstrap.");

        // Keep this early only if the Sentry interception is required before
        // the game's essential initialization.
        SentryStartupCrashInterceptor.Install(BootstrapHarmony);

        MethodInfo? locInitialize =
            AccessTools.DeclaredMethod(typeof(LocManager), "Initialize");

        if (locInitialize is null)
        {
            throw new MissingMethodException(
                typeof(LocManager).FullName,
                "Initialize");
        }

        BootstrapHarmony.Patch(
            locInitialize,
            postfix: new HarmonyMethod(
                typeof(MainFile),
                nameof(AfterLocManagerInitialized)));

        Logger.Info("[Loadout] Waiting for game localization initialization.");
    }

    private static void AfterLocManagerInitialized()
    {
        if (Interlocked.Exchange(
                ref _lateInitializationStarted,
                1) != 0)
        {
            return;
        }

        Logger.Info(
            "[Loadout] LocManager is ready; beginning full initialization.");

        Assembly assembly = typeof(MainFile).Assembly;

        RunInitializationStep(
            "Godot script registration",
            () => ScriptManagerBridge.LookupScriptsInAssembly(assembly));

        RunInitializationStep(
            "PCK marker",
            LogPckBuildMarker);

        RunInitializationStep(
            "version compatibility",
            Sts2Compatibility.Initialize);

        RunInitializationStep(
            "input registration",
            InputCompatibilityService.Register);

        RunInitializationStep(
            "version-sensitive patches",
            () => VersionSensitivePatchInstaller.Install(RuntimeHarmony));

        RunInitializationStep(
            "Harmony PatchAll",
            () => RuntimeHarmony.PatchAll(assembly));

        RunInitializationStep(
            "Loadout localization",
            LocMan.Load);

        RunInitializationStep(
            "configuration registration",
            () => ModConfigRegistry.Register(
                ModId,
                new LoadoutModConfig()));

        Logger.Info("[Loadout] Full initialization complete.");
    }

    private static void RunInitializationStep(
        string name,
        Action action)
    {
        Logger.Info($"[Loadout] Starting: {name}");
        action();
        Logger.Info($"[Loadout] Completed: {name}");
    }

    private static void LogPckBuildMarker()
    {
        const string path = "res://Loadout/build_marker.txt";

        if (!Godot.FileAccess.FileExists(path))
        {
            Logger.Warn("[Loadout] PCK build marker missing.");
            return;
        }

        using Godot.FileAccess file =
            Godot.FileAccess.Open(
                path,
                Godot.FileAccess.ModeFlags.Read);

        Logger.Info(
            "[Loadout] " + file.GetAsText().Trim());
    }
}