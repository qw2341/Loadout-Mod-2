#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

public sealed class HuntKeyword : LoadoutFatalKeywordModel
{
    public const string AmountVar = "LoadoutFatalCardRewards";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_FATAL_CARD_REWARDS",
                (name, value) => new IntVar(name, value))
        ];

    public static HuntKeyword Instance { get; } = new();

    private HuntKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Hunt;

    public override string StorageKey => LoadoutKeywords.HuntKey;

    public override string TitleLocKey => "LOADOUT-FATAL_HUNT.title";

    public override string? CardTextLocKey => "LOADOUT-FATAL_HUNT.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        if (card.Owner.RunState.CurrentRoom is not CombatRoom combatRoom)
            return;

        int rewardCount = decimal.ToInt32(
            GetTotalAmount(card, AmountVar, fatalCount));
        if (rewardCount <= 0)
            return;

        for (int rewardIndex = 0; rewardIndex < rewardCount; rewardIndex++)
        {
            combatRoom.AddExtraReward(
                card.Owner,
                new CardReward(
                    CardCreationOptions.ForRoom(
                        card.Owner,
                        combatRoom.RoomType),
                    3,
                    card.Owner));
        }

        await PowerCmd.Apply<TheHuntPower>(
            choiceContext,
            card.Owner.Creature,
            rewardCount,
            card.Owner.Creature,
            card);
    }
}
