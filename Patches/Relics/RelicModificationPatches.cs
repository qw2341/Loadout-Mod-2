#nullable enable

namespace Loadout.Patches.Relics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.RelicModification;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;

[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.MutableClone))]
public static class RelicMutableCloneModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(AbstractModel __instance, AbstractModel __result)
    {
        if (__instance is RelicModel source && __result is RelicModel clone)
            RelicModificationStateService.CarryStateToClone(source, clone);
    }
}

[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.ClonePreservingMutability))]
public static class RelicClonePreservingMutabilityModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(AbstractModel __instance, AbstractModel __result)
    {
        if (__instance is RelicModel source && __result is RelicModel clone)
            RelicModificationStateService.CarryStateToClone(source, clone);
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.FromSerializable))]
public static class RelicFromSerializableModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(RelicModel __result)
    {
        RelicModificationStateService.ApplyDeserializedState(__result);
    }
}

[HarmonyPatch(typeof(NInspectRelicScreen), nameof(NInspectRelicScreen.Open))]
public static class InspectRelicPermanentDisplayPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref IReadOnlyList<RelicModel> relics, ref RelicModel relic)
    {
        IReadOnlyList<RelicModel> originalRelics = relics;
        RelicModel selectedRelic = relic;
        int selectedIndex = -1;
        for (int index = 0; index < originalRelics.Count; index++)
        {
            RelicModel candidate = originalRelics[index];
            if (!ReferenceEquals(candidate, selectedRelic) && !candidate.Id.Equals(selectedRelic.Id))
                continue;
            selectedIndex = index;
            break;
        }
        if (selectedIndex < 0)
            return;

        List<RelicModel> displayRelics = originalRelics
            .Select(RelicModificationStateService.GetEffectivePermanentRelicForDisplay)
            .ToList();
        bool changed = false;
        for (int index = 0; index < displayRelics.Count; index++)
            changed |= !ReferenceEquals(displayRelics[index], originalRelics[index]);
        if (changed)
        {
            relics = displayRelics;
            relic = displayRelics[selectedIndex];
        }
    }
}

[HarmonyPatch(typeof(NRelicCollectionEntry), "OnFocus")]
public static class RelicCollectionHoverTipPermanentDisplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRelicCollectionEntry __instance)
    {
        if (__instance.ModelVisibility != ModelVisibility.Visible)
            return;

        RelicModel displayRelic = RelicModificationStateService.GetEffectivePermanentRelicForDisplay(__instance.relic);
        if (ReferenceEquals(displayRelic, __instance.relic))
            return;

        NHoverTipSet.Remove(__instance);
        NHoverTipSet.CreateAndShow(
                __instance,
                displayRelic.HoverTips,
                HoverTip.GetHoverTipAlignment(__instance))
            ?.SetFollowOwner();
    }
}

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), typeof(RelicModel), typeof(MegaCrit.Sts2.Core.Entities.Players.Player), typeof(int))]
public static class RelicObtainModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(RelicModel relic) => RelicModificationStateService.ApplyPermanentToRelic(relic);
}

[HarmonyPatch(typeof(Player), "PopulateRelics", typeof(IEnumerable<RelicModel>), typeof(bool))]
public static class PlayerPopulateRelicsModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player __instance)
    {
        foreach (RelicModel relic in __instance.Relics) RelicModificationStateService.ApplyPermanentToRelic(relic);
    }
}

[HarmonyPatch]
public static class RelicRarityModificationPatch
{
    public static IEnumerable<MethodBase> TargetMethods() => typeof(RelicModel).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(RelicModel).IsAssignableFrom(type))
        .Select(type => AccessTools.PropertyGetter(type, nameof(RelicModel.Rarity)))
        .Where(method => method is not null)
        .Distinct()!;

    [HarmonyPostfix]
    public static void Postfix(RelicModel __instance, ref RelicRarity __result)
    {
        if (!RelicModificationStateService.HasRarityOverrides) return;
        if (RelicModificationStateService.TryGetRarity(__instance, out RelicRarity rarity))
            __result = rarity;
    }
}

[HarmonyPatch]
public static class RelicIsUsedUpModificationPatch
{
    public static IEnumerable<MethodBase> TargetMethods() => typeof(RelicModel).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(RelicModel).IsAssignableFrom(type))
        .Select(type => AccessTools.PropertyGetter(type, nameof(RelicModel.IsUsedUp)))
        .Where(method => method is not null)
        .Distinct()!;

    [HarmonyPostfix]
    public static void Postfix(RelicModel __instance, ref bool __result)
    {
        if (!RelicModificationStateService.HasNeverUsedOverrides) return;
        if (RelicModificationStateService.ShouldNeverUse(__instance)) __result = false;
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Status), MethodType.Setter)]
public static class RelicStatusModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(RelicModel __instance, ref RelicStatus value)
    {
        if (!RelicModificationStateService.HasNeverUsedOverrides) return;
        if (value == RelicStatus.Disabled && RelicModificationStateService.ShouldNeverUse(__instance))
            value = RelicStatus.Normal;
    }
}

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Melt), typeof(RelicModel))]
public static class RelicMeltModificationPatch
{
    [HarmonyPrefix]
    public static bool Prefix(RelicModel relic, ref Task __result)
    {
        if (!RelicModificationStateService.HasNeverMeltOverrides) return true;
        if (!RelicModificationStateService.ShouldNeverMelt(relic)) return true;
        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Title), MethodType.Getter)]
public static class RelicTitleModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(RelicModel __instance, out bool __state)
    {
        __state = RelicModificationStateService.HasCustomTextOverrides;
        if (__state) RelicModificationStateService.PushLocStringContext(__instance, "title");
    }

    [HarmonyPostfix]
    public static void Postfix(RelicModel __instance, LocString __result, bool __state)
    {
        if (__state) RelicModificationStateService.AssociateLocString(__instance, __result, "title");
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state) RelicModificationStateService.PopLocStringContext();
        return __exception;
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Flavor), MethodType.Getter)]
public static class RelicFlavorModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(RelicModel __instance, out bool __state)
    {
        __state = RelicModificationStateService.HasCustomTextOverrides;
        if (__state) RelicModificationStateService.PushLocStringContext(__instance, "flavor");
    }

    [HarmonyPostfix]
    public static void Postfix(RelicModel __instance, LocString __result, bool __state)
    {
        if (__state) RelicModificationStateService.AssociateLocString(__instance, __result, "flavor");
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state) RelicModificationStateService.PopLocStringContext();
        return __exception;
    }
}

[HarmonyPatch(typeof(LocString), nameof(LocString.GetRawText))]
public static class RelicLocStringRawTextModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(LocString __instance, ref string __result)
    {
        if (!RelicModificationStateService.HasCustomTextOverrides) return;
        if (RelicModificationStateService.TryGetCustomRawLocString(__instance, out string text)) __result = text;
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.DynamicDescription), MethodType.Getter)]
public static class RelicDescriptionModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(RelicModel __instance, out bool __state)
    {
        __state = RelicModificationStateService.HasCustomTextOverrides;
        if (__state) RelicModificationStateService.PushLocStringContext(__instance, "description");
    }

    [HarmonyPostfix]
    public static void Postfix(RelicModel __instance, LocString __result, bool __state)
    {
        if (__state) RelicModificationStateService.AssociateLocString(__instance, __result, "description");
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state) RelicModificationStateService.PopLocStringContext();
        return __exception;
    }
}
