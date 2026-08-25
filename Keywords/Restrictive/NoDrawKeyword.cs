#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

public sealed class NoDrawKeyword : LoadoutRestrictiveKeywordModel
{
    public static NoDrawKeyword Instance { get; } = new();

    private NoDrawKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.NoDraw;

    public override string StorageKey => LoadoutKeywords.NoDrawKey;

    public override string TitleLocKey => "LOADOUT-RESTRICTIVE_NO_DRAW.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_NO_DRAW.cardText";

    public override bool HasOnPlayEffect => true;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        await PowerCmd.Apply<NoDrawPower>(
            choiceContext,
            card.Owner.Creature,
            1m,
            card.Owner.Creature,
            card);
    }
}
