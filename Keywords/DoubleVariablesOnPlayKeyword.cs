#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class DoubleVariablesOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public static DoubleVariablesOnPlayKeyword Instance { get; } = new();

    private DoubleVariablesOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.DoubleVariablesOnPlay;

    public override string StorageKey =>
        LoadoutKeywords.DoubleVariablesOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_DOUBLE_VARIABLES_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_DOUBLE_VARIABLES_ON_PLAY.cardText";

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        MultiplyOtherVariables(card, 2m);
        return Task.CompletedTask;
    }
}
