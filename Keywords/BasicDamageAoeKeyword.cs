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

public sealed class BasicDamageAoeKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicDamageAoe";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_DAMAGE_AOE",
                (name, value) => new DamageVar(name, value, ValueProp.Move))
        ];

    public static BasicDamageAoeKeyword Instance { get; } = new();

    private BasicDamageAoeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicDamageAoe;

    public override string StorageKey => LoadoutKeywords.BasicDamageAoeKey;

    public override string TitleLocKey => "LOADOUT-BASIC_DAMAGE_AOE.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_DAMAGE_AOE.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        if (amount.BaseValue <= 0m || card.CombatState is null)
            return;

        await DamageCmd.Attack(amount.BaseValue)
            .FromCard(card, cardPlay)
            .TargetingAllOpponents(card.CombatState)
            .WithHitFx("vfx/vfx_attack_slash")
            .Execute(choiceContext);
    }
}
