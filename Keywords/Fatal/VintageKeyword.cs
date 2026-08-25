#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;

public sealed class VintageKeyword : LoadoutFatalKeywordModel
{
    public static VintageKeyword Instance { get; } = new();

    private VintageKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Vintage;

    public override string StorageKey => LoadoutKeywords.VintageKey;

    public override string TitleLocKey => "LOADOUT-FATAL_VINTAGE.title";

    public override string? CardTextLocKey => "LOADOUT-FATAL_VINTAGE.cardText";

    public override Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        if (card.Owner.RunState.CurrentRoom is not CombatRoom combatRoom)
            return Task.CompletedTask;

        for (int rewardIndex = 0; rewardIndex < fatalCount; rewardIndex++)
        {
            combatRoom.AddExtraReward(
                card.Owner,
                new RelicReward(card.Owner));
        }

        return Task.CompletedTask;
    }
}
