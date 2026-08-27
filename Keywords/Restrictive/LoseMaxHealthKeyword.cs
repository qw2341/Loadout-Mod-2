#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class LoseMaxHealthKeyword : LoadoutRestrictiveKeywordModel
{
    public const string AmountVar = "LoadoutRestrictiveMaxHpLoss";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_RESTRICTIVE_MAX_HP_LOSS",
                (name, value) => new MaxHpVar(name, value))
        ];

    public static LoseMaxHealthKeyword Instance { get; } = new();

    private LoseMaxHealthKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.LoseMaxHealth;

    public override string StorageKey => LoadoutKeywords.LoseMaxHealthKey;

    public override string TitleLocKey =>
        "LOADOUT-RESTRICTIVE_LOSE_MAX_HEALTH.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_LOSE_MAX_HEALTH.cardText";

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
            await CreatureCmd.GainMaxHp(card.Owner.Creature, amount);
        else
            await CreatureCmd.LoseMaxHp(
                choiceContext,
                card.Owner.Creature,
                amount,
                isFromCard: true);
    }
}
