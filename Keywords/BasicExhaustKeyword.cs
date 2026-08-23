#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class BasicExhaustKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicExhaust";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_EXHAUST",
                (name, value) => new CardsVar(name, decimal.ToInt32(value)))
        ];

    public static BasicExhaustKeyword Instance { get; } = new();

    private BasicExhaustKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicExhaust;

    public override string StorageKey => LoadoutKeywords.BasicExhaustKey;

    public override string TitleLocKey => "LOADOUT-BASIC_EXHAUST.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_EXHAUST.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        int amount = GetClampedHandCount(card, AmountVar);
        if (amount == 0)
            return;

        IEnumerable<CardModel> selection = await CardSelectCmd.FromHand(
            choiceContext,
            card.Owner,
            new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, amount),
            null,
            card);
        foreach (CardModel selectedCard in selection.ToList())
            await CardCmd.Exhaust(choiceContext, selectedCard);
    }
}
