#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class BasicDiscardKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicDiscard";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_DISCARD",
                (name, value) => new CardsVar(name, decimal.ToInt32(value)))
        ];

    public static BasicDiscardKeyword Instance { get; } = new();

    private BasicDiscardKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicDiscard;

    public override string StorageKey => LoadoutKeywords.BasicDiscardKey;

    public override string TitleLocKey => "LOADOUT-BASIC_DISCARD.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_DISCARD.cardText";

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

        IEnumerable<CardModel> selection = await CardSelectCmd.FromHandForDiscard(
            choiceContext,
            card.Owner,
            new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, amount),
            null,
            card);
        await CardCmd.Discard(choiceContext, selection);
    }
}
