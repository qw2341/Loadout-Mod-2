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

public sealed class BasicGainMaxHpKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicMaxHpGain";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_MAX_HP_GAIN",
                (name, value) => new MaxHpVar(name, value))
        ];

    public static BasicGainMaxHpKeyword Instance { get; } = new();

    private BasicGainMaxHpKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicGainMaxHp;

    public override string StorageKey => LoadoutKeywords.BasicGainMaxHpKey;

    public override string TitleLocKey => "LOADOUT-BASIC_GAIN_MAX_HP.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_GAIN_MAX_HP.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        decimal amount = Math.Max(0m, GetAmount(card, AmountVar).BaseValue);
        return amount <= 0m
            ? CreatureCmd.LoseMaxHp(choiceContext,card.Owner.Creature, amount,true)
            : CreatureCmd.GainMaxHp(card.Owner.Creature, amount);
    }
}
