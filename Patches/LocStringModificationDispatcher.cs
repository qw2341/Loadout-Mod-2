#nullable enable

namespace Loadout.Patches;

using System;
using HarmonyLib;
using Loadout.Keywords;
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
        Harmony.Patch(
            AccessTools.Method(
                typeof(LocString),
                nameof(LocString.GetFormattedText))
            ?? throw new MissingMethodException(
                typeof(LocString).FullName,
                nameof(LocString.GetFormattedText)),
            postfix: new HarmonyMethod(
                typeof(LocStringModificationDispatcher),
                nameof(FormattedPostfix)));
    }

    private static void Postfix(LocString __instance, ref string __result)
    {
        LocStringRawTextCardModificationPatch.Postfix(__instance, ref __result);
        RelicLocStringRawTextModificationPatch.Postfix(__instance, ref __result);
    }

    private static void FormattedPostfix(
        LocString __instance,
        ref string __result)
    {
        LoadoutBaseDescriptionKeywordLocStringPatch.Postfix(
            __instance,
            ref __result);
    }
}
