#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class BlankSlateKeyword : LoadoutKeywordModel
{
    public static BlankSlateKeyword Instance { get; } = new();

    private BlankSlateKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BlankSlate;

    public override string StorageKey => LoadoutKeywords.BlankSlateKey;

    public override string TitleLocKey => "LOADOUT-BLANK_SLATE.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Basic;

    public override bool TransformsBaseDescription => true;

    public override int BaseDescriptionPriority => int.MinValue;

    public override bool SuppressesOriginalOnPlay => true;

    public override bool SuppressesOriginalIsPlayable => true;

    public override bool SuppressesOriginalShouldGlowGold => true;

    public override string TransformBaseDescription(
        CardModel card,
        string description)
    {
        return string.Empty;
    }
}

internal static class BlankSlateCardLogicPatch
{
    public static IEnumerable<MethodBase> ShouldGlowGoldTargets()
    {
        return LoadoutKeywordModel.GetCardPropertyGetters(
            "ShouldGlowGoldInternal");
    }

    [HarmonyPrefix]
    public static bool IsPlayablePrefix(
        CardModel __instance,
        ref bool __result)
    {
        if (!LoadoutKeywordRegistry.SuppressesOriginalIsPlayable(__instance))
            return true;

        __result = true;
        return false;
    }

    [HarmonyPrefix]
    public static bool ShouldGlowGoldPrefix(
        CardModel __instance,
        ref bool __result)
    {
        if (!LoadoutKeywordRegistry.SuppressesOriginalShouldGlowGold(__instance))
            return true;

        __result = false;
        return false;
    }
}
