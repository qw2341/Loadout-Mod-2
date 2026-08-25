#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class EndTurnKeyword : LoadoutRestrictiveKeywordModel
{
    public static EndTurnKeyword Instance { get; } = new();

    private EndTurnKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.EndTurn;

    public override string StorageKey => LoadoutKeywords.EndTurnKey;

    public override string TitleLocKey => "LOADOUT-RESTRICTIVE_END_TURN.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_END_TURN.cardText";

    public override bool HasOnPlayEffect => true;

    public override int OnPlayPriority => int.MaxValue;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        PlayerCmd.EndTurn(card.Owner, canBackOut: false);
        return Task.CompletedTask;
    }
}
