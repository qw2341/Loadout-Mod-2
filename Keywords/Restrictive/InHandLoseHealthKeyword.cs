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
using MegaCrit.Sts2.Core.ValueProps;

public sealed class InHandLoseHealthKeyword : LoadoutRestrictiveKeywordModel
{
    public const string AmountVar = "LoadoutRestrictiveHpLoss";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_RESTRICTIVE_HP_LOSS",
                (name, value) => new HpLossVar(name, value))
        ];

    public static InHandLoseHealthKeyword Instance { get; } = new();

    private InHandLoseHealthKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.InHandLoseHealth;

    public override string StorageKey => LoadoutKeywords.InHandLoseHealthKey;

    public override string TitleLocKey =>
        "LOADOUT-RESTRICTIVE_IN_HAND_LOSE_HEALTH.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_IN_HAND_LOSE_HEALTH.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasTurnEndInHandEffect => true;

    public override async Task AfterTurnEndInHand(
        CardModel card,
        PlayerChoiceContext choiceContext)
    {
        decimal amount = Math.Max(0m, GetAmount(card, AmountVar).BaseValue);
        if (amount <= 0m)
            return;

        await CreatureCmd.Damage(
            choiceContext,
            card.Owner.Creature,
            amount,
            ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
            card,
            null);
    }
}
