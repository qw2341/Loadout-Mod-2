#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class BasicForgeKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicForge";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_FORGE",
                (name, value) => new ForgeVar(name, decimal.ToInt32(value)))
        ];

    public static BasicForgeKeyword Instance { get; } = new();

    private BasicForgeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicForge;

    public override string StorageKey => LoadoutKeywords.BasicForgeKey;

    public override string TitleLocKey => "LOADOUT-BASIC_FORGE.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_FORGE.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override IEnumerable<IHoverTip> GetAdditionalCardHoverTips(
        CardModel card) => HoverTipFactory.FromForge();

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        await ForgeCmd.Forge(amount.BaseValue, card.Owner, card);
    }
}
