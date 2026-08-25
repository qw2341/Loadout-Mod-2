#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class VariablesOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public const string AmountVar = "LoadoutImprovementVariablesOnPlay";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_IMPROVEMENT_VARIABLES_ON_PLAY",
                (name, value) => new IntVar(name, value))
        ];

    public static VariablesOnPlayKeyword Instance { get; } = new();

    private VariablesOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.VariablesOnPlay;

    public override string StorageKey => LoadoutKeywords.VariablesOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_VARIABLES_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_VARIABLES_ON_PLAY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        IncreaseOtherVariables(card, GetAmount(card, AmountVar));
        return Task.CompletedTask;
    }
}
