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

public sealed class FeedKeyword : LoadoutFatalKeywordModel
{
    public const string AmountVar = "LoadoutFatalMaxHp";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_FATAL_MAX_HP",
                (name, value) => new MaxHpVar(name, value))
        ];

    public static FeedKeyword Instance { get; } = new();

    private FeedKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Feed;

    public override string StorageKey => LoadoutKeywords.FeedKey;

    public override string TitleLocKey => "LOADOUT-FATAL_FEED.title";

    public override string? CardTextLocKey => "LOADOUT-FATAL_FEED.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        decimal amount = GetTotalAmount(card, AmountVar, fatalCount);
        return amount <= 0m
            ? Task.CompletedTask
            : CreatureCmd.GainMaxHp(card.Owner.Creature, amount);
    }
}
