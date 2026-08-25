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

public sealed class BasicTransformKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicTransform";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_TRANSFORM",
                (name, value) => new CardsVar(name, decimal.ToInt32(value)))
        ];

    public static BasicTransformKeyword Instance { get; } = new();

    private BasicTransformKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicTransform;

    public override string StorageKey => LoadoutKeywords.BasicTransformKey;

    public override string TitleLocKey => "LOADOUT-BASIC_TRANSFORM.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_TRANSFORM.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        int requested = Math.Max(0, GetAmount(card, AmountVar).IntValue);
        int available = PileType.Hand.GetPile(card.Owner).Cards.Count(
            candidate => candidate.IsTransformable);
        int amount = Math.Min(requested, available);
        if (amount == 0)
            return;

        List<CardModel> selection = (await CardSelectCmd.FromHand(
            choiceContext,
            card.Owner,
            new CardSelectorPrefs(
                CardSelectorPrefs.TransformSelectionPrompt,
                amount),
            candidate => candidate.IsTransformable,
            card)).ToList();

        await CardCmd.Transform(
            selection.Select(selected => new CardTransformation(selected)),
            card.Owner.RunState.Rng.CombatCardSelection);
    }
}
