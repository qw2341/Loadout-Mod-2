#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class GatlingKeyword : LoadoutKeywordModel
{
    public const string ChancePercentageVar =
        "LoadoutGatlingChancePercentage";
    public const string AdditionalPlayCountVar =
        "LoadoutGatlingAdditionalPlayCount";
    public const int FastAnimationAdditionalPlayThreshold = 10;
    public const int ExtremelyFastAnimationAdditionalPlayThreshold = 50;
    public const int InstantAnimationAdditionalPlayThreshold = 1000;

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                ChancePercentageVar,
                25m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_GATLING_CHANCE_PERCENTAGE",
                (name, value) => new IntVar(name, value)),
            new(
                AdditionalPlayCountVar,
                3m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_GATLING_ADDITIONAL_PLAYS",
                (name, value) => new IntVar(name, value))
        ];

    public static GatlingKeyword Instance { get; } = new();

    private GatlingKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Gatling;

    public override string StorageKey => LoadoutKeywords.GatlingKey;

    public override string TitleLocKey => "LOADOUT-GATLING.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey =>
        "LOADOUT-GATLING.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
internal static class GatlingModifyCardPlayCountPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card, ref int playCount)
    {
        if (!LoadoutKeywords.Has(card, LoadoutKeywords.Gatling))
            return;

        if (!LoadoutKeywordRegistry.TryGetValue(
                card,
                GatlingKeyword.ChancePercentageVar,
                out DynamicVar chanceVar)
            || !LoadoutKeywordRegistry.TryGetValue(
                card,
                GatlingKeyword.AdditionalPlayCountVar,
                out DynamicVar additionalPlayCountVar))
        {
            return;
        }

        int chancePercentage = Math.Clamp(chanceVar.IntValue, 0, 100);
        int additionalPlayCount = Math.Max(
            0,
            additionalPlayCountVar.IntValue);
        if (chancePercentage == 0 || additionalPlayCount == 0)
            return;

        if (card.Owner.RunState.Rng.CombatCardSelection.NextInt(100)
            >= chancePercentage)
        {
            return;
        }

        CardEffectAnimationScope.MarkGatlingReplay(
            card,
            additionalPlayCount);
        playCount += additionalPlayCount;
    }
}
