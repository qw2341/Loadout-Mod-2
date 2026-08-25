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

public sealed class BasicLoseHealthKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicHpLoss";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_HP_LOSS",
                (name, value) => new HpLossVar(name, value))
        ];

    public static BasicLoseHealthKeyword Instance { get; } = new();

    private BasicLoseHealthKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicLoseHealth;

    public override string StorageKey => LoadoutKeywords.BasicLoseHealthKey;

    public override string TitleLocKey => "LOADOUT-BASIC_LOSE_HEALTH.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_LOSE_HEALTH.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
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
            cardPlay);
    }
}
