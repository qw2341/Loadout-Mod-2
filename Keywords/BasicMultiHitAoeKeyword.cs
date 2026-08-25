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

public sealed class BasicMultiHitAoeKeyword : LoadoutBasicKeywordModel
{
    public const string DamageAmountVar = "LoadoutBasicMultiHitAoeDamage";
    public const string RepeatAmountVar = "LoadoutBasicMultiHitAoeRepeat";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                DamageAmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_MULTI_HIT_AOE_DAMAGE",
                (name, value) =>
                    new DamageVar(name, value, ValueProp.Move)),
            new(
                RepeatAmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_MULTI_HIT_AOE_REPEAT",
                (name, value) =>
                    new RepeatVar(name, decimal.ToInt32(value)))
        ];

    public static BasicMultiHitAoeKeyword Instance { get; } = new();

    private BasicMultiHitAoeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicMultiHitAoe;

    public override string StorageKey => LoadoutKeywords.BasicMultiHitAoeKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTI_HIT_AOE.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTI_HIT_AOE.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

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
            .TargetingAllOpponents(card.CombatState)
            .WithHitFx("vfx/vfx_attack_blunt", null, "heavy_attack.mp3")
            .Execute(choiceContext);
    }
}
