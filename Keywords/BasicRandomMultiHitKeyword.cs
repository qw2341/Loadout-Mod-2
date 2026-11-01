#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class BasicRandomMultiHitKeyword : LoadoutBasicKeywordModel
{
    public const string DamageAmountVar = "LoadoutBasicRandomMultiHitDamage";
    public const string RepeatAmountVar = "LoadoutBasicRandomMultiHitRepeat";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                DamageAmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_RANDOM_MULTI_HIT_DAMAGE",
                (name, value) => new DamageVar(name, value, ValueProp.Move)),
            new(
                RepeatAmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_RANDOM_MULTI_HIT_REPEAT",
                (name, value) => new RepeatVar(name, decimal.ToInt32(value)))
        ];

    public static BasicRandomMultiHitKeyword Instance { get; } = new();

    private BasicRandomMultiHitKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicRandomMultiHit;

    public override string StorageKey => LoadoutKeywords.BasicRandomMultiHitKey;

    public override string TitleLocKey => "LOADOUT-BASIC_RANDOM_MULTI_HIT.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_RANDOM_MULTI_HIT.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override TargetType? MultiTargetType => TargetType.RandomEnemy;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar damage = GetAmount(card, DamageAmountVar);
        int repeats = Math.Max(0, GetAmount(card, RepeatAmountVar).IntValue);
        if (damage.BaseValue <= 0m
            || repeats == 0
            || card.CombatState is null)
        {
            return;
        }

        await DamageCmd.Attack(damage.BaseValue)
            .WithHitCount(repeats)
            .FromCard(card, cardPlay)
            .TargetingRandomOpponents(card.CombatState)
            .WithHitFx("vfx/vfx_attack_slash")
            .Execute(choiceContext);
    }
}
