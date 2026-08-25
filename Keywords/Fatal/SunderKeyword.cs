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

public sealed class SunderKeyword : LoadoutFatalKeywordModel
{
    public const string AmountVar = "LoadoutFatalEnergy";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_FATAL_ENERGY",
                (name, value) => new EnergyVar(name, decimal.ToInt32(value)))
        ];

    public static SunderKeyword Instance { get; } = new();

    private SunderKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Sunder;

    public override string StorageKey => LoadoutKeywords.SunderKey;

    public override string TitleLocKey => "LOADOUT-FATAL_SUNDER.title";

    public override string? CardTextLocKey => "LOADOUT-FATAL_SUNDER.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        decimal amount = GetTotalAmount(card, AmountVar, fatalCount);
        return amount <= 0m
            ? Task.CompletedTask
            : PlayerCmd.GainEnergy(amount, card.Owner);
    }
}
