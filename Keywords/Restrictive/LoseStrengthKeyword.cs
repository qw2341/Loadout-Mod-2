#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

public sealed class LoseStrengthKeyword : LoadoutRestrictiveKeywordModel
{
    public const string AmountVar = "LoadoutRestrictiveStrengthLoss";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_RESTRICTIVE_STRENGTH_LOSS",
                (name, value) => new PowerVar<StrengthPower>(name, value))
        ];

    public static LoseStrengthKeyword Instance { get; } = new();

    private LoseStrengthKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.LoseStrength;

    public override string StorageKey => LoadoutKeywords.LoseStrengthKey;

    public override string TitleLocKey =>
        "LOADOUT-RESTRICTIVE_LOSE_STRENGTH.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_LOSE_STRENGTH.cardText";

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

        await PowerCmd.Apply<StrengthPower>(
            choiceContext,
            card.Owner.Creature,
            -amount,
            card.Owner.Creature,
            card);
    }
}
