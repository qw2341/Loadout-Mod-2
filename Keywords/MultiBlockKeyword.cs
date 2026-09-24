#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class MultiBlockKeyword : LoadoutKeywordModel
{
    public const string UnitBlockVarName = "LoadoutMultiBlockUnitBlock";
    public static MultiBlockKeyword Instance { get; } = new();
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> Variables =
    [
        new(UnitBlockVarName, 1m, 1, 1, "",
            (_, _) => new UnitBlockVar(), EditorVisible: false)
    ];

    private MultiBlockKeyword() { }

    public override CardKeyword Keyword => LoadoutKeywords.MultiBlock;

    public override LoadoutCardModificationFlags ModificationFlags =>
        LoadoutCardModificationFlags.FastAnimation;
    public override string StorageKey => LoadoutKeywords.MultiBlockKey;
    public override string TitleLocKey => "LOADOUT-MULTI_BLOCK.title";
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Joke;
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.Normal;
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;

    public sealed class UnitBlockVar() : DynamicVar(UnitBlockVarName, 1m)
    {
        public override void UpdateCardPreview(
            CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
        {
            ValueProp props = card.DynamicVars.TryGetValue(BlockVar.defaultName, out DynamicVar? original)
                              && original is BlockVar block ? block.Props : ValueProp.Move;
            BlockVar unit = new(1m, props);
            unit.UpdateCardPreview(card, previewMode, target, runGlobalHooks);
            BaseValue = 1m;
            EnchantedValue = unit.EnchantedValue;
            PreviewValue = unit.PreviewValue;
        }
    }
}

public static class MultiBlockKeywordPatches
{
    private static readonly Harmony Harmony = new("Loadout.Keyword.MultiBlock");
    private static readonly AccessTools.FieldRef<DynamicVar, AbstractModel?> Owner =
        AccessTools.FieldRefAccess<DynamicVar, AbstractModel?>("_owner");
    private static bool Installed;

    public static bool IsOriginalBlock(DynamicVar variable, out CardModel card)
    {
        card = null!;
        if (variable is not BlockVar || variable.Name != BlockVar.defaultName
            || Owner(variable) is not CardModel owner
            || !LoadoutKeywords.Has(owner, LoadoutKeywords.MultiBlock))
            return false;
        card = owner;
        return true;
    }

    public static void Prepare()
    {
        if (Installed)
            return;
        Harmony.Patch(AccessTools.Method(typeof(BlockVar), nameof(BlockVar.UpdateCardPreview)),
            prefix: new HarmonyMethod(typeof(MultiBlockKeywordPatches), nameof(PreviewPrefix)));
        Harmony.Patch(AccessTools.Method(typeof(DynamicVar), nameof(DynamicVar.ToHighlightedString)),
            postfix: new HarmonyMethod(typeof(MultiBlockKeywordPatches), nameof(HighlightPostfix)));
        Harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainBlock),
                [typeof(Creature), typeof(BlockVar), typeof(CardPlay), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(MultiBlockKeywordPatches), nameof(GainBlockPrefix)));
        Installed = true;
    }

    public static bool PreviewPrefix(BlockVar __instance)
    {
        if (!IsOriginalBlock(__instance, out _))
            return true;
        __instance.ResetToBase();
        return false;
    }

    public static void HighlightPostfix(DynamicVar __instance, bool inverse, ref string __result)
    {
        if (IsOriginalBlock(__instance, out CardModel card)
            && card.DynamicVars.TryGetValue(MultiBlockKeyword.UnitBlockVarName, out DynamicVar? unit))
        {
            __result = $"{unit.ToHighlightedString(inverse)} x {__result}";
            if (LocManager.Instance?.Language is "zhs" or "zht")
                __result = $" {__result} ";
        }
    }

    public static bool GainBlockPrefix(
        Creature creature, BlockVar blockVar, CardPlay? cardPlay, bool fast,
        ref Task<decimal> __result)
    {
        if (!IsOriginalBlock(blockVar, out CardModel card))
            return true;
        __result = GainRepeatedBlock(creature, MultiHitKeyword.GetHitCount(blockVar.BaseValue),
            blockVar.Props, cardPlay, fast, card);
        return false;
    }

    public static async Task<decimal> GainRepeatedBlock(
        Creature creature, int count, ValueProp props, CardPlay? cardPlay, bool fast, CardModel card)
    {
        CardEffectAnimationScope.MarkAltHeavenlyResult(card, count);
        decimal amount = XValueKeywordRuntime.GetInstanceValue(card);
        decimal total = 0m;
        for (int i = 0; i < count && !creature.IsDead && !CombatManager.Instance.IsOverOrEnding; i++)
            total += await CreatureCmd.GainBlock(creature, amount, props, cardPlay, fast);
        return total;
    }
}
