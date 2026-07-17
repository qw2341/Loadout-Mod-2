#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using HarmonyLib;
using Loadout.Patches.Cards;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

/// <summary>
/// Installs optional per-copy hooks only when temporary state can exist.
/// Numeric permanent-only runs do not install any clone/serialization hook.
/// </summary>
internal static class CardModificationDynamicPatches
{
    private const string HarmonyId = "Loadout.CardModification.Dynamic";
    private static readonly Harmony Harmony = new(HarmonyId);
    private static bool _temporaryEnabled;
    private static bool _textEnabled;
    private static bool _portraitEnabled;

    public static void EnableTemporaryPatches()
    {
        if (_temporaryEnabled) return;
        _temporaryEnabled = true;
        PatchPostfix(typeof(AbstractModel), nameof(AbstractModel.MutableClone),
            typeof(CardModelMutableCloneCardModificationPatch), nameof(CardModelMutableCloneCardModificationPatch.Postfix));
        PatchPostfix(typeof(AbstractModel), nameof(AbstractModel.ClonePreservingMutability),
            typeof(CardModelMutableCloneCardModificationPatch), nameof(CardModelMutableCloneCardModificationPatch.Postfix));
        PatchPostfix(typeof(CardModel), nameof(CardModel.ToSerializable),
            typeof(CardModelToSerializableCardModificationPatch), nameof(CardModelToSerializableCardModificationPatch.Postfix));
        PatchPrefixFinalizer(typeof(ChecksumTracker), "ObtainAndTrackChecksum",
            typeof(ChecksumTrackerCardModificationSerializationPatch),
            nameof(ChecksumTrackerCardModificationSerializationPatch.Prefix),
            nameof(ChecksumTrackerCardModificationSerializationPatch.Finalizer));
        PatchPostfix(typeof(CardCmd), nameof(CardCmd.Downgrade),
            typeof(CardCmdDowngradeCardModificationPatch), nameof(CardCmdDowngradeCardModificationPatch.Postfix));
        PatchPrefixPostfix(
            AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Upgrade),
                [typeof(System.Collections.Generic.IEnumerable<CardModel>), typeof(CardPreviewStyle)])
            ?? throw new MissingMethodException(typeof(CardCmd).FullName, nameof(CardCmd.Upgrade)),
            typeof(CardCmdUpgradeCardModificationPatch),
            nameof(CardCmdUpgradeCardModificationPatch.Prefix),
            nameof(CardCmdUpgradeCardModificationPatch.Postfix));
    }

    public static void EnableTextPatches()
    {
        if (_textEnabled) return;
        _textEnabled = true;
        PatchPrefixFinalizer(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Title))!,
            typeof(CardModelTitleCardModificationPatch), nameof(CardModelTitleCardModificationPatch.Prefix),
            nameof(CardModelTitleCardModificationPatch.Finalizer));
        PatchPrefixFinalizer(AccessTools.Method(typeof(CardModel), nameof(CardModel.GetDescriptionForPile),
                [typeof(PileType), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)])!,
            typeof(CardModelDescriptionCardModificationPatch), nameof(CardModelDescriptionCardModificationPatch.Prefix),
            nameof(CardModelDescriptionCardModificationPatch.Finalizer));
        PatchPrefixFinalizer(AccessTools.Method(typeof(CardModel), nameof(CardModel.GetDescriptionForUpgradePreview))!,
            typeof(CardModelUpgradeDescriptionCardModificationPatch), nameof(CardModelUpgradeDescriptionCardModificationPatch.Prefix),
            nameof(CardModelUpgradeDescriptionCardModificationPatch.Finalizer));
        PatchPostfix(typeof(LocString), nameof(LocString.GetRawText),
            typeof(LocStringRawTextCardModificationPatch), nameof(LocStringRawTextCardModificationPatch.Postfix));
    }

    public static void EnablePortraitPatches()
    {
        if (_portraitEnabled) return;
        _portraitEnabled = true;
        PatchPostfix(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.PortraitPath))!,
            typeof(CardModelPortraitPathCardModificationPatch), nameof(CardModelPortraitPathCardModificationPatch.Postfix));
        PatchPostfix(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.BetaPortraitPath))!,
            typeof(CardModelBetaPortraitPathCardModificationPatch), nameof(CardModelBetaPortraitPathCardModificationPatch.Postfix));
    }

    public static void ResetRunPatches()
    {
        ClearAll();
        if (PermanentCardModificationStore.HasAnyCustomText) EnableTextPatches();
        if (PermanentCardModificationStore.HasAnyPortraitOverrides) EnablePortraitPatches();
    }

    public static void ClearAll()
    {
        Harmony.UnpatchAll(HarmonyId);
        _temporaryEnabled = false;
        _textEnabled = false;
        _portraitEnabled = false;
    }

    private static void PatchPostfix(Type targetType, string targetName, Type patchType, string patchName) =>
        PatchPostfix(AccessTools.Method(targetType, targetName)
                     ?? throw new MissingMethodException(targetType.FullName, targetName), patchType, patchName);

    private static void PatchPostfix(System.Reflection.MethodBase target, Type patchType, string patchName) =>
        Harmony.Patch(target, postfix: new HarmonyMethod(patchType, patchName));

    private static void PatchPrefixFinalizer(
        Type targetType,
        string targetName,
        Type patchType,
        string prefix,
        string finalizer) =>
        PatchPrefixFinalizer(AccessTools.Method(targetType, targetName)
                             ?? throw new MissingMethodException(targetType.FullName, targetName),
            patchType, prefix, finalizer);

    private static void PatchPrefixFinalizer(
        System.Reflection.MethodBase target,
        Type patchType,
        string prefix,
        string finalizer) =>
        Harmony.Patch(target,
            prefix: new HarmonyMethod(patchType, prefix),
            finalizer: new HarmonyMethod(patchType, finalizer));

    private static void PatchPrefixPostfix(
        System.Reflection.MethodBase target,
        Type patchType,
        string prefix,
        string postfix) =>
        Harmony.Patch(target,
            prefix: new HarmonyMethod(patchType, prefix),
            postfix: new HarmonyMethod(patchType, postfix));
}

