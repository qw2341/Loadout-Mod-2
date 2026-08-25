#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class GrandKeyword : LoadoutRestrictiveKeywordModel
{
    public static GrandKeyword Instance { get; } = new();

    private GrandKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Grand;

    public override string StorageKey => LoadoutKeywords.GrandKey;

    public override string TitleLocKey => "LOADOUT-RESTRICTIVE_GRAND.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_GRAND.cardText";

    public override bool HasPlayRestriction => true;

    public static bool CanPlay(CardModel card)
    {
        return PileType.Draw.GetPile(card.Owner).Cards.Count == 0;
    }
}
