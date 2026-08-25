#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class AllBlockOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public const string AmountVar = "LoadoutImprovementAllBlockOnPlay";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_IMPROVEMENT_ALL_BLOCK_ON_PLAY",
                (name, value) => new IntVar(name, value))
        ];

    public static AllBlockOnPlayKeyword Instance { get; } = new();

    private AllBlockOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllBlockOnPlay;

    public override string StorageKey => LoadoutKeywords.AllBlockOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_ALL_BLOCK_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_ALL_BLOCK_ON_PLAY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    protected override void AddCardTextVariables(CardModel card, LocString cardText)
    {
        cardText.Add("CardTitle", card.Title);
    }

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        decimal amount = GetAmount(card, AmountVar);
        if (amount <= 0m || card.Owner.PlayerCombatState is not { } combatState)
            return Task.CompletedTask;

        foreach (CardModel candidate in combatState.AllCards)
        {
            if (candidate.Id.Equals(card.Id))
                IncreaseBlock(candidate, amount);
        }

        return Task.CompletedTask;
    }
}
