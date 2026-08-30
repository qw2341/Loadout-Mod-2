#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

public sealed class MegaGatlingKeyword : LoadoutKeywordModel
{
    public const string ChancePercentageVar =
        "LoadoutMegaGatlingChancePercentage";
    public const string ChanceIncreasePercentageVar =
        "LoadoutMegaGatlingChanceIncreasePercentage";
    public const string TriggerChanceCapPercentageVar =
        "LoadoutMegaGatlingTriggerChanceCapPercentage";
    public const string TotalPlayCountVar =
        "LoadoutMegaGatlingTotalPlayCount";
    public const string CurrentChancePercentageTextVar =
        "LoadoutMegaGatlingCurrentChancePercentage";
    public const int DefaultStartingChancePercentage = 2;
    public const int DefaultChanceIncreasePercentage = 1;
    public const int DefaultTriggerChanceCapPercentage = 10;

    private static readonly ConditionalWeakTable<CardModel, ChanceState>
        ChanceStates = new();

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                ChancePercentageVar,
                DefaultStartingChancePercentage,
                0,
                100,
                "DYNAMIC_VAR_LOADOUT_MEGA_GATLING_CHANCE_PERCENTAGE",
                (name, value) => new IntVar(name, value)),
            new(
                ChanceIncreasePercentageVar,
                DefaultChanceIncreasePercentage,
                0,
                100,
                "DYNAMIC_VAR_LOADOUT_MEGA_GATLING_CHANCE_INCREASE_PERCENTAGE",
                (name, value) => new IntVar(name, value)),
            new(
                TriggerChanceCapPercentageVar,
                DefaultTriggerChanceCapPercentage,
                0,
                100,
                "DYNAMIC_VAR_LOADOUT_MEGA_GATLING_TRIGGER_CHANCE_CAP_PERCENTAGE",
                (name, value) => new IntVar(name, value)),
            new(
                TotalPlayCountVar,
                150m,
                1,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_MEGA_GATLING_TOTAL_PLAYS",
                (name, value) => new IntVar(name, value))
        ];

    public static MegaGatlingKeyword Instance { get; } = new();

    private MegaGatlingKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.MegaGatling;

    public override string StorageKey => LoadoutKeywords.MegaGatlingKey;

    public override string TitleLocKey => "LOADOUT-MEGA_GATLING.title";

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Joke;

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey =>
        "LOADOUT-MEGA_GATLING.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    protected override void AddCardTextVariables(
        CardModel card,
        LocString cardText)
    {
        int currentChancePercentage = ChanceStates.TryGetValue(
            card,
            out ChanceState? state)
            ? state.CurrentChancePercentage
            : GetStartingChancePercentage(card);
        cardText.Add(new IntVar(
            CurrentChancePercentageTextVar,
            currentChancePercentage));
    }

    public static int GetStartingChancePercentage(CardModel card)
    {
        return LoadoutKeywordRegistry.TryGetValue(
            card,
            ChancePercentageVar,
            out DynamicVar startingChanceVar)
            ? startingChanceVar.IntValue
            : DefaultStartingChancePercentage;
    }

    public static int GetCurrentChancePercentage(
        CardModel card,
        int startingChancePercentage)
    {
        return ChanceStates.GetValue(
                card,
                _ => new ChanceState(startingChancePercentage))
            .CurrentChancePercentage;
    }

    public static void SetCurrentChancePercentage(
        CardModel card,
        int chancePercentage)
    {
        ChanceStates.GetValue(
                card,
                _ => new ChanceState(chancePercentage))
            .CurrentChancePercentage = chancePercentage;
    }

    public static void RefreshCardText(CardModel card)
    {
        NCard.FindOnTable(card)?.UpdateVisuals(
            PileType.Play,
            CardPreviewMode.Normal);
    }

    private sealed class ChanceState(int currentChancePercentage)
    {
        public int CurrentChancePercentage { get; set; } =
            currentChancePercentage;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
internal static class MegaGatlingModifyCardPlayCountPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card, ref int playCount)
    {
        if (!LoadoutKeywords.Has(card, LoadoutKeywords.MegaGatling))
            return;

        if (!LoadoutKeywordRegistry.TryGetValue(
                card,
                MegaGatlingKeyword.ChancePercentageVar,
                out DynamicVar startingChanceVar)
            || !LoadoutKeywordRegistry.TryGetValue(
                card,
                MegaGatlingKeyword.ChanceIncreasePercentageVar,
                out DynamicVar chanceIncreaseVar)
            || !LoadoutKeywordRegistry.TryGetValue(
                card,
                MegaGatlingKeyword.TriggerChanceCapPercentageVar,
                out DynamicVar triggerChanceCapVar)
            || !LoadoutKeywordRegistry.TryGetValue(
                card,
                MegaGatlingKeyword.TotalPlayCountVar,
                out DynamicVar totalPlayCountVar))
        {
            return;
        }

        int startingChancePercentage = startingChanceVar.IntValue;
        int chancePercentage = MegaGatlingKeyword.GetCurrentChancePercentage(
            card,
            startingChancePercentage);
        int chanceIncreasePercentage = Math.Max(0, chanceIncreaseVar.IntValue);
        int triggerChanceCapPercentage = Math.Clamp(
            triggerChanceCapVar.IntValue,
            0,
            100);
        int totalPlayCount = Math.Max(1, totalPlayCountVar.IntValue);
        bool triggered = card.Owner.RunState.Rng.CombatCardSelection.NextInt(100)
                         < Math.Clamp(chancePercentage, 0, 100);

        int nextChancePercentage = chancePercentage;
        if (triggered)
        {
            nextChancePercentage = startingChancePercentage;
        }
        else if (chancePercentage < triggerChanceCapPercentage)
        {
            nextChancePercentage = (int)Math.Min(
                triggerChanceCapPercentage,
                (long)chancePercentage + chanceIncreasePercentage);
        }

        MegaGatlingKeyword.SetCurrentChancePercentage(
            card,
            nextChancePercentage);
        MegaGatlingKeyword.RefreshCardText(card);

        if (!triggered)
            return;

        int additionalPlayCount = totalPlayCount - 1;
        CardEffectAnimationScope.MarkMegaGatlingReplay(
            card,
            additionalPlayCount);
        playCount += additionalPlayCount;
    }
}
