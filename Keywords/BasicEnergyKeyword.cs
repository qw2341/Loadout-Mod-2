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

public sealed class BasicEnergyKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicEnergy";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_ENERGY",
                (name, value) => new EnergyVar(name, decimal.ToInt32(value)))
        ];

    public static BasicEnergyKeyword Instance { get; } = new();

    private BasicEnergyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicEnergy;

    public override string StorageKey => LoadoutKeywords.BasicEnergyKey;

    public override string TitleLocKey => "LOADOUT-BASIC_ENERGY.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_ENERGY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        int amount = Math.Max(0, GetAmount(card, AmountVar).IntValue);
        return amount == 0
            ? Task.CompletedTask
            : PlayerCmd.GainEnergy(amount, card.Owner);
    }
}
