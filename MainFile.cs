using System.Reflection;
using Godot;
using HarmonyLib;
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
        var assembly = Assembly.GetExecutingAssembly();
        Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(assembly);

        Harmony harmony = new(ModId);
        harmony.PatchAll();
        
        LocMan.Load();
    }
}
