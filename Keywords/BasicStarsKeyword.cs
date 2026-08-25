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

public sealed class BasicStarsKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicStars";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_STARS",
                (name, value) => new StarsVar(name, decimal.ToInt32(value)))
        ];

    public static BasicStarsKeyword Instance { get; } = new();

    private BasicStarsKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicStars;

    public override string StorageKey => LoadoutKeywords.BasicStarsKey;

    public override string TitleLocKey => "LOADOUT-BASIC_STARS.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_STARS.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        return amount.BaseValue <= 0m
            ? Task.CompletedTask
            : PlayerCmd.GainStars(amount.BaseValue, card.Owner);
    }
}
