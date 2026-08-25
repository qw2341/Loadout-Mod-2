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

public sealed class GreedKeyword : LoadoutFatalKeywordModel
{
    public const string AmountVar = "LoadoutFatalGold";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_FATAL_GOLD",
                (name, value) => new GoldVar(name, decimal.ToInt32(value)))
        ];

    public static GreedKeyword Instance { get; } = new();

    private GreedKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Greed;

    public override string StorageKey => LoadoutKeywords.GreedKey;

    public override string TitleLocKey => "LOADOUT-FATAL_GREED.title";

    public override string? CardTextLocKey => "LOADOUT-FATAL_GREED.cardText";

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
            : PlayerCmd.GainGold(amount, card.Owner);
    }
}
