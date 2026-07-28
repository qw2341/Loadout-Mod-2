#nullable enable

namespace Loadout.Patches.Compatibility;

using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

/// <summary>
/// RitsuLib's secondary-resource card UI currently assumes that every owned
/// card has a live PlayerCombatState. Loadout's deck/card-modification previews
/// are deliberately owned while outside combat, so that assumption aborts
/// NCard.UpdateVisuals and leaves a fallback placeholder in the grid.
///
/// Patch the optional postfix itself (without linking RitsuLib) and skip only
/// that combat-only UI pass when its card owner has no combat state.
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
        return card?.Owner is null
               || card.Owner.PlayerCombatState is not null;
    }
}