internal static class CardModificationPermanentPatches
{
    private const string HarmonyId = "Loadout.CardModification.Permanent";
    private static readonly Harmony Harmony = new(HarmonyId);
    private static int _configuration;

    public static void Configure(bool creationResidual, bool canonicalStarGetter)
    {
        int next = (creationResidual ? 1 : 0) | (canonicalStarGetter ? 2 : 0);
        if (next == _configuration)
            return;

        Harmony.UnpatchAll(HarmonyId);
        _configuration = next;
        if (creationResidual)
        {
            Harmony.Patch(
                AccessTools.Method(typeof(CardModel), nameof(CardModel.ToMutable))
                ?? throw new MissingMethodException(typeof(CardModel).FullName, nameof(CardModel.ToMutable)),
                postfix: new HarmonyMethod(typeof(CardModelToMutableCardModificationPatch), nameof(CardModelToMutableCardModificationPatch.Postfix)));
        }
        if (canonicalStarGetter)
        {
            Harmony.Patch(
                AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.BaseStarCost))
                ?? throw new MissingMethodException(typeof(CardModel).FullName, $"get_{nameof(CardModel.BaseStarCost)}"),
                postfix: new HarmonyMethod(typeof(CardModelBaseStarCostCardModificationPatch), nameof(CardModelBaseStarCostCardModificationPatch.Postfix)));
            Harmony.Patch(
                AccessTools.Method(typeof(CardModel), "DowngradeInternal")
                ?? throw new MissingMethodException(typeof(CardModel).FullName, "DowngradeInternal"),
                postfix: new HarmonyMethod(typeof(CardModelDowngradePermanentStarCostPatch), nameof(CardModelDowngradePermanentStarCostPatch.Postfix)));
        }
    }

    public static void Reset()
    {
        Harmony.UnpatchAll(HarmonyId);
        _configuration = 0;
    }
}
