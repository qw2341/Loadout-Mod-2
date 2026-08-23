#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class BasicMultiHitKeyword : LoadoutBasicKeywordModel
{
    public const string DamageAmountVar = "LoadoutBasicMultiHitDamage";
    public const string RepeatAmountVar = "LoadoutBasicMultiHitRepeat";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                DamageAmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_MULTI_HIT_DAMAGE",
                (name, value) =>
                    new DamageVar(name, value, ValueProp.Move)),
            new(
                RepeatAmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_MULTI_HIT_REPEAT",
                (name, value) =>
                    new RepeatVar(name, decimal.ToInt32(value)))
        ];

    public static BasicMultiHitKeyword Instance { get; } = new();

    private BasicMultiHitKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicMultiHit;

    public override string StorageKey => LoadoutKeywords.BasicMultiHitKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTI_HIT.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTI_HIT.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool ChangesTargeting => true;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar damage = GetAmount(card, DamageAmountVar);
        int repeats = Math.Max(0, GetAmount(card, RepeatAmountVar).IntValue);
        if (damage.BaseValue <= 0m || repeats == 0)
            return;

        Creature? target = cardPlay.Target;
        if (target is null && card.TargetType == TargetType.AnyPlayer)
            target = card.Owner.Creature;
        else if (target is null && card.TargetType == TargetType.Osty)
            target = card.Owner.Osty;

        if (target is null || target.IsDead)
            return;

        await DamageCmd.Attack(damage.BaseValue)
            .WithHitCount(repeats)
            .FromCard(card, cardPlay)
            .Targeting(target)
            .WithHitFx("vfx/vfx_attack_slash")
            .Execute(choiceContext);
    }
}
