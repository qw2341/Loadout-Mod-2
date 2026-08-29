#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public enum LoadoutBasicMultiplierTarget
{
    DamageDealt,
    MonsterDamageTaken
}

public abstract class LoadoutBasicMultiplierKeywordModel
    : LoadoutBasicKeywordModel
{
    public const string DirectionVarName =
        "LoadoutBasicMultiplierDirection";
    public const string DisplayAmountVarName =
        "LoadoutBasicMultiplierDisplayAmount";

    private IReadOnlyList<LoadoutKeywordDynamicVarDefinition>? _dynamicVars;

    public abstract string PercentageVarName { get; }

    public abstract LoadoutBasicMultiplierTarget MultiplierTarget { get; }

    public abstract TildeKeyDamageMultiplierDuration Duration { get; }

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.BasicMultipliers;

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        _dynamicVars ??=
        [
            new(
                PercentageVarName,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_MULTIPLIER_PERCENTAGE",
                (name, value) => new IntVar(name, value))
        ];

    protected override void AddCardTextVariables(
        CardModel card,
        LocString cardText)
    {
        DynamicVar amount = GetAmount(card, PercentageVarName);
        string directionKey = amount.BaseValue < 0m
            ? "LOADOUT-BASIC_MULTIPLIER_DIRECTION_DECREASE"
            : "LOADOUT-BASIC_MULTIPLIER_DIRECTION_INCREASE";
        cardText.Add(
            DirectionVarName,
            new LocString("card_keywords", directionKey));
        cardText.Add(new DynamicVar(
            DisplayAmountVarName,
            Math.Abs(amount.BaseValue)));
    }

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        int amount = GetAmount(card, PercentageVarName).IntValue;
        if (amount == 0)
            return Task.CompletedTask;

        string statId = MultiplierTarget ==
                        LoadoutBasicMultiplierTarget.DamageDealt
            ? TildeKeyStateService.PlayerDamageMultiplierStatId
            : TildeKeyStateService.EnemyDamageMultiplierStatId;
        TildeKeyStateService.AdjustDamageMultiplier(
            card.Owner,
            statId,
            amount,
            Duration);
        return Task.CompletedTask;
    }
}
