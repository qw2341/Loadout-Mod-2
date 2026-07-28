using HarmonyLib;
using Loadout.UI;
using MegaCrit.Sts2.Core.Runs;

namespace Loadout.Patches.Core;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class RunManagerCleanUpPatch
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		NLoadoutPanelRoot.Instance?.ClearNativeFeedback();
	}

	[HarmonyPostfix]
	private static void Postfix()
	{
		NLoadoutPanel.NotifyRunCleanedUp();
	}
}
