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

public sealed class LoseDexterityKeyword : LoadoutRestrictiveKeywordModel
{
    public const string AmountVar = "LoadoutRestrictiveDexterityLoss";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_RESTRICTIVE_DEXTERITY_LOSS",
                (name, value) => new PowerVar<DexterityPower>(name, value))
        ];

    public static LoseDexterityKeyword Instance { get; } = new();

    private LoseDexterityKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.LoseDexterity;

    public override string StorageKey => LoadoutKeywords.LoseDexterityKey;

    public override string TitleLocKey =>
        "LOADOUT-RESTRICTIVE_LOSE_DEXTERITY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_LOSE_DEXTERITY.cardText";

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

        await PowerCmd.Apply<DexterityPower>(
            choiceContext,
            card.Owner.Creature,
            -amount,
            card.Owner.Creature,
            card);
    }
}
