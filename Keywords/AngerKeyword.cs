#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class AngerKeyword : LoadoutKeywordModel
{
    public static AngerKeyword Instance { get; } = new();

    private AngerKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Anger;

    public override string StorageKey => LoadoutKeywords.AngerKey;

    public override string TitleLocKey => "LOADOUT-ANGER.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey => "LOADOUT-ANGER.cardText";

    public override bool HasOnPlayEffect => true;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        CardModel copy = card.CreateClone();
        CardCmd.PreviewCardPileAdd(
            await CardPileCmd.AddGeneratedCardToCombat(
                copy,
                PileType.Discard,
                card.Owner),
            2.2f);
    }
}
