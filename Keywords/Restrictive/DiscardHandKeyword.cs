#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class DiscardHandKeyword : LoadoutRestrictiveKeywordModel
{
    public static DiscardHandKeyword Instance { get; } = new();

    private DiscardHandKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.DiscardHand;

    public override string StorageKey => LoadoutKeywords.DiscardHandKey;

    public override string TitleLocKey =>
        "LOADOUT-RESTRICTIVE_DISCARD_HAND.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_DISCARD_HAND.cardText";

    public override bool HasOnPlayEffect => true;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        await CardCmd.Discard(
            choiceContext,
            PileType.Hand.GetPile(card.Owner).Cards);
    }
}
