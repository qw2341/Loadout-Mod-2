#nullable enable

namespace Loadout.Patches.Relics;

using System;
using System.Reflection;
using HarmonyLib;
using Loadout.Patches;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;

internal static class RelicModificationDynamicPatches
{
    private const string HarmonyId = "Loadout.RelicModification.Dynamic";
    private static readonly Harmony Harmony = new(HarmonyId);
    private static readonly object Gate = new();
    private static bool _rarityInstalled;
    private static bool _textInstalled;
    private static bool _neverMeltInstalled;
    private static bool _neverUsedInstalled;

    public static void Enable(bool rarity, bool customText, bool neverMelt, bool neverUsed)
    {
        lock (Gate)
        {
            if (rarity && !_rarityInstalled)
            {
                foreach (MethodBase target in RelicRarityModificationPatch.TargetMethods())
                {
                    Harmony.Patch(
                        target,
                        postfix: new HarmonyMethod(
                            typeof(RelicRarityModificationPatch),
                            nameof(RelicRarityModificationPatch.Postfix)));
                }

                _rarityInstalled = true;
            }

            if (customText && !_textInstalled)
            {
                PatchLocStringGetter(nameof(RelicModel.Title), typeof(RelicTitleModificationPatch));
                PatchLocStringGetter(nameof(RelicModel.Flavor), typeof(RelicFlavorModificationPatch));
                PatchLocStringGetter(nameof(RelicModel.DynamicDescription), typeof(RelicDescriptionModificationPatch));
                LocStringModificationDispatcher.EnsureInstalled();
                _textInstalled = true;
            }

            if (neverMelt && !_neverMeltInstalled)
            {
                Harmony.Patch(
                    AccessTools.Method(typeof(RelicCmd), nameof(RelicCmd.Melt), [typeof(RelicModel)])
                    ?? throw new MissingMethodException(typeof(RelicCmd).FullName, nameof(RelicCmd.Melt)),
                    prefix: new HarmonyMethod(
                        typeof(RelicMeltModificationPatch),
                        nameof(RelicMeltModificationPatch.Prefix)));
                _neverMeltInstalled = true;
            }

            if (neverUsed && !_neverUsedInstalled)
            {
                foreach (MethodBase target in RelicIsUsedUpModificationPatch.TargetMethods())
                {
                    Harmony.Patch(
                        target,
                        postfix: new HarmonyMethod(
                            typeof(RelicIsUsedUpModificationPatch),
                            nameof(RelicIsUsedUpModificationPatch.Postfix)));
                }

                Harmony.Patch(
                    AccessTools.PropertySetter(typeof(RelicModel), nameof(RelicModel.Status))
                    ?? throw new MissingMethodException(typeof(RelicModel).FullName, $"set_{nameof(RelicModel.Status)}"),
                    prefix: new HarmonyMethod(
                        typeof(RelicStatusModificationPatch),
                        nameof(RelicStatusModificationPatch.Prefix)));
                _neverUsedInstalled = true;
            }
        }
    }

    private static void PatchLocStringGetter(string propertyName, Type patchType)
    {
        Harmony.Patch(
            AccessTools.PropertyGetter(typeof(RelicModel), propertyName)
            ?? throw new MissingMethodException(typeof(RelicModel).FullName, $"get_{propertyName}"),
            prefix: new HarmonyMethod(patchType, "Prefix"),
            postfix: new HarmonyMethod(patchType, "Postfix"),
            finalizer: new HarmonyMethod(patchType, "Finalizer"));
    }
}
