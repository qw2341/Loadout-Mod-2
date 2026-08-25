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

public sealed class BorrowedKeyword : LoadoutRestrictiveKeywordModel
{
    public const string AmountVar = "LoadoutRestrictiveExtraCost";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_RESTRICTIVE_EXTRA_COST",
                (name, value) => new EnergyVar(name, decimal.ToInt32(value)))
        ];

    public static BorrowedKeyword Instance { get; } = new();

    private BorrowedKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Borrowed;

    public override string StorageKey => LoadoutKeywords.BorrowedKey;

    public override string TitleLocKey => "LOADOUT-RESTRICTIVE_BORROWED.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_BORROWED.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        decimal amount = GetAmount(card, AmountVar).BaseValue;
        if (amount <= 0m)
            return;

        await PowerCmd.Apply<BorrowedTimePower>(
            choiceContext,
            card.Owner.Creature,
            amount,
            card.Owner.Creature,
            card);
    }
}
