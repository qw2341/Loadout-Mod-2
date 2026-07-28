#nullable enable

namespace Loadout.Patches.Compatibility;

using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

/// <summary>
/// RitsuLib handles canonical catalog cards itself, but its secondary-resource
/// card UI assumes that every mutable owned card has a live PlayerCombatState.
/// Loadout's deck/card-modification previews are deliberately owned while
/// outside combat, so that assumption aborts NCard.UpdateVisuals and leaves a
/// fallback placeholder in the grid.
///
/// Patch the optional postfix itself (without linking RitsuLib) and skip only
/// that combat-only UI pass for mutable cards whose owner has no combat state.
/// </summary>
[HarmonyPatch]
internal static class RitsuLibCardPreviewCompatibilityPatch
{
    private const string PatchTypeName =
        "STS2RitsuLib.Combat.SecondaryResources.Patches."
        + "NCardUpdateVisualsSecondaryResourceCardUiPatch";

    private static MethodBase? TargetMethod()
    {
        Type? patchType = AccessTools.TypeByName(PatchTypeName);
        return patchType is null
            ? null
            : AccessTools.DeclaredMethod(
                patchType,
                "Postfix",
                [typeof(NCard), typeof(PileType), typeof(CardPreviewMode)]);
    }

    [HarmonyPrepare]
    private static bool Prepare()
    {
        return TargetMethod() is not null;
    }

    [HarmonyPrefix]
    private static bool Prefix(NCard __0)
    {
        CardModel? card = __0.Model;
        if (card is null || card.IsCanonical)
            return true;

        var owner = card.Owner;
        return owner is null || owner.PlayerCombatState is not null;
    }
}
