using System;
using System.Linq;
using HarmonyLib;
using Loadout.UI;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Loadout.Patches.Core;

[HarmonyPatch(typeof(EventConsoleCmd), nameof(EventConsoleCmd.Process))]
public static class EventConsoleCmdProcessPatch
{
    [HarmonyPrefix]
    public static void Prefix(string[] args)
    {
	    if (!IsValidEventCommand(args))
		    return;

	    ConsoleNavigationScreenCloser.CloseRunNavigationScreens();
    }

    private static bool IsValidEventCommand(string[] args)
    {
	    if (args.Length == 0 || !RunManager.Instance.IsInProgress)
		    return false;

	    string eventName = args[0].ToUpperInvariant();
	    return ModelDb.AllEvents.Concat(ModelDb.AllAncients)
		    .Any(eventModel => eventModel.Id.Entry == eventName);
    }
}

[HarmonyPatch(typeof(RoomConsoleCmd), nameof(RoomConsoleCmd.Process))]
public static class RoomConsoleCmdProcessPatch
{
    [HarmonyPrefix]
    public static void Prefix(string[] args)
    {
	    if (args.Length == 0
	        || !RunManager.Instance.IsInProgress
	        || !Enum.TryParse(args[0], ignoreCase: true, out RoomType _))
		    return;

	    ConsoleNavigationScreenCloser.CloseRunNavigationScreens();
    }
}

public static class ConsoleNavigationScreenCloser
{
    public static void CloseRunNavigationScreens()
    {
	    NLoadoutPanelRoot.CloseBlockingRunScreens();
	    NLoadoutPanelRoot.Instance?.CloseAllScreens();
    }
}
