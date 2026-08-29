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

public sealed class MaxHpStealKeyword : LoadoutFatalKeywordModel
{
    public const string PercentageVar = "LoadoutFatalMaxHpStealPercent";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                PercentageVar,
                5m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_FATAL_MAX_HP_STEAL_PERCENT",
                (name, value) => new IntVar(name, value))
        ];

    public static MaxHpStealKeyword Instance { get; } = new();

    private MaxHpStealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.MaxHpSteal;

    public override string StorageKey => LoadoutKeywords.MaxHpStealKey;

    public override string TitleLocKey => "LOADOUT-FATAL_MAX_HP_STEAL.title";

    public override string? CardTextLocKey =>
        "LOADOUT-FATAL_MAX_HP_STEAL.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool RequiresFatalTargetSnapshots => true;

    public override Task AfterFatalTargets(
        CardModel card,
        PlayerChoiceContext choiceContext,
        FatalKeywordContext fatalContext)
    {
        if (!LoadoutKeywordRegistry.TryGetValue(
                card,
                PercentageVar,
                out DynamicVar percentageVar))
        {
            return Task.CompletedTask;
        }

        int percentage = Math.Max(0, percentageVar.IntValue);
        if (percentage == 0 || fatalContext.Targets.Count == 0)
            return Task.CompletedTask;

        decimal targetMaxHp = 0m;
        foreach (FatalTargetSnapshot target in fatalContext.Targets)
            targetMaxHp += target.MaxHp;

        decimal amount = targetMaxHp * percentage / 100m;
        return amount <= 0m
            ? Task.CompletedTask
            : CreatureCmd.GainMaxHp(card.Owner.Creature, amount);
    }
}
