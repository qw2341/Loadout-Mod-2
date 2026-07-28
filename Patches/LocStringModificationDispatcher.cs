#nullable enable

namespace Loadout.Patches;

using System;
using HarmonyLib;
using Loadout.Patches.Cards;
using Loadout.Patches.Relics;
using MegaCrit.Sts2.Core.Localization;

internal static class LocStringModificationDispatcher
{
    private const string HarmonyId = "Loadout.LocStringModificationDispatcher";
    private static readonly Harmony Harmony = new(HarmonyId);
    private static bool _installed;

    public static void EnsureInstalled()
    {
        if (_installed)
            return;

        _installed = true;
        Harmony.Patch(
            AccessTools.Method(typeof(LocString), nameof(LocString.GetRawText))
            ?? throw new MissingMethodException(typeof(LocString).FullName, nameof(LocString.GetRawText)),
            postfix: new HarmonyMethod(typeof(LocStringModificationDispatcher), nameof(Postfix)));
    }

    private static void Postfix(LocString __instance, ref string __result)
    {
        LocStringRawTextCardModificationPatch.Postfix(__instance, ref __result);
        RelicLocStringRawTextModificationPatch.Postfix(__instance, ref __result);
    }
}
