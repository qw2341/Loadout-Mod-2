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

public sealed class BasicHealKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicHeal";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_HEAL",
                (name, value) => new HealVar(name, value))
        ];

    public static BasicHealKeyword Instance { get; } = new();

    private BasicHealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicHeal;

    public override string StorageKey => LoadoutKeywords.BasicHealKey;

    public override string TitleLocKey => "LOADOUT-BASIC_HEAL.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_HEAL.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        decimal amount = Math.Max(0m, GetAmount(card, AmountVar).BaseValue);
        return amount <= 0m
            ? Task.CompletedTask
            : CreatureCmd.Heal(card.Owner.Creature, amount);
    }
}
