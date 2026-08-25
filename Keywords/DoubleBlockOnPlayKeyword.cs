#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class DoubleBlockOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public static DoubleBlockOnPlayKeyword Instance { get; } = new();

    private DoubleBlockOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.DoubleBlockOnPlay;

    public override string StorageKey => LoadoutKeywords.DoubleBlockOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_DOUBLE_BLOCK_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_DOUBLE_BLOCK_ON_PLAY.cardText";

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        MultiplyBlock(card, 2m);
        return Task.CompletedTask;
    }
}
