#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;

public sealed class VintageKeyword : LoadoutFatalKeywordModel
{
    public const string AmountVar = "LoadoutFatalRelicRewards";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_FATAL_RELIC_REWARDS",
                (name, value) => new IntVar(name, value))
        ];

    public static VintageKeyword Instance { get; } = new();

    private VintageKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Vintage;

    public override string StorageKey => LoadoutKeywords.VintageKey;

    public override string TitleLocKey => "LOADOUT-FATAL_VINTAGE.title";

    public override string? CardTextLocKey => "LOADOUT-FATAL_VINTAGE.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        if (card.Owner.RunState.CurrentRoom is not CombatRoom combatRoom)
            return Task.CompletedTask;

        int rewardCount = decimal.ToInt32(
            GetTotalAmount(card, AmountVar, fatalCount));
        for (int rewardIndex = 0; rewardIndex < rewardCount; rewardIndex++)
        {
            combatRoom.AddExtraReward(
                card.Owner,
                new RelicReward(card.Owner));
        }

        return Task.CompletedTask;
    }
}
