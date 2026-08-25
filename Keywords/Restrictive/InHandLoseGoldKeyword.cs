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

public sealed class InHandLoseGoldKeyword : LoadoutRestrictiveKeywordModel
{
    public const string AmountVar = "LoadoutRestrictiveGold";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_RESTRICTIVE_GOLD",
                (name, value) => new GoldVar(name, decimal.ToInt32(value)))
        ];

    public static InHandLoseGoldKeyword Instance { get; } = new();

    private InHandLoseGoldKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.InHandLoseGold;

    public override string StorageKey => LoadoutKeywords.InHandLoseGoldKey;

    public override string TitleLocKey =>
        "LOADOUT-RESTRICTIVE_IN_HAND_LOSE_GOLD.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_IN_HAND_LOSE_GOLD.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasTurnEndInHandEffect => true;

    public override async Task AfterTurnEndInHand(
        CardModel card,
        PlayerChoiceContext choiceContext)
    {
        int amount = Math.Min(
            Math.Max(0, GetAmount(card, AmountVar).IntValue),
            card.Owner.Gold);
        if (amount > 0)
            await PlayerCmd.LoseGold(amount, card.Owner);
    }
}
